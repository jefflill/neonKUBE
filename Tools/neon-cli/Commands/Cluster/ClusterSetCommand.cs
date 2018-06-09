﻿//-----------------------------------------------------------------------------
// FILE:	    ClusterSetCommand.cs
// CONTRIBUTOR: Jeff Lill
// COPYRIGHT:	Copyright (c) 2016-2018 by neonFORGE, LLC.  All rights reserved.

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

using Neon.Cluster;
using Neon.Common;

namespace NeonCli
{
    /// <summary>
    /// Implements the <b>cluster set</b> command.
    /// </summary>
    public class ClusterSetCommand : CommandBase
    {
        private const string usage = @"
Modifies a global cluster setting.

USAGE:

    neon cluster set [--no-verify] SETTING=VALUE

ARGUMENTS:

    SETTING         - identifies the setting
    VALUE           - the value to set

OPTIONS:

    --no-verify     - 

SETTINGS:

    allow-unit-testing      - enable ClusterFixture unit testing (bool)
    disable-auto-unseal     - disables automatic Vault unsealing (bool)
    log-retention-days      - number of days of logs to keep
";
        /// <inheritdoc/>
        public override string[] Words
        {
            get { return new string[] { "cluster", "set" }; }
        }

        /// <inheritdoc/>
        public override void Help()
        {
            Console.WriteLine(usage);
        }

        /// <inheritdoc/>
        public override void Run(CommandLine commandLine)
        {
            if (commandLine.HasHelpOption)
            {
                Console.WriteLine(usage);
                Program.Exit(0);
            }

            var clusterLogin = Program.ConnectCluster();
            var cluster      = new ClusterProxy(clusterLogin);

            if (commandLine.Arguments.Length != 1)
            {
                Console.Error.WriteLine("*** ERROR: SETTING=VALUE expected.");
                Console.Error.WriteLine();
                Console.Error.WriteLine(usage);
                Program.Exit(1);
            }

            var assignment = commandLine.Arguments[0];
            var fields     = assignment.Split(new char[] { '=' }, 2);

            if (fields.Length != 2)
            {
                Console.Error.WriteLine("*** ERROR: SETTING=VALUE expected.");
                Console.Error.WriteLine();
                Console.Error.WriteLine(usage);
                Program.Exit(1);
            }

            var setting = fields[0].ToLowerInvariant();
            var value   = fields[1];

            switch (setting)
            {
                case NeonClusterGlobals.UserAllowUnitTesting:

                    cluster.Globals.Set(NeonClusterGlobals.UserAllowUnitTesting, NeonHelper.ParseBool(value));
                    break;

                case NeonClusterGlobals.UserDisableAutoUnseal:

                    cluster.Globals.Set(NeonClusterGlobals.UserDisableAutoUnseal, NeonHelper.ParseBool(value));
                    break;

                case NeonClusterGlobals.UserLogRetentionDays:

                    if (int.TryParse(value, out var logRetentionDays) || logRetentionDays <= 0)
                    {
                        Console.Error.WriteLine($"*** ERROR: [{value}] is not a positive number of days.");
                        Program.Exit(1);
                    }

                    cluster.Globals.Set(NeonClusterGlobals.UserLogRetentionDays, NeonHelper.ParseBool(value));
                    break;

                default:

                    Console.Error.WriteLine($"*** ERROR: [{setting}] is not a valid cluster setting.");
                    Program.Exit(1);
                    break;
            }

            Console.WriteLine();
            Console.WriteLine($"* updated: {setting}");
        }

        /// <inheritdoc/>
        public override DockerShimInfo Shim(DockerShim shim)
        {
            return new DockerShimInfo(isShimmed: true, ensureConnection: true);
        }
    }
}
