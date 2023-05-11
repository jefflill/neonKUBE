//-----------------------------------------------------------------------------
// FILE:	    ClusterPrepareCommand.cs
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
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.Extensions.DependencyInjection;

using Neon.Collections;
using Neon.Common;
using Neon.Cryptography;
using Neon.Deployment;
using Neon.IO;
using Neon.Kube;
using Neon.Kube.ClusterDef;
using Neon.Kube.Hosting;
using Neon.Kube.Proxy;
using Neon.Kube.Setup;
using Neon.Net;
using Neon.SSH;

namespace NeonCli
{
    /// <summary>
    /// Implements the <b>cluster prepare</b> command.
    /// </summary>
    [Command]
    public class ClusterPrepareCommand : CommandBase
    {
        private const string usage = @"
Provisions local and/or cloud infrastructure required to host a neonKUBE cluster.
This includes provisioning networks, load balancers, virtual machines, etc.  Once
the infrastructure is ready, you'll use the [neon cluster setup ...] command to
actually configure the cluster.

USAGE:

    neon cluster prepare [OPTIONS] CLUSTER-DEF

ARGUMENTS:

    CLUSTER-DEF     - Path to the cluster definition file or ""WSL2"" to deploy
                      the standard neonKUBE WSL2 distribution.

OPTIONS:

    --node-image-uri            - Overrides the default node image URI.

                                  NOTE: This is ignored for [--debug] mode.
                                  NOTE: This is ignored when [--node-image-path] is present.

    --node-image-path=PATH      - Uses the node image at the PATH specified rather than
                                  downloading the node image from GitHub Releases.  This
                                  is useful for debugging node image changes.

    --package-caches=HOST:PORT  - Optionally specifies one or more APT Package cache
                                  servers by hostname and port for use by the new cluster. 
                                  Specify multiple servers by separating the endpoints 
                                  with spaces.

    --unredacted                - Runs commands with potential secrets without 
                                  redacting logs.  This is useful for debugging 
                                  cluster setup issues.  Do not use for production
                                  clusters.

    --max-parallel=#            - Specifies the maximum number of node related operations
                                  to perform in parallel.  This defaults to [6].

    --disable-pending           - Disable parallization of setup tasks across steps.
                                  This is generally intended for use while debugging
                                  cluster setup and may slow down setup substantially.

    --remove-templates          - Removes any cached local virtual machine 
                                  templates without actually setting up a 
                                  cluster.  You can use this to ensure that 
                                  cluster will be created from the most recent
                                  template.

    --debug                     - Implements cluster setup from the base rather
                                  than the node image.  This mode is useful while
                                  developing and debugging cluster setup.  This
                                  implies [--upload-charts].

                                  NOTE: This mode is not supported for cloud and
                                        bare-metal environments.

    --quiet                     - Only print the currently executing step rather than
                                  displaying detailed setup status.

    --base-image-name           - Specifies the base image name to use when operating
                                  in [--debug] mode.  This will be the name of the base
                                  image file as published to our public S3 bucket for
                                  the target hosting manager.  Examples:

                                        Hyper-V:   ubuntu-22.04.hyperv.vhdx
                                        XenServer: ubuntu-22.04.xenserver.xva

                                  NOTE: This is required for [--debug]

    --use-staged[=branch]       - MAINTAINERS ONLY: Specifies that the staged image 
                                  should be used as opposed to the public release image.

                                  [--use-staged] by itself will prepare the cluster using
                                  the staged neonKUBE node image whose version is a 
                                  combination of the neonKUBE version along with the 
                                  name of the neonKUBE branch when the libraries were
                                  built.

                                  [--use-staged=branch] allows you to override the branch
                                  so you can base your cluster off of a specific image
                                  build.

";

        /// <inheritdoc/>
        public override string[] Words => new string[] { "cluster", "prepare" };

        /// <inheritdoc/>
        public override string[] ExtendedOptions => new string[] 
        { 
            "--node-image-uri", 
            "--node-image-path",
            "--package-caches",
            "--unredacted", 
            "--max-parallel", 
            "--disable-pending", 
            "--remove-templates", 
            "--debug",
            "--quiet",
            "--base-image-name",
            "--use-staged"
        };

        /// <inheritdoc/>
        public override bool NeedsSshCredentials(CommandLine commandLine) => !commandLine.HasOption("--remove-templates");

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
            if (commandLine.HasHelpOption)
            {
                Help();
                Program.Exit(0);
            }

            Console.WriteLine();

            // Cluster prepare/setup uses the [ProfileClient] to retrieve secrets and profile values.
            // We need to inject an implementation for [PreprocessReader] so it will be able to
            // perform the lookups.

            NeonHelper.ServiceContainer.AddSingleton<IProfileClient>(new MaintainerProfile());

            // Handle the [--remove-templates] option.

            if (commandLine.HasOption("--remove-templates"))
            {
                Console.WriteLine("Removing cached virtual machine templates.");

                foreach (var fileName in Directory.GetFiles(KubeHelper.VmImageFolder, "*.*", SearchOption.TopDirectoryOnly))
                {
                    File.Delete(fileName);
                }
            }
           
            var nodeImageUri      = commandLine.GetOption("--node-image-uri");
            var nodeImagePath     = commandLine.GetOption("--node-image-path");
            var debug             = commandLine.HasOption("--debug");
            var quiet             = commandLine.HasOption("--quiet");
            var baseImageName     = commandLine.GetOption("--base-image-name");
            var maxParallelOption = commandLine.GetOption("--max-parallel", "6");
            var disablePending    = commandLine.HasOption("--disable-pending");
            var useStaged         = commandLine.HasOption("--use-staged");
            var stageBranch       = commandLine.GetOption("--use-staged", KubeVersions.BuildBranch);

            if (useStaged && string.IsNullOrEmpty(stageBranch))
            {
                stageBranch = KubeVersions.BuildBranch;
            }

            if (!int.TryParse(maxParallelOption, out var maxParallel) || maxParallel <= 0)
            {
                Console.Error.WriteLine($"*** ERROR: [--max-parallel={maxParallelOption}] is not valid.");
                Program.Exit(1);
            }

            if (debug && string.IsNullOrEmpty(baseImageName))
            {
                Console.Error.WriteLine($"*** ERROR: [--base-image-name] is required for [--debug] mode.");
                Program.Exit(1);
            }

            // Implement the command.

            if (KubeHelper.CurrentContext != null)
            {
                Console.Error.WriteLine("*** ERROR: You are logged into a cluster.  You need to logout before preparing another.");
                Program.Exit(1);
            }

            if (commandLine.Arguments.Length == 0)
            {
                Console.Error.WriteLine($"*** ERROR: CLUSTER-DEF expected.");
                Program.Exit(1);
            }

            // Load the cluster definition.

            var clusterDefPath    = commandLine.Arguments[0];
            var clusterDefinition = (ClusterDefinition)null;            

            ClusterDefinition.ValidateFile(clusterDefPath, strict: true);

            clusterDefinition = ClusterDefinition.FromFile(clusterDefPath, strict: true);

            // Do a quick sanity check to ensure that the hosting environment has no conflicts
            // as well as enough resources (memory, disk,...) to actually host the cluster.

            var setupState = new KubeSetupState() { ClusterDefinition = clusterDefinition };

            using (var cluster = ClusterProxy.Create(setupState, new HostingManagerFactory(), !useStaged))
            {
                var status = await cluster.GetResourceAvailabilityAsync();

                if (!status.CanBeDeployed)
                {
                    Console.Error.WriteLine();
                    Console.Error.WriteLine($"*** ERROR: Cannot deploy the cluster due to conflicts or resource constraints.");
                    Console.Error.WriteLine();

                    foreach (var entity in status.Constraints.Keys
                        .OrderBy(key => key, StringComparer.InvariantCultureIgnoreCase))
                    {
                        Console.Error.WriteLine();
                        Console.Error.WriteLine($"{entity}:");

                        foreach (var constraint in status.Constraints[entity])
                        {
                            Console.Error.WriteLine($"    {constraint.ResourceType.ToString().ToUpperInvariant()}: {constraint.Details}");
                        }
                    }

                    Console.Error.WriteLine();
                    Program.Exit(1);
                }
            }

            if (KubeHelper.IsOnPremiseHypervisorEnvironment(clusterDefinition.Hosting.Environment))
            {
                // Use the default node image for the hosting environment unless [--node-image-uri]
                // or [--node-image-path] was specified.

                if (string.IsNullOrEmpty(nodeImageUri) && string.IsNullOrEmpty(nodeImagePath))
                {
                    nodeImageUri = await KubeDownloads.GetNodeImageUriAsync(clusterDefinition.Hosting.Environment, stageBranch: stageBranch);
                }
            }

            // Parse any specified package cache endpoints.

            var packageCaches         = commandLine.GetOption("--package-caches", null);
            var packageCacheEndpoints = new List<IPEndPoint>();

            if (!string.IsNullOrEmpty(packageCaches))
            {
                foreach (var item in packageCaches.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                {
                    if (!NetHelper.TryParseIPv4Endpoint(item, out var endpoint))
                    {
                        Console.Error.WriteLine($"*** ERROR: [{item}] is not a valid package cache IPv4 endpoint.");
                        Program.Exit(1);
                    }

                    packageCacheEndpoints.Add(endpoint);
                }
            }

            // Create and run the cluster prepare controller.

            var prepareOptions = new PrepareClusterOptions()
            {
                NodeImageUri          = nodeImageUri,
                NodeImagePath         = nodeImagePath,
                MaxParallel           = maxParallel,
                PackageCacheEndpoints = packageCacheEndpoints,
                Unredacted            = commandLine.HasOption("--unredacted"),
                DebugMode             = debug,
                BaseImageName         = baseImageName,
                DisableConsoleOutput  = quiet
            };

            if (clusterDefinition.Hosting.Environment == HostingEnvironment.HyperV &&
                clusterDefinition.Hosting.HyperV.NeonDesktopBuiltIn)
            {
                prepareOptions.DesktopReadyToGo = true;
            }

            var controller = await KubeSetup.CreateClusterPrepareControllerAsync(
                clusterDefinition, 
                cloudMarketplace: !useStaged,
                options:          prepareOptions);

            controller.DisablePendingTasks = disablePending;

            if (quiet)
            {
                Console.WriteLine($"Preparing: {clusterDefinition.Name}");
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
                        Console.Error.WriteLine();
                        Console.Error.WriteLine($"*** ERROR: [{pendingGroups.Count}] pending task groups have not been awaited:");
                        Console.Error.WriteLine();

                        foreach (var groupName in pendingGroups)
                        {
                            Console.Error.WriteLine($"   {groupName}");
                        }

                        Program.Exit(1);
                    }

                    Console.WriteLine();
                    Console.WriteLine($" [{clusterDefinition.Name}] cluster is prepared.");
                    Console.WriteLine();
                    Program.Exit(0);
                    break;

                case SetupDisposition.Cancelled:

                    Console.Error.WriteLine();
                    Console.Error.WriteLine(" *** CANCELLED: Cluster prepare was cancelled.");
                    Console.Error.WriteLine();
                    Program.Exit(1);
                    break;

                case SetupDisposition.Failed:

                    Console.Error.WriteLine();
                    Console.Error.WriteLine(" *** ERROR: Cluster prepare has failed.  Examine the logs here:");
                    Console.Error.WriteLine();
                    Console.Error.WriteLine($" {KubeHelper.LogFolder}");
                    Console.Error.WriteLine();
                    Program.Exit(1);
                    break;

                default:

                    throw new NotImplementedException();
            }

            await Task.CompletedTask;
        }
    }
}
