﻿//-----------------------------------------------------------------------------
// FILE:	    KubeSetup.cs
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
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.Win32;

using Couchbase;
using Newtonsoft.Json;

using k8s;
using k8s.Models;

using Neon.Common;
using Neon.Data;
using Neon.Diagnostics;
using Neon.IO;
using Neon.Net;
using Neon.Retry;
using Neon.SSH;
using Neon.Windows;
using Neon.Cryptography;

namespace Neon.Kube
{
    /// <summary>
    /// Node and cluster configuration/setup methods.
    /// </summary>
    public static class KubeSetup
    {
        /// <summary>
        /// Customizes the OpenSSH configuration on a node.
        /// </summary>
        /// <param name="node">The target node.</param>
        public static void ConfigureOpenSsh(NodeSshProxy<NodeDefinition> node)
        {
            // Upload the OpenSSH server configuration and restart OpenSSH.

            node.UploadText("/etc/ssh/sshd_config", KubeHelper.OpenSshConfig);
            node.SudoCommand("systemctl restart sshd");
        }

        /// <summary>
        /// Configures the global environment variables that describe the configuration 
        /// of the server within the cluster.
        /// </summary>
        /// <param name="node">The server to be updated.</param>
        /// <param name="clusterDefinition">The cluster definition.</param>
        public static void ConfigureEnvironmentVariables(NodeSshProxy<NodeDefinition> node, ClusterDefinition clusterDefinition)
        {
            node.Status = "environment variables";

            // We're going to append the new variables to the existing Linux [/etc/environment] file.

            var sb = new StringBuilder();

            // Append all of the existing environment variables except for those
            // whose names start with "NEON_" to make the operation idempotent.
            //
            // Note that we're going to special case PATH to add any Neon
            // related directories.

            using (var currentEnvironmentStream = new MemoryStream())
            {
                node.Download("/etc/environment", currentEnvironmentStream);

                currentEnvironmentStream.Position = 0;

                using (var reader = new StreamReader(currentEnvironmentStream))
                {
                    foreach (var line in reader.Lines())
                    {
                        if (line.StartsWith("PATH="))
                        {
                            if (!line.Contains(KubeNodeFolders.Bin))
                            {
                                sb.AppendLine(line + $":/snap/bin:{KubeNodeFolders.Bin}");
                            }
                            else
                            {
                                sb.AppendLine(line);
                            }
                        }
                        else if (!line.StartsWith("NEON_"))
                        {
                            sb.AppendLine(line);
                        }
                    }
                }
            }

            // Add the global cluster related environment variables. 

            sb.AppendLine($"NEON_CLUSTER={clusterDefinition.Name}");
            sb.AppendLine($"NEON_DATACENTER={clusterDefinition.Datacenter.ToLowerInvariant()}");
            sb.AppendLine($"NEON_ENVIRONMENT={clusterDefinition.Environment.ToString().ToLowerInvariant()}");

            var sbPackageProxies = new StringBuilder();

            if (clusterDefinition.PackageProxy != null)
            {
                foreach (var proxyEndpoint in clusterDefinition.PackageProxy.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                {
                    sbPackageProxies.AppendWithSeparator(proxyEndpoint);
                }
            }
            
            sb.AppendLine($"NEON_PACKAGE_PROXY={sbPackageProxies}");

            if (clusterDefinition.Hosting != null)
            {
                sb.AppendLine($"NEON_HOSTING={clusterDefinition.Hosting.Environment.ToMemberString().ToLowerInvariant()}");
            }

            sb.AppendLine($"NEON_NODE_NAME={node.Name}");

            if (node.Metadata != null)
            {
                sb.AppendLine($"NEON_NODE_ROLE={node.Metadata.Role}");
                sb.AppendLine($"NEON_NODE_IP={node.Metadata.Address}");
                sb.AppendLine($"NEON_NODE_HDD={node.Metadata.Labels.StorageHDD.ToString().ToLowerInvariant()}");
            }

            sb.AppendLine($"NEON_BIN_FOLDER={KubeNodeFolders.Bin}");
            sb.AppendLine($"NEON_CONFIG_FOLDER={KubeNodeFolders.Config}");
            sb.AppendLine($"NEON_SETUP_FOLDER={KubeNodeFolders.Setup}");
            sb.AppendLine($"NEON_STATE_FOLDER={KubeNodeFolders.State}");
            sb.AppendLine($"NEON_TMPFS_FOLDER={KubeNodeFolders.Tmpfs}");

            // Kubernetes related variables for masters.

            if (node.Metadata.IsMaster)
            {
                sb.AppendLine($"KUBECONFIG=/etc/kubernetes/admin.conf");
            }

            // Upload the new environment to the server.

            node.UploadText("/etc/environment", sb, tabStop: 4);
        }

        /// <summary>
        /// Configures a node's host public SSH key during node provisioning.
        /// </summary>
        /// <param name="node">The target node.</param>
        /// <param name="clusterLogin">The cluster login.</param>
        public static void ConfigureSshKey(NodeSshProxy<NodeDefinition> node, ClusterLogin clusterLogin)
        {
            Covenant.Requires<ArgumentNullException>(node != null, nameof(node));
            Covenant.Requires<ArgumentNullException>(clusterLogin != null, nameof(clusterLogin));

            // Configure the SSH credentials on the node.

            node.InvokeIdempotentAction("setup/ssh",
                () =>
                {
                    CommandBundle bundle;

                    // Here's some information explaining what how this works:
                    //
                    //      https://help.ubuntu.com/community/SSH/OpenSSH/Configuring
                    //      https://help.ubuntu.com/community/SSH/OpenSSH/Keys

                    node.Status = "setup: client SSH key";

                    // Enable the public key by appending it to [$HOME/.ssh/authorized_keys],
                    // creating the file if necessary.  Note that we're allowing only a single
                    // authorized key.

                    var addKeyScript =
$@"
chmod go-w ~/
mkdir -p $HOME/.ssh
chmod 700 $HOME/.ssh
touch $HOME/.ssh/authorized_keys
cat ssh-key.ssh2 > $HOME/.ssh/authorized_keys
chmod 600 $HOME/.ssh/authorized_keys
";
                    bundle = new CommandBundle("./addkeys.sh");

                    bundle.AddFile("addkeys.sh", addKeyScript, isExecutable: true);
                    bundle.AddFile("ssh_host_rsa_key", clusterLogin.SshKey.PublicSSH2);

                    // NOTE: I'm explicitly not running the bundle as [sudo] because the OpenSSH
                    //       server is very picky about the permissions on the user's [$HOME]
                    //       and [$HOME/.ssl] folder and contents.  This took me a couple 
                    //       hours to figure out.

                    node.RunCommand(bundle);

                    // These steps are required for both password and public key authentication.

                    // Upload the server key and edit the [sshd] config to disable all host keys 
                    // except for RSA.

                    var configScript =
$@"
# Install public SSH key for the [sysadmin] user.

cp ssh_host_rsa_key.pub /home/{KubeConst.SysAdminUser}/.ssh/authorized_keys

# Disable all host keys except for RSA.

sed -i 's!^\HostKey /etc/ssh/ssh_host_dsa_key$!#HostKey /etc/ssh/ssh_host_dsa_key!g' /etc/ssh/sshd_config
sed -i 's!^\HostKey /etc/ssh/ssh_host_ecdsa_key$!#HostKey /etc/ssh/ssh_host_ecdsa_key!g' /etc/ssh/sshd_config
sed -i 's!^\HostKey /etc/ssh/ssh_host_ed25519_key$!#HostKey /etc/ssh/ssh_host_ed25519_key!g' /etc/ssh/sshd_config

# Restart SSHD to pick up the changes.

systemctl restart sshd
";
                    bundle = new CommandBundle("./config.sh");

                    bundle.AddFile("config.sh", configScript, isExecutable: true);
                    bundle.AddFile("ssh_host_rsa_key.pub", clusterLogin.SshKey.PublicPUB);
                    node.SudoCommand(bundle);
                });

            // Verify that we can login with the new SSH private key and also verify that
            // the password still works.

            node.Status = "ssh: verify private key auth";
            node.Disconnect();
            node.UpdateCredentials(SshCredentials.FromPrivateKey(KubeConst.SysAdminUser, clusterLogin.SshKey.PrivatePEM));
            node.WaitForBoot();

            node.Status = "ssh: verify password auth";
            node.Disconnect();
            node.UpdateCredentials(SshCredentials.FromUserPassword(KubeConst.SysAdminUser, clusterLogin.SshPassword));
            node.WaitForBoot();
        }

        /// <summary>
        /// Configures the APY package manager.
        /// </summary>
        /// <param name="node">The target node.</param>
        /// <param name="packageManagerRetries">Optionally specifies the packager manager retries (defaults to <b>5</b>).</param>
        /// <param name="allowPackageManagerIPv6">Optionally prevent the package manager from using IPv6 (defaults to <c>false</c>.</param>
        public static void ConfigureApt(NodeSshProxy<NodeDefinition> node, int packageManagerRetries = 5, bool allowPackageManagerIPv6 = false)
        {
            node.Status = "configure: [apt] package manager";

            if (!allowPackageManagerIPv6)
            {
                // Restrict the [apt] package manager to using IPv4 to communicate
                // with the package mirrors, since IPv6 doesn't work sometimes.

                node.UploadText("/etc/apt/apt.conf.d/99-force-ipv4-transport", "Acquire::ForceIPv4 \"true\";");
                node.SudoCommand("chmod 644 /etc/apt/apt.conf.d/99-force-ipv4-transport", RunOptions.FaultOnError);
            }

            // Configure [apt] to retry.

            node.UploadText("/etc/apt/apt.conf.d/99-retries", $"APT::Acquire::Retries \"{packageManagerRetries}\";");
            node.SudoCommand("chmod 644 /etc/apt/apt.conf.d/99-retries", RunOptions.FaultOnError);

            // We're going to disable apt updating services so we can control when this happens.

            var disableAptServices =
@"#------------------------------------------------------------------------------
# Disable the [apt-timer] and [apt-daily] services.  We're doing this 
# for two reasons:
#
#   1. These services interfere with with [apt-get] usage during
#      cluster setup and is also likely to interfere with end-user
#      configuration activities as well.
#
#   2. Automatic updates for production and even test clusters is
#      just not a great idea.  You just don't want a random update
#      applied in the middle of the night which might cause trouble.
#
#      We're going to implement our own cluster updating machanism
#      that will be smart enough to update the nodes such that the
#      impact on cluster workloads will be limited.

systemctl stop apt-daily.timer
systemctl mask apt-daily.timer

systemctl stop apt-daily.service
systemctl mask apt-daily.service

# It may be possible for the auto updater to already be running so we'll
# wait here for it to release any lock files it holds.

while fuser /var/{{lib /{{dpkg,apt/lists}},cache/apt/archives}}/lock; do
    sleep 1
done";
            node.SudoCommand(CommandBundle.FromScript(disableAptServices), RunOptions.FaultOnError);
        }

        /// <summary>
        /// Disables the SNAP package manasger.
        /// </summary>
        /// <param name="node">The target node.</param>
        public static void DisableSnap(NodeSshProxy<NodeDefinition> node)
        {
            node.Status = "disable: [snapd.service]";

            var disableSnapScript =
@"# Stop and mask [snapd.service] if it's not already masked.

systemctl status --no-pager snapd.service

if [ $? ]; then
    systemctl stop snapd.service
    systemctl mask snapd.service
fi
";
            node.SudoCommand(CommandBundle.FromScript(disableSnapScript), RunOptions.FaultOnError);
        }

        /// <summary>
        /// Installs the <b>neon-init</b> service which is a cloud-init like service we
        /// use to configure the network and credentials for VMs hosted in non-cloud
        /// hypervisors.
        /// </summary>
        /// <param name="node">The target node.</param>
        /// <remarks>
        /// <para>
        /// Install and configure the [neon-init] service.  This is a simple script
        /// that is configured to run as a oneshot systemd service before networking is
        /// started.  This is currently used to configure the node's static IP address
        /// configuration on first boot, so we don't need to rely on DHCP (which may not
        /// be available in some environments).
        /// </para>
        /// <para>
        /// [neon-init] is intended to run the first time a node is booted after
        /// being created from a template.  It checks to see if a special ISO with a
        /// configuration script named [neon-init.sh] is inserted into the VMs DVD
        /// drive and when present, the script will be executed and the [/etc/neon-init]
        /// file will be created to indicate that the service no longer needs to do this for
        /// subsequent reboots.
        /// </para>
        /// <note>
        /// The script won't create the [/etc/neon-init] when the script ISO doesn't exist 
        /// for debugging purposes.
        /// </note>
        /// </remarks>
        public static void InstallNeonInit(NodeSshProxy<NodeDefinition> node)
        {
            node.Status = "install: [neon-init]";

            var neonNodePrepScript =
$@"# Ensure that the neon binary folder exists.

mkdir -p {KubeNodeFolders.Bin}

# Create the systemd unit file.

cat <<EOF > /etc/systemd/system/neon-init.service

[Unit]
Description=neonKUBE one-time node preparation service 
After=systemd-networkd.service

[Service]
Type=oneshot
ExecStart={KubeNodeFolders.Bin}/neon-init.sh
RemainAfterExit=false
StandardOutput=journal+console

[Install]
WantedBy=multi-user.target
EOF

# Create the service script.

cat <<EOF > {KubeNodeFolders.Bin}/neon-init.sh
#!/bin/bash
#------------------------------------------------------------------------------
# FILE:	        neon-init.sh
# CONTRIBUTOR:  Jeff Lill
# COPYRIGHT:	Copyright (c) 2005-2021 by neonFORGE LLC.  All rights reserved.
#
# Licensed under the Apache License, Version 2.0 (the ""License"");
# you may not use this file except in compliance with the License.
# You may obtain a copy of the License at
#
#     http://www.apache.org/licenses/LICENSE-2.0
#
# Unless required by applicable law or agreed to in writing, software
# distributed under the License is distributed on an ""AS IS"" BASIS,
# WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
# See the License for the specific language governing permissions and
# limitations under the License.
#
# This script is run early during node boot before the netork is configured
# as a poor man's way for neonKUBE cluster setup to configure the network
# without requiring DHCP.  Here's how this works:
#
#       1. neonKUBE cluster setup creates a node VM from a template.
#
#       2. Setup creates a temporary ISO (DVD) image with a script named 
#          [neon-init.sh] on it and uploads this to the Hyper-V
#          or XenServer host machine.
#
#       3. Setup inserts the VFD into the VM's DVD drive and starts the VM.
#
#       4. The VM boots, eventually running this script (via the
#          [neon-init] service).
#
#       5. This script checks whether a DVD is present, mounts
#          it and checks it for the [neon-init.sh] script.
#
#       6. If the DVD and script file are present, this service will
#          execute the script via Bash, peforming any required custom setup.
#          Then this script creates the [/etc/neon-init] file which 
#          prevents the service from doing anything during subsequent node 
#          reboots.
#
#       7. The service just exits if the DVD and/or script file are 
#          not present.  This shouldn't happen in production but is useful
#          for script debugging.

# Run the prep script only once.

if [ -f /etc/neon-init ] ; then
    echo ""INFO: Machine is already prepared.""
    exit 0
fi

# Check for the DVD and prep script.

mkdir -p /media/neon-init

if [ ! $? ] ; then
    echo ""ERROR: Cannot create DVD mount point.""
    rm -rf /media/neon-init
    exit 1
fi

mount /dev/dvd /media/neon-init

if [ ! $? ] ; then
    echo ""WARNING: No DVD is present.""
    rm -rf /media/neon-init
    exit 0
fi

if [ ! -f /media/neon-init/neon-init.sh ] ; then
    echo ""WARNING: No [neon-init.sh] script is present on the DVD.""
    rm -rf /media/neon-init
    exit 0
fi

# The script file is present so execute it.  Note that we're
# passing the path where the DVD is mounted as a parameter.

echo ""INFO: Running [neon-init.sh]""
bash /media/neon-init/neon-init.sh /media/neon-init

# Unmount the DVD and cleanup.

echo ""INFO: Cleanup""
umount /media/neon-init
rm -rf /media/neon-init

# Disable [neon-init] the next time it is started.

touch /etc/neon-init
EOF

chmod 744 {KubeNodeFolders.Bin}/neon-init.sh

# Configure [neon-init] to start at boot.

systemctl enable neon-init
systemctl daemon-reload
";
            node.SudoCommand(CommandBundle.FromScript(neonNodePrepScript), RunOptions.FaultOnError);
        }

        /// <summary>
        /// Initializes a near virgin server with the basic capabilities required
        /// for a cluster node.
        /// </summary>
        /// <param name="node">The target cluster node.</param>
        /// <param name="clusterDefinition">The cluster definition.</param>
        /// <param name="hostingManager">The hosting manager.</param>
        /// <param name="shutdown">Optionally shuts down the node.</param>
        public static void PrepareNode(NodeSshProxy<NodeDefinition> node, ClusterDefinition clusterDefinition, HostingManager hostingManager, bool shutdown = false)
        {
            Covenant.Requires<ArgumentNullException>(node != null, nameof(node));
            Covenant.Requires<ArgumentNullException>(clusterDefinition != null, nameof(clusterDefinition));
            Covenant.Requires<ArgumentNullException>(hostingManager != null, nameof(hostingManager));

            if (node.FileExists($"{KubeNodeFolders.State}/setup/prepared"))
            {
                return;     // Already prepared
            }

            //-----------------------------------------------------------------
            // Package manager configuration.

            node.Status = "configure: [apt] package manager";

            ConfigureApt(node, clusterDefinition.NodeOptions.PackageManagerRetries, clusterDefinition.NodeOptions.AllowPackageManagerIPv6);

            //-----------------------------------------------------------------
            // We're going to stop and mask the [snapd.service] if it's running
            // because we don't want it to randomlly update apps on cluster nodes.

            node.Status = "disable: [snapd.service]";

            var disableSnapScript =
@"
# Stop and mask [snapd.service] when it's not already masked.

systemctl status --no-pager snapd.service

if [ $? ]; then
    systemctl stop snapd.service
    systemctl mask snapd.service
fi
";
            node.SudoCommand(CommandBundle.FromScript(disableSnapScript), RunOptions.FaultOnError);

            //-----------------------------------------------------------------
            // Create the standard neonKUBE host folders.

            node.Status = "prepare: neonKUBE host folders";

            node.SudoCommand($"mkdir -p {KubeNodeFolders.Bin}", RunOptions.LogOnErrorOnly);
            node.SudoCommand($"chmod 750 {KubeNodeFolders.Bin}", RunOptions.LogOnErrorOnly);

            node.SudoCommand($"mkdir -p {KubeNodeFolders.Config}", RunOptions.LogOnErrorOnly);
            node.SudoCommand($"chmod 750 {KubeNodeFolders.Config}", RunOptions.LogOnErrorOnly);

            node.SudoCommand($"mkdir -p {KubeNodeFolders.Setup}", RunOptions.LogOnErrorOnly);
            node.SudoCommand($"chmod 750 {KubeNodeFolders.Setup}", RunOptions.LogOnErrorOnly);

            node.SudoCommand($"mkdir -p {KubeNodeFolders.State}", RunOptions.LogOnErrorOnly);
            node.SudoCommand($"chmod 750 {KubeNodeFolders.State}", RunOptions.LogOnErrorOnly);

            node.SudoCommand($"mkdir -p {KubeNodeFolders.State}/setup", RunOptions.LogOnErrorOnly);
            node.SudoCommand($"chmod 750 {KubeNodeFolders.State}/setup", RunOptions.LogOnErrorOnly);

            //-----------------------------------------------------------------
            // Other configuration.

            node.Status = "configure: journald filters";

            var filterScript =
@"
# neonKUBE: 
#
# Filter [rsyslog.service] log events we don't care about.

cat <<EOF > /etc/rsyslog.d/60-filter.conf
if $programname == ""systemd"" and ($msg startswith ""Created slice "" or $msg startswith ""Removed slice "") then stop
EOF

systemctl restart rsyslog.service
";
            node.SudoCommand(CommandBundle.FromScript(filterScript), RunOptions.FaultOnError);

            node.Status = "configure: openssh";

            ConfigureOpenSsh(node);

            node.Status = "upload: prepare files";

            node.UploadConfigFiles(clusterDefinition);
            node.UploadResources(clusterDefinition);

            node.Status = "configure: environment vars";

            if (clusterDefinition != null)
            {
                ConfigureEnvironmentVariables(node, clusterDefinition);
            }

            node.SudoCommand("safe-apt-get update");

            node.InvokeIdempotentAction("setup/prep-node",
                () =>
                {
                    node.Status = "prepare: node";
                    node.SudoCommand("setup-prep.sh");
                    node.Reboot(wait: true);
                });

            // We need to upload the cluster configuration and initialize drives attached 
            // to the node.  We're going to assume that these are not already initialized.

            node.Status = "setup: disk";

            var diskName  = hostingManager.GetDataDisk(node);
            var partition = char.IsDigit(diskName.Last()) ? $"{diskName}p1" : $"{diskName}1";

            node.SudoCommand("setup-disk.sh", diskName, partition);

            // Clear any DHCP leases to be super sure that cloned node
            // VMs will obtain fresh IP addresses.

            node.Status = "clear: DHCP leases";
            node.SudoCommand("rm -f /var/lib/dhcp/*");

            // Indicate that the node has been fully prepared.

            node.SudoCommand($"touch {KubeNodeFolders.State}/setup/prepared");

            // Shutdown the node if requested.

            if (shutdown)
            {
                node.Status = "shutdown";
                node.SudoCommand("shutdown 0", RunOptions.Defaults | RunOptions.Shutdown);
            }
        }
    }
}
