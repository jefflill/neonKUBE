﻿//-----------------------------------------------------------------------------
// FILE:	    NeonClusterConst.cs
// CONTRIBUTOR: Jeff Lill
// COPYRIGHT:	Copyright (c) 2016-2018 by neonFORGE, LLC.  All rights reserved.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

using Neon.Net;

namespace Neon.Cluster
{
    /// <summary>
    /// Important neonCLUSTER constants.
    /// </summary>
    /// <remarks>
    /// <note>
    /// <b>IMPORTANT:</b> These definitions must match those in the <b>$\Stack\Docker\Images\neoncluster.sh</b>
    /// file.  You must manually update that file and then rebuild and push the containers
    /// as well as redeploy all clusters from scratch.
    /// </note>
    /// </remarks>
    public static class NeonClusterConst
    {
        /// <summary>
        /// Name for the built-in cluster user that has the ability to manage other users.
        /// </summary>
        public const string RootUser = "root";

        /// <summary>
        /// The local endpoint exposed by cluster docker instances to be monitored by the 
        /// <b>neon-log-metricbeat</b> container to capture Docker metrics.
        /// </summary>
        public readonly static string DockerApiInternalEndpoint = $"tcp://127.0.0.1:{NetworkPorts.Docker}";

        /// <summary>
        /// Name of the standard cluster <b>public</b> overlay network.
        /// </summary>
        public const string PublicNetwork = "neon-public";

        /// <summary>
        /// Name of the standard cluster <b>private</b> overlay network.
        /// </summary>
        public const string PrivateNetwork = "neon-private";

        /// <summary>
        /// IP endpoint of the Docker embedded DNS server.
        /// </summary>
        public const string DockerDnsEndpoint = "127.0.0.11:53";

        /// <summary>
        /// Hostname of the Docker public registry.
        /// </summary>
        public const string DockerPublicRegistry = "docker.io";

        /// <summary>
        /// The default Vault transit key.
        /// </summary>
        public const string VaultTransitKey = "neon-transit";

        /// <summary>
        /// The root Vault Docker registry key.
        /// </summary>
        public const string VaultRegistryKey = "neon-secret/registry";

        /// <summary>
        /// The Vault Docker registry credentials key.
        /// </summary>
        public static readonly string VaultRegistryCredentialsKey = $"{VaultRegistryKey}/credentials";

        /// <summary>
        /// The port exposed by the <b>neon-proxy-public</b> and <b>neon-proxy-private</b>
        /// HAProxy service that server the proxy statistics pages.
        /// </summary>
        public const int HAProxyStatsPort = 1936;

        /// <summary>
        /// The relative URI for the HAProxy statistics pages.
        /// </summary>
        public const string HaProxyStatsUri = "/_stats?no-cache";

        /// <summary>
        /// The HAProxy unique ID generating format used for generating activity IDs.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The generated ID parts are:
        /// </para>
        /// <list type="table">
        /// <item>
        ///     <term><b>%ci</b></term>
        ///     <descrption>
        ///     Client IP address.
        ///     </descrption>
        /// </item>
        /// <item>
        ///     <term><b>%cp</b></term>
        ///     <descrption>
        ///     Client port number.
        ///     </descrption>
        /// </item>
        /// <item>
        ///     <term><b>%fi</b></term>
        ///     <descrption>
        ///     Proxy frontend IP address.
        ///     </descrption>
        /// </item>
        /// <item>
        ///     <term><b>%fp</b></term>
        ///     <descrption>
        ///     Proxy frontend port number.
        ///     </descrption>
        /// </item>
        /// <item>
        ///     <term><b>%Ts</b></term>
        ///     <descrption>
        ///     Timestamp.
        ///     </descrption>
        /// </item>
        /// <item>
        ///     <term><b>%rt</b></term>
        ///     <descrption>
        ///     Proxy request count.
        ///     </descrption>
        /// </item>
        /// </list>
        /// </remarks>
        public const string HAProxyUidFormat = "%{+X}o%ci:%cp_%fi:%fp_%Ts_%rt";

        /// <summary>
        /// The maximum number of manager nodes allowed in a neonCLUSTER.
        /// </summary>
        public const int MaxManagers = 5;

        /// <summary>
        /// Identifies the Git production branch.
        /// </summary>
        public const string GitProdBranch = "prod";

        /// <summary>
        /// Consul root key for cluster globals. 
        /// </summary>
        public static readonly string ClusterRootKey = "neon/cluster";

        /// <summary>
        /// Consul root key for the Dynamic DNS service related values.
        /// </summary>
        public static readonly string ConsulDnsRootKey = "neon/dns";

        /// <summary>
        /// Consul root key for the Dynamic DNS entry definitions.
        /// </summary>
        public static readonly string ConsulDnsEntriesKey = $"{ConsulDnsRootKey}/entries";

        /// <summary>
        /// Consul key for the Dynamic DNS answer <b>hosts.txt</b> file.
        /// </summary>
        public static readonly string ConsulDnsHostsKey = $"{ConsulDnsRootKey}/answers/hosts.txt";

        /// <summary>
        /// Consul key for the Dynamic DNS answer <b>hosts.md5</b> file.
        /// </summary>
        public static readonly string ConsulDnsHostsMd5Key = $"{ConsulDnsRootKey}/answers/hosts.md5";

        /// <summary>
        /// Consul key where cluster dashboards are registered.
        /// </summary>
        public const string ConsulDashboardsKey = "neon/dashboards";

        /// <summary>
        /// Consul root key for the <b>neon-registry</b> service.
        /// </summary>
        public const string ConsulRegistryRootKey = "neon/services/neon-registry";

        /// <summary>
        /// Identifies the dashboard folder where built-in cluster dashboards will reside.
        /// </summary>
        public const string DashboardSystemFolder = "system";
    }
}
