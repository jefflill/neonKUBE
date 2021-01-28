﻿//-----------------------------------------------------------------------------
// FILE:	    NodeSshProxy.ClusterSetup.cs
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

// This file includes node configuration methods executed while configuring
// the node to be a cluster member.

using System;
using System.Collections.Generic;
using System.Diagnostics.Contracts;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Neon.Collections;
using Neon.Common;
using Neon.Cryptography;
using Neon.Diagnostics;
using Neon.IO;
using Neon.Net;
using Neon.Retry;
using Neon.SSH;
using Neon.Time;

using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

using Renci.SshNet;
using Renci.SshNet.Common;

namespace Neon.Kube
{
    public partial class NodeSshProxy<TMetadata> : LinuxSshProxy<TMetadata>
        where TMetadata : class
    {
        /// <summary>
        /// Configures NTP and also installs some tool scripts for managing this.
        /// </summary>
        /// <param name="statusWriter">Optional log writer action.</param>
        public void SetupConfigureNtp(Action<string> statusWriter = null)
        {
            InvokeIdempotent("setup/ntp",
                () =>
                {
                    KubeHelper.WriteStatus(statusWriter, "Configure", $"NTP");
                    Status = $"configure: ntp";

                    var clusterDefinition = this.Cluster.Definition;
                    var nodeDefinition    = this.NodeDefinition;

                    var script =
$@"
mkdir -p /var/log/ntp

cat <<EOF > /etc/ntp.conf
# For more information about this file, see the man pages
# ntp.conf(5), ntp_acc(5), ntp_auth(5), ntp_clock(5), ntp_misc(5), ntp_mon(5).

# Specifies that NTP will implicitly trust the time source, 
# even if it is way off from the current system time.  This
# can happen when:
#
#       1. The BIOS time is set incorrectly
#       2. The machine is a VM that woke up from a long sleep
#       3. The time source is messed up
#       4. Somebody is screwing with us by sending bad time responses
#
# Normally, NTP is configured to panic when it sees a difference
# of 1000 seconds or more between the source and the system time
# and NTP will log an error and terminate.
#
# NTP is configured to start with the [-g] option, which will 
# ignore 1000+ second differences when the service starts.  This
# handles issues #1 and #2 above.
#
# The setting below disables the 1000 second check and also allows
# NTP to modify the system clock in steps of 1 second (normally,
# this is much smaller).

# \$todo(jefflill):
#
# This was the default setting when NTP was installed but I'm not 
# entirely sure that this is what we'd want for production.  The
# [man ntp.conf] page specifically recommends not messing with
# these defaults.

tinker panic 0 dispersion 1.000

# Specifies the file used to track the frequency offset of The
# local clock oscillator.

driftfile /var/lib/ntp/ntp.drift

# Configure a log file.

logfile /var/log/ntp/ntp.log
 
# Permit time synchronization with our time source, but do not
# permit the source to query or modify the service on this system.

restrict default kod nomodify notrap nopeer noquery
restrict -6 default kod nomodify notrap nopeer noquery

# Permit all access over the loopback interface.  This could
# be tightened as well, but to do so would effect some of
# the administrative functions.

restrict 127.0.0.1
restrict -6 ::1

# Hosts on local networks are less restricted.

restrict 10.0.0.0 mask 255.0.0.0 nomodify notrap
restrict 169.254.0.0 mask 255.255.0.0 nomodify notrap
restrict 172.16.0.0 mask 255.255.0.0 nomodify notrap
restrict 192.168.0.0 mask 255.255.0.0 nomodify notrap

# Use local system time when the time sources are not reachable.

server  127.127.1.0 # local clock
fudge   127.127.1.0 stratum 10

# Specify the time sources.

EOF

# Append the time sources to the configuration file.
#
# We're going to prefer the first source.  This will generally
# be the first master node.  The nice thing about this
# the worker nodes will be using the same time source on
# the local network, so the clocks should be very close to
# being closely synchronized.

sources=${GetNtpSources()}

echo ""*** TIME SOURCES = ${{sources}}"" 1>&2

preferred_set=false

for source in ${{sources}}
do
    if ! ${{preferred_set}} ; then
        echo ""server $source burst iburst prefer"" >> /etc/ntp.conf
        preferred_set=true
    else
        echo ""server $source burst iburst"" >> /etc/ntp.conf
    fi
done

# Generate the [{KubeNodeFolders.Bin}/update-time] script.

cat <<EOF > {KubeNodeFolders.Bin}/update-time
#!/bin/bash
#------------------------------------------------------------------------------
# This script stops the NTP time service and forces an immediate update.  This 
# is called from the NTP init.d script (as modified below) to ensure that we 
# obtain the current time on boot (even if the system clock is way out of sync).
# This may also be invoked manually.
#
# Usage:
#
#       update-time [--norestart]
#
#       --norestart - Don't restart NTP

. $<load-cluster-conf-quiet>

restart=true

for arg in \$@
do
    if [ ""\${{arg}}"" = ""--norestart"" ] ; then
        restart=false
    fi
done

if \${{restart}} ; then
    service ntp stop
fi

ntpdate ${{sources[@]}}

if \${{restart}} ; then
    service ntp start
fi
EOF

chmod 700 {KubeNodeFolders.Bin}/update-time

# Edit the NTP [/etc/init.d/ntp] script to initialize the hardware clock and
# call [update-time] before starting NTP.

cat <<""EOF"" > /etc/init.d/ntp
#!/bin/sh

### BEGIN INIT INFO
# Provides:        ntp
# Required-Start:  $network $remote_fs $syslog
# Required-Stop:   $network $remote_fs $syslog
# Default-Start:   2 3 4 5
# Default-Stop:    1
# Short-Description: Start NTP daemon
### END INIT INFO

PATH=/sbin:/bin:/usr/sbin:/usr/bin

. /lib/lsb/init-functions

DAEMON=/usr/sbin/ntpd
PIDFILE=/var/run/ntpd.pid

test -x $DAEMON || exit 5

if [ -r /etc/default/ntp ]; then
    . /etc/default/ntp
fi

if [ -e /var/lib/ntp/ntp.conf.dhcp ]; then
    NTPD_OPTS=""$NTPD_OPTS -c /var/lib/ntp/ntp.conf.dhcp""
fi

LOCKFILE=/var/lock/ntpdate

lock_ntpdate() {{
    if [ -x /usr/bin/lockfile-create ]; then
        lockfile-create $LOCKFILE
        lockfile-touch $LOCKFILE &
        LOCKTOUCHPID=""$!""
    fi
}}

unlock_ntpdate() {{
    if [ -x /usr/bin/lockfile-create ] ; then
        kill $LOCKTOUCHPID
        lockfile-remove $LOCKFILE
    fi
}}

RUNASUSER=ntp
UGID=$(getent passwd $RUNASUSER | cut -f 3,4 -d:) || true
if test ""$(uname -s)"" = ""Linux""; then
        NTPD_OPTS=""$NTPD_OPTS -u $UGID""
fi

case $1 in
    start)
        log_daemon_msg ""Starting NTP server"" ""ntpd""
        if [ -z ""$UGID"" ]; then
            log_failure_msg ""user \""$RUNASUSER\"" does not exist""
            exit 1
        fi

        #------------------------------
        # This is the modification.

		# This bit of voodoo disables Hyper-V time synchronization with The
		# host server.  We don't want this because the cluster is doing its
		# own time management and time sync will fight us.  This is described
		# here:
		#
		# https://social.msdn.microsoft.com/Forums/en-US/8c0a1026-0b02-405a-848e-628e68229eaf/i-have-a-lot-of-time-has-been-changed-in-the-journal-of-my-linux-boxes?forum=WAVirtualMachinesforWindows

		log_daemon_msg ""Start: Disabling Hyper-V time synchronization"" ""ntpd""
		echo 2dd1ce17-079e-403c-b352-a1921ee207ee > /sys/bus/vmbus/drivers/hv_util/unbind
		log_daemon_msg ""Finished: Disabling Hyper-V time synchronization"" ""ntpd""
        
        log_daemon_msg ""Start: Updating current time"" ""ntpd""
        {KubeNodeFolders.Bin}/update-time --norestart
        log_daemon_msg ""Finished: Updating current time"" ""ntpd""

        #------------------------------

        lock_ntpdate
        start-stop-daemon --start --quiet --oknodo --pidfile $PIDFILE --startas $DAEMON -- -p $PIDFILE $NTPD_OPTS
        status=$?
        unlock_ntpdate
        log_end_msg $status
        ;;
    stop)
        log_daemon_msg ""Stopping NTP server"" ""ntpd""
        start-stop-daemon --stop --quiet --oknodo --pidfile $PIDFILE
        log_end_msg $?
        rm -f $PIDFILE
        ;;
    restart|force-reload)
        $0 stop && sleep 2 && $0 start
        ;;
    try-restart)
        if $0 status >/dev/null; then
            $0 restart
        else
            exit 0
        fi
        ;;
    reload)
        exit 3
        ;;
    status)
        status_of_proc $DAEMON ""NTP server""
        ;;
    *)
        echo ""Usage: $0 {{start|stop|restart|try-restart|force-reload|status}}""
        exit 2
        ;;
esac
EOF

# Restart NTP to ensure that we've picked up the current time.

service ntp restart
";
                    SudoCommand(CommandBundle.FromScript(script), RunOptions.Defaults | RunOptions.FaultOnError);
                });
        }

        /// <summary>
        /// Configures the global environment variables that describe the configuration 
        /// of the server within the cluster.
        /// </summary>
        /// <param name="setupState">The setup controller state.</param>
        public void ConfigureEnvironmentVariables(ObjectDictionary setupState)
        {
            Covenant.Requires<ArgumentNullException>(setupState != null, nameof(setupState));

            var clusterDefinition = Cluster.Definition;
            var nodeDefinition    = NeonHelper.CastTo<NodeDefinition>(Metadata);

            Status = "environment variables";

            // We're going to append the new variables to the existing Linux [/etc/environment] file.

            var sb = new StringBuilder();

            // Append all of the existing environment variables except for those
            // whose names start with "NEON_" to make the operation idempotent.
            //
            // Note that we're going to special case PATH to add any Neon
            // related directories.

            using (var currentEnvironmentStream = new MemoryStream())
            {
                Download("/etc/environment", currentEnvironmentStream);

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

            sb.AppendLine($"NEON_NODE_NAME={Name}");

            if (nodeDefinition != null)
            {
                sb.AppendLine($"NEON_NODE_ROLE={nodeDefinition.Role}");
                sb.AppendLine($"NEON_NODE_IP={nodeDefinition.Address}");
                sb.AppendLine($"NEON_NODE_HDD={nodeDefinition.Labels.StorageHDD.ToString().ToLowerInvariant()}");
            }

            sb.AppendLine($"NEON_BIN_FOLDER={KubeNodeFolders.Bin}");
            sb.AppendLine($"NEON_CONFIG_FOLDER={KubeNodeFolders.Config}");
            sb.AppendLine($"NEON_SETUP_FOLDER={KubeNodeFolders.Setup}");
            sb.AppendLine($"NEON_STATE_FOLDER={KubeNodeFolders.State}");
            sb.AppendLine($"NEON_TMPFS_FOLDER={KubeNodeFolders.Tmpfs}");

            // Kubernetes related variables for masters.

            if (nodeDefinition.IsMaster)
            {
                sb.AppendLine($"KUBECONFIG=/etc/kubernetes/admin.conf");
            }

            // Upload the new environment to the server.

            UploadText("/etc/environment", sb, tabStop: 4);
        }

        /// <summary>
        /// Updates the node hostname and related configuration.
        /// </summary>
        private void UpdateHostname()
        {
            // Update the hostname.

            SudoCommand($"hostnamectl set-hostname {Name}");

            // We need to edit [/etc/cloud/cloud.cfg] to preserve the hostname change.

            var cloudCfg = DownloadText("/etc/cloud/cloud.cfg");

            cloudCfg = cloudCfg.Replace("preserve_hostname: false", "preserve_hostname: true");

            UploadText("/etc/cloud/cloud.cfg", cloudCfg);

            // Update the [/etc/hosts] file to resolve the new hostname.

            var sbHosts = new StringBuilder();

            var nodeAddress = Address.ToString();
            var separator = new string(' ', Math.Max(16 - nodeAddress.Length, 1));

            sbHosts.Append(
$@"
127.0.0.1	    localhost
127.0.0.1       kubernetes-masters
{nodeAddress}{separator}{Name}
::1             localhost ip6-localhost ip6-loopback
ff02::1         ip6-allnodes
ff02::2         ip6-allrouters
");
            UploadText("/etc/hosts", sbHosts, 4, Encoding.UTF8);
        }

        /// <summary>
        /// Configures cluster package manager caching.
        /// </summary>
        /// <param name="setupState">The setup controller state.</param>
        public void SetupPackageProxy(ObjectDictionary setupState)
        {
            Covenant.Requires<ArgumentNullException>(setupState != null, nameof(setupState));

            var nodeDefinition    = NeonHelper.CastTo<NodeDefinition>(Metadata);
            var clusterDefinition = Cluster.Definition;
            var hostingManager    = setupState.Get<IHostingManager>(KubeSetup.HostingManagerProperty);

            InvokeIdempotent("setup/package-caching",
                () =>
                {
                    // Configure the [apt-cacher-ng] pckage proxy service on master nodes.

                    if (NodeDefinition.Role == NodeRole.Master)
                    {
                        var proxyServiceScript =
@"
	set -eou pipefail	# Enable full failure detection

	safe-apt-get update
	safe-apt-get install -yq apt-cacher-ng

	# Configure the cache to pass-thru SSL requests
	# and then restart.

	echo ""PassThroughPattern:^.*:443$"" >> /etc/apt-cacher-ng/acng.conf
	systemctl restart apt-cacher-ng

	set -eo pipefail	# Revert back to partial failure detection

	# Give the proxy service a chance to start.

	sleep 5
";
                        SudoCommand(CommandBundle.FromScript(proxyServiceScript), RunOptions.FaultOnError);
                    }

                    var sbPackageProxies = new StringBuilder();

                    if (clusterDefinition.PackageProxy != null)
                    {
                        foreach (var proxyEndpoint in clusterDefinition.PackageProxy.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                        {
                            sbPackageProxies.AppendWithSeparator(proxyEndpoint);
                        }
                    }

                    // Configure the package manager to use the first master as the proxy by default,
                    // failing over to the other masters (in order) when necessary.

                    var proxySelectorScript =
$@"
# Configure APT proxy selection.

echo {sbPackageProxies} > {KubeNodeFolders.Config}/package-proxy

cat <<EOF > /usr/local/bin/get-package-proxy
#!/bin/bash
#------------------------------------------------------------------------------
# FILE:        get-package-proxy
# CONTRIBUTOR: Generated by [neon-cli] during cluster setup.
#
# This script determine which (if any) configured APT proxy caches are running
# and returns its endpoint or ""DIRECT"" if none of the proxies are available and 
# the distribution's mirror should be accessed directly.  This uses the
# [{KubeNodeFolders.Config}/package-proxy] file to obtain the list of proxies.
#
# This is called when the following is specified in the APT configuration,
# as we do further below:
#
#		Acquire::http::Proxy-Auto-Detect ""/usr/local/bin/get-package-proxy"";
#
# See this link for more information:
#
#		https://trent.utfs.org/wiki/Apt-get#Failover_Proxy
NEON_PACKAGE_PROXY=$(cat {KubeNodeFolders.Config}/package-proxy)
if [ ""\${{NEON_PACKAGE_PROXY}}"" == """" ] ; then
    echo DIRECT
    exit 0
fi
for proxy in ${{NEON_PACKAGE_PROXY}}; do
	if nc -w1 -z \${{proxy/:/ }}; then
		echo http://\${{proxy}}/
		exit 0
	fi
done
echo DIRECT
exit 0
EOF

chmod 775 /usr/local/bin/get-package-proxy

cat <<EOF > /etc/apt/apt.conf
//-----------------------------------------------------------------------------
// FILE:        /etc/apt/apt.conf
// CONTRIBUTOR: Generated by during neonKUBE cluster setup.
//
// This file configures APT on the local machine to proxy requests through the
// [apt-cacher-ng] instance(s) at the configured.  This uses the [/usr/local/bin/get-package-proxy] 
// script to select a working PROXY if there are more than one, or to go directly to the package
// mirror if none of the proxies are available.
//
// Presumably, this cache is running on the local network which can dramatically
// reduce external network traffic to the APT mirrors and improve cluster setup 
// and update performance.

Acquire::http::Proxy-Auto-Detect ""/usr/local/bin/get-package-proxy"";
EOF
";
                    SudoCommand(proxySelectorScript, RunOptions.FaultOnError);
                });
        }

        /// <summary>
        /// Performs common node configuration.
        /// </summary>
        /// <param name="setupState">The setup controller state.</param>
        public void SetupNode(ObjectDictionary setupState)
        {
            Covenant.Requires<ArgumentNullException>(setupState != null, nameof(setupState));

            var nodeDefinition    = NeonHelper.CastTo<NodeDefinition>(Metadata);
            var clusterDefinition = Cluster.Definition;
            var hostingManager    = setupState.Get<IHostingManager>(KubeSetup.HostingManagerProperty);

            InvokeIdempotent("setup/common",
                () =>
                {
                    PrepareNode(setupState);
                    ConfigureEnvironmentVariables(setupState);
                    SetupPackageProxy(setupState);

                    // Set the hostname.

                    UpdateHostname();
                });
        }

        /// <summary>
        /// Installs a prepositioned Helm chart from a master node.
        /// </summary>
        /// <param name="chartName">The name of the Helm chart.</param>
        /// <param name="releaseName">Optionally specifies the component release name.</param>
        /// <param name="namespace">Optionally specifies the namespace where Kubernetes namespace where the Helm chart should be installed. This defaults to <b>default</b></param>
        /// <param name="values">Optionally specifies Helm chart values.</param>
        /// <returns>The tracking <see cref="Task"/>.</returns>
        /// <remarks>
        /// neonKUBE images prepositions the Helm chart files embedded as resources in the <b>Resources/Helm</b>
        /// project folder to cluster node images as the <b>/lib/neonkube/helm/charts.zip</b> archive.  This
        /// method unzips that file to the same folder (if it hasn't been unzipped already) and then installs
        /// the helm chart (if it hasn't already been installed).
        /// </remarks>
        public async Task InstallHelmChartAsync(
            string                              chartName,
            string                              releaseName = null,
            string                              @namespace  = "default",
            List<KeyValuePair<string, object>>  values      = null)
        {
            if (string.IsNullOrEmpty(releaseName))
            {
                releaseName = chartName;
            }

            // Unzip the Helm chart archive if we haven't done so already.

            InvokeIdempotent("setup/helm-unzip",
                () =>
                {
                    var zipPath = LinuxPath.Combine(KubeNodeFolders.Helm, "charts.zip");

                    SudoCommand($"unzip {zipPath} -d {KubeNodeFolders.Helm}");
                    SudoCommand($"rm -f {zipPath}");
                });

            // Install the chart when we haven't already done so.

            InvokeIdempotent($"setup/helm-install-{releaseName}",
                () =>
                {
                    var valueOverrides = new StringBuilder();

                    if (values != null)
                    {
                        foreach (var value in values)
                        {
                            var valueType = value.Value.GetType();

                            if (valueType == typeof(string))
                            {
                                valueOverrides.AppendWithSeparator($"--set-string {value.Key}=\"{value.Value}\"", @"\n");
                            }
                            else if (valueType == typeof(int))
                            {
                                valueOverrides.AppendWithSeparator($"--set {value.Key}={value.Value}", @"\n");
                            }
                            else
                            {
                                throw new NotImplementedException();
                            }
                        }
                    }

                    var helmChartScript =
$@"#!/bin/bash
cd /tmp/charts

until [ -f {chartName}.zip ]
do
  sleep 1
done

rm -rf {chartName}

unzip {chartName}.zip -d {chartName}
helm install {releaseName} {chartName} --namespace {@namespace} -f {chartName}/values.yaml {valueOverrides}

START=`date +%s`
DEPLOY_END=$((START+15))

until [ `helm status {releaseName} --namespace {@namespace} | grep ""STATUS: deployed"" | wc -l` -eq 1  ];
do
  if [ $((`date +%s`)) -gt $DEPLOY_END ]; then
    helm delete {releaseName} || true
    exit 1
  fi
   sleep 1
done

rm -rf {chartName}*
";
                    SudoCommand(CommandBundle.FromScript(helmChartScript), RunOptions.None).EnsureSuccess();
                });

            await Task.CompletedTask;
        }

        /// <summary>
        /// Installs the required Kubernetes related components on a node.
        /// </summary>
        /// <param name="setupState">The setup controller state.</param>
        public void SetupKubernetes(ObjectDictionary setupState)
        {
            InvokeIdempotent("setup/setup-install-kubernetes",
                () =>
                {
                    Status = "setup: kubernetes apt repository";

                    var bundle = CommandBundle.FromScript(
$@"#!/bin/bash
curl {KubeHelper.CurlOptions} https://packages.cloud.google.com/apt/doc/apt-key.gpg | apt-key add -
echo ""deb https://apt.kubernetes.io/ kubernetes-xenial main"" > /etc/apt/sources.list.d/kubernetes.list
safe-apt-get update
");
                    SudoCommand(bundle);

                    Status = "install: kubeadm";
                    SudoCommand($"safe-apt-get install -yq kubeadm={KubeVersions.KubeAdminPackageVersion}");

                    Status = "install: kubectl";
                    SudoCommand($"safe-apt-get install -yq kubectl={KubeVersions.KubeCtlPackageVersion}");

                    Status = "install: kubelet";
                    SudoCommand($"safe-apt-get install -yq kubelet={KubeVersions.KubeletPackageVersion}");

                    Status = "hold: kubernetes packages";
                    SudoCommand("apt-mark hold kubeadm kubectl kubelet");

                    Status = "configure: kubelet";
                    SudoCommand("mkdir -p /opt/cni/bin");
                    SudoCommand("mkdir -p /etc/cni/net.d");
                    SudoCommand(CommandBundle.FromScript(
@"#!/bin/bash

echo KUBELET_EXTRA_ARGS=--network-plugin=cni --cni-bin-dir=/opt/cni/bin --cni-conf-dir=/etc/cni/net.d --feature-gates=\""AllAlpha=false,RunAsGroup=true\"" --container-runtime=remote --cgroup-driver=systemd --container-runtime-endpoint='unix:///var/run/crio/crio.sock' --runtime-request-timeout=5m> /etc/default/kubelet
systemctl daemon-reload
service kubelet restart
"));

                    // Download and install the Helm client:

                    InvokeIdempotent("setup/cluster-helm",
                        () =>
                        {
                            Status = "install: helm";

                            var helmInstallScript =
$@"#!/bin/bash
cd /tmp
curl {KubeHelper.CurlOptions} {KubeDownloads.HelmLinuxUri} > helm.tar.gz
tar xvf helm.tar.gz
cp linux-amd64/helm /usr/local/bin
chmod 770 /usr/local/bin/helm
rm -f helm.tar.gz
rm -rf linux-amd64
";
                            SudoCommand(CommandBundle.FromScript(helmInstallScript));
                        });
                });
        }
    }
}
