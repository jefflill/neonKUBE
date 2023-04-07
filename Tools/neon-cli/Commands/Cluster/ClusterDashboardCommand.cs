﻿//-----------------------------------------------------------------------------
// FILE:	    ClusterDashboardCommand.cs
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
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Newtonsoft;
using Newtonsoft.Json;

using Neon.Common;
using Neon.Kube;
using Neon.Kube.Proxy;
using Neon.Kube.Hosting;

namespace NeonCli
{
    /// <summary>
    /// Implements the <b>cluster dashboard</b> command.
    /// </summary>
    [Command]
    public class ClusterDashboardCommand : CommandBase
    {
        private const string usage = @"
Lists the dashboards available for the current cluster or displays a named
dashboard in a browser window.

USAGE:

    neon cluster dashboard         - Lists available dashboard names
    neon cluster dashboard NAME    - Displays the named dashboard
";

        /// <inheritdoc/>
        public override string[] Words => new string[] { "cluster", "dashboard" };

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
            Console.WriteLine();

            var currentContext = KubeHelper.CurrentContext;

            if (currentContext == null)
            {
                Console.Error.WriteLine("*** ERROR: No cluster selected.");
                Program.Exit(1);
            }

            var dashboardName = commandLine.Arguments.ElementAtOrDefault(0);

            using (var cluster = new ClusterProxy(currentContext, new HostingManagerFactory(), cloudMarketplace: false))   // [cloudMarketplace] arg doesn't matter here.
            {
                var dashboards = await cluster.ListClusterDashboardsAsync();

                if (string.IsNullOrEmpty(dashboardName))
                {
                    if (dashboards.Count > 0)
                    {
                        Console.WriteLine("Available Dashboards:");
                        Console.WriteLine("---------------------");
                    }
                    else
                    {
                        Console.WriteLine("*** No dashboards are available.");
                        Console.WriteLine();
                        return;
                    }

                    foreach (var item in dashboards
                        .OrderBy(item => item.Key, StringComparer.CurrentCultureIgnoreCase))
                    {
                        Console.WriteLine(item.Key);
                    }

                    Console.WriteLine();
                }
                else
                {
                    if (dashboards.TryGetValue(dashboardName, out var dashboard))
                    {
                        NeonHelper.OpenPlatformBrowser(dashboard.Spec.Url, newWindow: true);
                    }
                    else
                    {
                        Console.WriteLine($"*** ERROR: Dashboard [{dashboardName}] does not exist.");
                        Program.Exit(1);
                    }
                }
            }
        }
    }
}
