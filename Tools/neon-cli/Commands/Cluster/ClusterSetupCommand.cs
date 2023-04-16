﻿//-----------------------------------------------------------------------------
// FILE:	    ClusterSetupCommand.cs
// CONTRIBUTOR: Jeff Lill
// COPYRIGHT:	Copyright © 2005-2023 by NEONFORGE LLC.  All rights reserved.
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
using Neon.Kube.ClusterDef;
using Neon.Kube.Config;
using Neon.Kube.Setup;
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
        private const string usage = @"
Configures a neonKUBE cluster as described in the cluster definition file.

USAGE: 

    neon cluster setup [OPTIONS] root@CLUSTER-NAME  

OPTIONS:

    --unredacted        - Runs Vault and other commands with potential
                          secrets without redacting logs.  This is useful 
                          for debugging cluster setup  issues.  Do not
                          use for production clusters.

    --max-parallel=#    - Specifies the maximum number of node related operations
                          to perform in parallel.  This defaults to [6].

    --disable-pending   - Disable parallization of setup tasks across steps.
                          This is generally intended for use while debugging
                          cluster setup and may slow down setup substantially.

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

    --quiet             - Only print the currently executing step rather than
                          displaying detailed setup status.

    --check             - Performs development related checks against the cluster
                          after it's been setup.  Note that checking is disabled
                          when [--debug] is specified.

                          NOTE: A non-zero exit code will be returned when this
                                option is specified and one or more chechks fail.

    --use-staged        - Specifies that the private node image should be deployed.
                          Only NEONFORGE maintainers should use this.

    --no-telemetry      - Disables whether telemetry for failed cluster deployment,
                          overriding the NEONKUBE_DISABLE_TELEMETRY environment variable.
                          
";

        /// <inheritdoc/>
        public override string[] Words => new string[] { "cluster", "setup" };

        /// <inheritdoc/>
        public override string[] ExtendedOptions => new string[]
        {
            "--unredacted",
            "--max-parallel",
            "--disable-pending",
            "--force",
            "--upload-charts",
            "--debug",
            "--quiet",
            "--check",
            "--use-staged",
            "--no-telemetry"
        };

        /// <inheritdoc/>
        public override bool NeedsHostingManager => true;

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

            Console.WriteLine();

            // Cluster prepare/setup uses the [ProfileClient] to retrieve secrets and profile values.
            // We need to inject an implementation for [PreprocessReader] so it will be able to
            // perform the lookups.

            NeonHelper.ServiceContainer.AddSingleton<IProfileClient>(new MaintainerProfile());

            var contextName       = KubeContextName.Parse(commandLine.Arguments[0]);
            var context           = KubeHelper.Config.GetCluster(contextName.Cluster);
            var setupState        = KubeSetupState.Load((string)contextName, nullIfMissing: true);
            var unredacted        = commandLine.HasOption("--unredacted");
            var debug             = commandLine.HasOption("--debug");
            var quiet             = commandLine.HasOption("--quiet");
            var check             = commandLine.HasOption("--check");
            var uploadCharts      = commandLine.HasOption("--upload-charts") || debug;
            var maxParallelOption = commandLine.GetOption("--max-parallel", "6");
            var disablePending    = commandLine.HasOption("--disable-pending");
            var useStaged         = commandLine.HasOption("--use-staged");
            var noTelemetry       = commandLine.HasOption("--no-telemetry");

            if (noTelemetry)
            {
                KubeEnv.IsTelemetryDisabled = true;
            }

            if (!int.TryParse(maxParallelOption, out var maxParallel) || maxParallel <= 0)
            {
                Console.Error.WriteLine($"*** ERROR: [--max-parallel={maxParallelOption}] is not valid.");
                Program.Exit(1);
            }

            // Verify that it looks like the cluster has already been prepared.

            if (setupState == null || setupState.DeploymentStatus != ClusterDeploymentStatus.Prepared)
            {
                Console.Error.WriteLine($"*** ERROR: Be sure to prepare the cluster first via: neon cluster prepare...");
                Program.Exit(1);
            }

            // Check for conflicting clusters and remove them when allowed by the user.

            if (context != null && setupState != null && setupState.DeploymentStatus == ClusterDeploymentStatus.Ready)
            {
                if (commandLine.GetOption("--force") == null && !Program.PromptYesNo($"One or more clusters reference [context={context.Name}].  Do you wish to delete these?"))
                {
                    Program.Exit(0);
                }

                // Remove the cluster from the kubeconfig and remove any 
                // contexts that reference it.

                KubeHelper.Config.Clusters.Remove(context);

                var delList = new List<KubeConfigContext>();

                foreach (var existingContext in KubeHelper.Config.Contexts)
                {
                    if (existingContext.Name == existingContext.Name)
                    {
                        delList.Add(existingContext);
                    }
                }

                foreach (var existingContext in delList)
                {
                    KubeHelper.Config.Contexts.Remove(existingContext);
                }

                if (KubeHelper.CurrentContext != null &&
                    KubeHelper.CurrentCluster != null && 
                    KubeHelper.CurrentCluster.ClusterInfo != null && 
                    KubeHelper.CurrentContext.Name == context.Name)
                {
                    KubeHelper.Config.CurrentContext = null;
                }

                KubeHelper.Config.Save();
            }

            // Create and run the cluster setup controller.

            var clusterDefinition = setupState.ClusterDefinition;

            var setupOptions = new SetupClusterOptions()
            {
                MaxParallel          = maxParallel,
                Unredacted           = unredacted,
                DebugMode            = debug,
                UploadCharts         = uploadCharts,
                DisableConsoleOutput = quiet
            };

            if (clusterDefinition.Hosting.Environment == HostingEnvironment.HyperV &&
                clusterDefinition.Hosting.HyperV.NeonDesktopBuiltIn)
            {
                setupOptions.DesktopReadyToGo = true;
            }

            var controller = KubeSetup.CreateClusterSetupController(
                clusterDefinition,
                cloudMarketplace: !useStaged,
                options:          setupOptions);

            controller.DisablePendingTasks = disablePending;

            if (quiet)
            {
                Console.WriteLine($"Configuring: {clusterDefinition.Name}");
                Console.WriteLine();

                controller.StepStarted +=
                    (sender, step) =>
                    {
                        Console.WriteLine($"{step.Number,5}: {step.Label}");
                    };
            }
            else
            {
                controller.StatusChangedEvent +=
                    status =>
                    {
                        status.WriteToConsole();
                    };
            }

            switch (await controller.RunAsync())
            {
                case SetupDisposition.Succeeded:

                    var pendingGroups = controller.GetPendingGroups();

                    if (pendingGroups.Count > 0)
                    {
                        Console.WriteLine();
                        Console.WriteLine($"*** ERROR: [{pendingGroups.Count}] pending task groups have not been awaited:");
                        Console.WriteLine();

                        foreach (var groupName in pendingGroups)
                        {
                            Console.WriteLine($"   {groupName}");
                        }

                        Program.Exit(1);
                    }

                    Console.WriteLine();
                    Console.WriteLine($" [{clusterDefinition.Name}] cluster is ready.");
                    Console.WriteLine();

                    if (check && !debug)
                    {
                        var k8s = new Kubernetes(KubernetesClientConfiguration.BuildConfigFromConfigFile(KubeHelper.KubeConfigPath), new KubernetesRetryHandler());

                        if (!await ClusterChecker.CheckAsync(k8s))
                        {
                            Program.Exit(1);
                        }
                    }

                    Program.Exit(0);
                    break;

                case SetupDisposition.Cancelled:

                    Console.WriteLine();
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
    }
}