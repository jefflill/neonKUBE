﻿//-----------------------------------------------------------------------------
// FILE:	    BareMetalHostingManager.cs
// CONTRIBUTOR: Jeff Lill
// COPYRIGHT:	Copyright (c) 2005-2021 by neonFORGE LLC.  All rights reserved.
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
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.NetworkInformation;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

using Newtonsoft;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

using Renci.SshNet.Common;

using Neon.Collections;
using Neon.Common;
using Neon.Cryptography;
using Neon.IO;
using Neon.Net;
using Neon.SSH;
using Neon.Time;

namespace Neon.Kube
{
    /// <summary>
    /// Manages cluster provisioning directly on (mostly) bare manually provisioned machines or virtual machines.
    /// </summary>
    [HostingProvider(HostingEnvironment.BareMetal)]
    public partial class BareMetalHostingManager : HostingManager
    {
        //---------------------------------------------------------------------
        // Static members

        /// <summary>
        /// Ensures that the assembly hosting this hosting manager is loaded.
        /// </summary>
        public static void Load()
        {
            // We don't have to do anything here because the assembly is loaded
            // as a byproduct of calling this method.
        }

        //---------------------------------------------------------------------
        // Instance members

        private ClusterProxy                    cluster;
        private ISetupController                controller;
        private string                          orgSshPassword;
        private string                          secureSshPassword;
        private Dictionary<string, string>      nodeToPassword;

        /// <summary>
        /// Creates an instance that is only capable of validating the hosting
        /// related options in the cluster definition.
        /// </summary>
        public BareMetalHostingManager()
        {
        }

        /// <summary>
        /// Creates an instance that is capable of provisioning a cluster on manually provisioned
        /// servers or virtual machines.
        /// </summary>
        /// <param name="cluster">The cluster being managed.</param>
        /// <param name="logFolder">
        /// The folder where log files are to be written, otherwise or <c>null</c> or 
        /// empty if logging is disabled.
        /// </param>
        public BareMetalHostingManager(ClusterProxy cluster, string logFolder = null)
        {
            Covenant.Requires<ArgumentNullException>(cluster != null, nameof(cluster));

            cluster.HostingManager = this;

            this.cluster        = cluster;
            this.nodeToPassword = new Dictionary<string, string>();
        }

        /// <inheritdoc/>
        public override void Dispose(bool disposing)
        {
            if (disposing)
            {
                GC.SuppressFinalize(this);
            }
        }

        /// <inheritdoc/>
        public override bool IsProvisionNOP => false;

        /// <inheritdoc/>
        public override HostingEnvironment HostingEnvironment => HostingEnvironment.BareMetal;

        /// <inheritdoc/>
        public override void Validate(ClusterDefinition clusterDefinition)
        {
            Covenant.Requires<ArgumentNullException>(clusterDefinition != null, nameof(clusterDefinition));
        }

        /// <inheritdoc/>
        public override bool GenerateSecurePassword => true;

        /// <inheritdoc/>
        public override async Task<bool> ProvisionAsync(ISetupController controller, string secureSshPassword, string orgSshPassword = null)
        {
            Covenant.Requires<ArgumentNullException>(controller != null, nameof(controller));
            Covenant.Requires<ArgumentNullException>(!string.IsNullOrEmpty(secureSshPassword), nameof(secureSshPassword));
            Covenant.Requires<ArgumentNullException>(!string.IsNullOrEmpty(orgSshPassword), nameof(orgSshPassword));
            Covenant.Assert(cluster != null, $"[{nameof(BareMetalHostingManager)}] was created with the wrong constructor.");

            this.controller        = controller;
            this.secureSshPassword = secureSshPassword;
            this.orgSshPassword    = orgSshPassword;

            await Task.CompletedTask;

            throw new NotImplementedException();
        }

        /// <inheritdoc/>
        public override void AddPostPrepareSteps(SetupController<NodeDefinition> setupController)
        {
        }

        /// <inheritdoc/>
        public override (string Address, int Port) GetSshEndpoint(string nodeName)
        {
            return (Address: cluster.GetNode(nodeName).Address.ToString(), Port: NetworkPorts.SSH);
        }

        /// <summary>
        /// Connects the proxy to the node.
        /// </summary>
        /// <param name="node">The target node.</param>
        private void Connect(NodeSshProxy<NodeDefinition> node)
        {
            // We'll start by using the insecure credentials to connect to the node.
            // It is possible though that a first provisiong run failed the first time
            // and was partially completed with some node passwords being changed to
            // the secure password and with other nodes still having the insecure
            // default password.
            //
            // We don't want to make the operator have to go back and reset the secure
            // passwords on those nodes, so we'll try the secure password if the insecure
            // one fails.
            //
            // Note that we're going to use the [nodeToPassword] dictionary to record
            // the password for the node (by name) so that this will be available when
            // we'll need to pass it to [KubeHelper.InitializeNode()].

            try
            {
                node.UpdateCredentials(SshCredentials.FromUserPassword(KubeConst.SysAdminUser, orgSshPassword));
                node.Connect();

                lock (nodeToPassword)
                {
                    nodeToPassword[node.Name] = orgSshPassword;
                }
            }
            catch (SshProxyException)
            {
                // Fall back to the original password if it is different from the secure one,
                // otherwise rethrow the error.

                if (orgSshPassword == secureSshPassword)
                {
                    throw;
                }

                node.UpdateCredentials(SshCredentials.FromUserPassword(KubeConst.SysAdminUser, secureSshPassword));
                node.Connect();

                lock (nodeToPassword)
                {
                    nodeToPassword[node.Name] = secureSshPassword;
                }
            }
        }

        /// <summary>
        /// Changes the password for the [sysadmin] account on the node to the new
        /// secure password and reconnects the node using the new password.
        /// </summary>
        /// <param name="node">The target node.</param>
        private void SetSecurePassword(NodeSshProxy<NodeDefinition> node)
        {
            node.Status = "setting secure password";

            var script =
$@"
echo '{KubeConst.SysAdminUser}:{secureSshPassword}' | chpasswd
";
            var response = node.SudoCommand(CommandBundle.FromScript(script));

            if (response.ExitCode != 0)
            {
                throw new KubeException($"*** ERROR: Unable to set the strong SSH password [exitcode={response.ExitCode}].");
            }

            // Update the node credentials and then reconnect. 

            node.UpdateCredentials(SshCredentials.FromUserPassword(KubeConst.SysAdminUser, secureSshPassword));
            node.Connect();
        }

        /// <summary>
        /// Inspects the node to determine physical machine capabilities like
        /// processor count, RAM, and primary disk capacity and then sets the
        /// corresponding node labels in the cluster definition.
        /// </summary>
        /// <param name="node">The target node.</param>
        private void DetectLabels(NodeSshProxy<NodeDefinition> node)
        {
            CommandResponse result;

            // Download [/proc/meminfo] and extract the [MemTotal] value (in kB).

            result = node.SudoCommand("cat /proc/meminfo", RunOptions.FaultOnError);

            if (result.ExitCode == 0)
            {
                var memInfo       = result.OutputText;
                var memTotalRegex = new Regex(@"^MemTotal:\s*(?<size>\d+)\s*kB", RegexOptions.Multiline);
                var memMatch      = memTotalRegex.Match(memInfo);

                if (memMatch.Success && long.TryParse(memMatch.Groups["size"].Value, out var memSizeKiB))
                {
                    // Note that the RAM reported by Linux is somewhat less than the
                    // physical RAM installed.

                    node.Metadata.Labels.ComputeRam = (int)(memSizeKiB / 1024);  // Convert KiB --> MiB
                }
            }

            // Download [/proc/cpuinfo] and count the number of processors.

            result = node.SudoCommand("cat /proc/cpuinfo", RunOptions.FaultOnError);

            if (result.ExitCode == 0)
            {
                var cpuInfo          = result.OutputText;
                var processorRegex   = new Regex(@"^processor\s*:\s*\d+", RegexOptions.Multiline);
                var processorMatches = processorRegex.Matches(cpuInfo);

                node.Metadata.Labels.ComputeCores = processorMatches.Count;
            }

            // Determine the primary disk size.

            // $hack(jefflill):
            //
            // I'm not entirely sure how to determine which block device is hosting
            // the primary file system for all systems.  For now, I'm just going to
            // assume that this can be one of:
            //
            //      /dev/sda1
            //      /dev/sda
            //      /dev/xvda1
            //      /dev/xvda
            //
            // I'll try each of these in order and setting the label for the
            // first reasonable result we get back.

            var blockDevices = new string[]
                {
                    "/dev/sda1",
                    "/dev/sda",
                    "/dev/xvda1",
                    "/dev/xvda"
                };

            foreach (var blockDevice in blockDevices)
            {
                result = node.SudoCommand($"lsblk -b --output SIZE -n -d {blockDevice}", RunOptions.LogOutput | RunOptions.FaultOnError);

                if (result.ExitCode == 0)
                {
                    if (long.TryParse(result.OutputText.Trim(), out var deviceSize) && deviceSize > 0)
                    {
                        node.Metadata.Labels.StorageSize = ByteUnits.ToGiB(deviceSize);
                        break;
                    }
                }
            }
        }

        /// <inheritdoc/>
        public override string GetDataDisk(LinuxSshProxy node)
        {
            Covenant.Requires<ArgumentNullException>(node != null, nameof(node));

            var unpartitonedDisks = node.ListUnpartitionedDisks();

            if (unpartitonedDisks.Count() == 0)
            {
                return "PRIMARY";
            }

            Covenant.Assert(unpartitonedDisks.Count() == 1, "VMs are assumed to have no more than one attached data disk.");

            return unpartitonedDisks.Single();
        }
    }
}
