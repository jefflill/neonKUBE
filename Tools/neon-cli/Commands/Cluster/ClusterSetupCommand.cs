﻿//-----------------------------------------------------------------------------
// FILE:	    ClusterSetupCommand.cs
// CONTRIBUTOR: Jeff Lill
// COPYRIGHT:	Copyright (c) 2005-2022 by neonFORGE LLC.  All rights reserved.
//
// Licensed under the Apache License, Version 2.0 (the "License");
// you may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//
//     http://www.apache.org/licenses/LICENSE-2.0
//
// Unless required by applicable law or agreed to in writing, software
// distributed under the License is distributed on an "AS IS" BASIS,
// WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
// See the License for the specific language governing permissions and
// limitations under the License.

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Diagnostics.Contracts;
using System.Dynamic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

using k8s;
using k8s.Models;

using Microsoft.Extensions.DependencyInjection;

using Neon.Common;
using Neon.Cryptography;
using Neon.Deployment;
using Neon.IO;
using Neon.Kube;
using Neon.Net;
using Neon.Retry;
using Neon.SSH;
using Neon.Time;

namespace NeonCli
{
    /// <summary>
    /// Implements the <b>cluster setup</b> command.
    /// </summary>
    [Command]
    public class ClusterSetupCommand : CommandBase
    {
        //---------------------------------------------------------------------
        // Implementation

        private const string usage = @"
Configures a neonKUBE cluster as described in the cluster definition file.

USAGE: 

    neon cluster setup [OPTIONS] root@CLUSTER-NAME  

OPTIONS:

    --unredacted        - Runs Vault and other commands with potential
                          secrets without redacting logs.  This is useful 
                          for debugging cluster setup  issues.  Do not
                          use for production clusters.

    --max-parallel=#            - Specifies the maximum number of node related operations
                                  to perform in parallel.  This defaults to [6].

    --force             - Don't prompt before removing existing contexts
                          that reference the target cluster.

    --upload-charts     - Upload helm charts to node before setup. This
                          is useful when debugging.

    --debug             - Implements cluster setup from the base rather
                          than the node image.  This mode is useful while
                          developing and debugging cluster setup.  This
                          implies [--upload-charts].

                          NOTE: This mode is not supported for cloud and
                                bare-metal environments.

    --check             - Performs development related checks against the cluster
                          after it's been setup.  Note that checking is disabled
                          when [--debug] is specified.

    --automation-folder - Indicates that the command must not impact normal clusters
                          by changing the current login, Kubernetes config or
                          other files like cluster deployment logs.  This is
                          used for automated CI/CD or unit test cluster deployments 
                          while not disrupting the built-in neonDESKTOP or
                          other normal clusters.
";

        private KubeConfigContext   kubeContext;
        private ClusterLogin        clusterLogin;

        /// <inheritdoc/>
        public override string[] Words => new string[] { "cluster", "setup" };

        /// <inheritdoc/>
        public override string[] ExtendedOptions => new string[] { "--unredacted", "--max-parallel", "--force", "--upload-charts", "--debug", "--check", "--automation-folder" };

        /// <inheritdoc/>
        public override void Help()
        {
            Console.WriteLine(usage);
        }

        /// <inheritdoc/>
        public override async Task RunAsync(CommandLine commandLine)
        {
            if (commandLine.Arguments.Length < 1)
            {
                Console.Error.WriteLine("*** ERROR: [root@CLUSTER-NAME] argument is required.");
                Program.Exit(1);
            }

            // Cluster prepare/setup uses the [ProfileClient] to retrieve secrets and profile values.
            // We need to inject an implementation for [PreprocessReader] so it will be able to
            // perform the lookups.

            NeonHelper.ServiceContainer.AddSingleton<IProfileClient>(new ProfileClient());

            var contextName       = KubeContextName.Parse(commandLine.Arguments[0]);
            var kubeCluster       = KubeHelper.Config.GetCluster(contextName.Cluster);
            var unredacted        = commandLine.HasOption("--unredacted");
            var debug             = commandLine.HasOption("--debug");
            var check             = commandLine.HasOption("--check");
            var uploadCharts      = commandLine.HasOption("--upload-charts") || debug;
            var automationFolder  = commandLine.GetOption("--automation-folder");
            var maxParallelOption = commandLine.GetOption("--max-parallel", "6");

            if (!int.TryParse(maxParallelOption, out var maxParallel) || maxParallel <= 0)
            {
                Console.Error.WriteLine($"*** ERROR: [--max-parallel={maxParallelOption}] is not valid.");
                Program.Exit(1);
            }

            clusterLogin = KubeHelper.GetClusterLogin(contextName);

            if (clusterLogin == null)
            {
                Console.Error.WriteLine($"*** ERROR: Be sure to prepare the cluster first via [neon cluster prepare...].");
                Program.Exit(1);
            }

            if (string.IsNullOrEmpty(clusterLogin.SshPassword))
            {
                Console.Error.WriteLine($"*** ERROR: No cluster node SSH password found.");
                Program.Exit(1);
            }

            if (kubeCluster != null && !clusterLogin.SetupDetails.SetupPending)
            {
                if (commandLine.GetOption("--force") == null && !Program.PromptYesNo($"One or more logins reference [{kubeCluster.Name}].  Do you wish to delete these?"))
                {
                    Program.Exit(0);
                }

                // Remove the cluster from the kubeconfig and remove any 
                // contexts that reference it.

                KubeHelper.Config.Clusters.Remove(kubeCluster);

                var delList = new List<KubeConfigContext>();

                foreach (var context in KubeHelper.Config.Contexts)
                {
                    if (context.Properties.Cluster == kubeCluster.Name)
                    {
                        delList.Add(context);
                    }
                }

                foreach (var context in delList)
                {
                    KubeHelper.Config.Contexts.Remove(context);
                }

                if (KubeHelper.CurrentContext != null && KubeHelper.CurrentContext.Properties.Cluster == kubeCluster.Name)
                {
                    KubeHelper.Config.CurrentContext = null;
                }

                KubeHelper.Config.Save();
            }

            kubeContext = new KubeConfigContext(contextName);

            KubeHelper.InitContext(kubeContext);

            // Create and run the cluster setup controller.

            var clusterDefinition = clusterLogin.ClusterDefinition;

#if ENTERPRISE
            if (clusterDefinition.Hosting.Environment == HostingEnvironment.Wsl2)
            {
                var distro = new Wsl2ExtendedProxy(KubeConst.NeonDesktopWsl2BuiltInDistroName, KubeConst.SysAdminUser);

                clusterDefinition.Masters.FirstOrDefault().Address = distro.Address;
            }
#endif

            var controller = KubeSetup.CreateClusterSetupController(
                clusterDefinition,
                maxParallel:        maxParallel,
                unredacted:         unredacted,
                debugMode:          debug,
                uploadCharts:       uploadCharts,
                automationFolder:   automationFolder);

            controller.StatusChangedEvent +=
                status =>
                {
                    status.WriteToConsole();
                };

            switch (await controller.RunAsync())
            {
                case SetupDisposition.Succeeded:

                    Console.WriteLine();
                    Console.WriteLine($" [{clusterDefinition.Name}] cluster is ready.");
                    Console.WriteLine();

                    if (check && !debug)
                    {
                        await CheckAsync();
                    }

                    Program.Exit(0);
                    break;

                case SetupDisposition.Cancelled:

                    Console.WriteLine(" *** CANCELLED: Cluster setup was cancelled.");
                    Console.WriteLine();
                    Console.WriteLine();
                    Program.Exit(1);
                    break;

                case SetupDisposition.Failed:

                    Console.WriteLine();
                    Console.WriteLine(" *** ERROR: Cluster setup failed.  Examine the logs here:");
                    Console.WriteLine();
                    Console.WriteLine($" {KubeHelper.LogFolder}");
                    Console.WriteLine();
                    Program.Exit(1);
                    break;

                default:

                    throw new NotImplementedException();
            }

            await Task.CompletedTask;
        }

        /// <summary>
        /// Performs development related cluster checks.
        /// </summary>
        private async Task CheckAsync()
        {
            var k8s = new Kubernetes(KubernetesClientConfiguration.BuildConfigFromConfigFile());

            await CheckContainerImagesAsync(k8s);
        }

        /// <summary>
        /// Verifies that all of the container images loaded on the pods are specified in the
        /// container manifest.  Any images that aren't in the manifest need to be preloaded
        /// into the node image.
        /// </summary>
        /// <param name="k8s">The cluster's Kubernertes client.</param>
        /// <returns>The tracking <see cref="Task"/>.</returns>
        private async Task CheckContainerImagesAsync(Kubernetes k8s)
        {
            Covenant.Requires<ArgumentNullException>(k8s != null, nameof(k8s));

            Console.Error.WriteLine("* Checking container images");

            var installedImages = new HashSet<string>(StringComparer.InvariantCultureIgnoreCase);

            foreach (var image in KubeSetup.ClusterManifest.ContainerImages)
            {
                installedImages.Add(image.SourceRef);
            }

            var nodes      = await k8s.ListNodeAsync();
            var badImages  = new List<string>();
            var sbBadImage = new StringBuilder();

            foreach (var node in nodes.Items)
            {
                foreach (var image in node.Status.Images)
                {
                    var found = false;

                    foreach (var name in image.Names)
                    {
                        if (installedImages.Contains(name))
                        {
                            found = true;
                            break;
                        }
                    }

                    if (!found)
                    {
                        sbBadImage.Clear();

                        foreach (var name in image.Names.OrderBy(name => name, StringComparer.InvariantCultureIgnoreCase))
                        {
                            sbBadImage.AppendWithSeparator(name, ", ");
                        }

                        badImages.Add(sbBadImage.ToString());
                    }
                }
            }

            if (badImages.Count > 0)
            {
                Console.Error.WriteLine();
                Console.Error.WriteLine($"WARNING!");
                Console.Error.WriteLine($"========");
                Console.Error.WriteLine($"[{badImages.Count}] container images are present in cluster without being included");
                Console.Error.WriteLine($"in the cluster manifest.  These images need to be added to the node image.");
                Console.Error.WriteLine();

                foreach (var badImage in badImages)
                {
                    Console.Error.WriteLine(badImage);
                }
            }
        }
    }
}