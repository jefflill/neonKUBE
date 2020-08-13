﻿//-----------------------------------------------------------------------------
// FILE:	    NetworkOptions.cs
// CONTRIBUTOR: Jeff Lill
// COPYRIGHT:	Copyright (c) 2005-2020 by neonFORGE, LLC.  All rights reserved.
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
using System.Diagnostics.Contracts;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using Newtonsoft.Json.Serialization;
using YamlDotNet.Serialization;

using Neon.Common;
using Neon.Net;

namespace Neon.Kube
{
    /// <summary>
    /// Describes the network options for a cluster.
    /// </summary>
    public class NetworkOptions
    {
        //---------------------------------------------------------------------
        // Local types

        /// <summary>
        /// Used for checking subnet conflicts below.
        /// </summary>
        private class SubnetDefinition
        {
            /// <summary>
            /// Constructor.
            /// </summary>
            /// <param name="name">Subnet name.</param>
            /// <param name="cidr">Subnet CIDR.</param>
            public SubnetDefinition(string name, NetworkCidr cidr)
            {
                this.Name = $"{nameof(NetworkOptions)}.{name}";
                this.Cidr = cidr;
            }

            /// <summary>
            /// Identifies the subnet.
            /// </summary>
            public string Name { get; set; }

            /// <summary>
            /// The subnet CIDR.
            /// </summary>
            public NetworkCidr Cidr { get; set; }
        }

        //---------------------------------------------------------------------
        // Implementation

        private const string defaultPodSubnet       = "10.254.0.0/16";
        private const string defaultServiceSubnet   = "10.253.0.0/16";
        private const string defaultCloudNodeSubnet = "10.100.0.0/16";

        /// <summary>
        /// Default constructor.
        /// </summary>
        public NetworkOptions()
        {
        }

        /// <summary>
        /// Specifies the subnet for entire host network for on-premise environments like
        /// <see cref="HostingEnvironments.Machine"/>, <see cref="HostingEnvironments.HyperVLocal"/> and
        /// <see cref="HostingEnvironments.XenServer"/>.  This is required for those environments.
        /// </summary>
        [JsonProperty(PropertyName = "PremiseSubnet", Required = Required.Default, DefaultValueHandling = DefaultValueHandling.IgnoreAndPopulate)]
        [YamlMember(Alias = "premiseSubnet", ApplyNamingConventions = false)]
        [DefaultValue(null)]
        public string PremiseSubnet { get; set; }

        /// <summary>
        /// <para>
        /// The subnet where the cluster nodes reside.
        /// </para>
        /// <note>
        /// This property must be configured for the on-premise providers (<see cref="HostingEnvironments.Machine"/>, 
        /// <b>HyperV</b>, and <b>XenServer</b>,...).  This defaults to <b>10.100.0.0/16</b> for cloud deployments 
        /// but can be customized as required.
        /// </note>
        /// <note>
        /// For on-premise clusters, the statically assigned IP addresses assigned 
        /// to the nodes must reside within the this subnet.  The network gateway
        /// will be assumed to be the second address in the subnet and the broadcast
        /// address will assumed to be the last address.
        /// </note>
        /// <note>
        /// <para>
        /// For cloud deployments, nodes will be assigned reasolable IP addresses by default.  You may assigned specific
        /// IP addresses to nodes within the to nodes if necessary, with a couple reservations:
        /// </para>
        /// <list type="bullet">
        ///     <item>
        ///     The first 10 IP addresses of the <see cref="NodeSubnet"/> are reserved for use by the cloud as well
        ///     as neonKUBE.  The default cloud <see cref="NodeSubnet"/> is <b>10.100.0.0/16</b> which means that
        ///     addresses from <b>10.100.0.0 - 10.100.0.9</b> are reserved, so the first available node IP will be
        ///     <b>10.100.0.10</b>.  Cloud platforms typically use IPs in the range for as the default gateway and
        ///     also for DNS request forwarding.  neonKUBE reserves the remaining addresses for potential future
        ///     features like integrated VPN and cluster management VMs.
        ///     </item>
        ///     <item>
        ///     The last IP address of the <see cref="NodeSubnet"/> is also reserved.  Clouds typically use this
        ///     as the UDP broadcast address for the network.  This will be <b>10.100.255.255</b> for the default
        ///     cloud subnet.
        ///     </item>
        /// </list>
        /// </note>
        /// </summary>
        [JsonProperty(PropertyName = "NodeSubnet", Required = Required.Default, DefaultValueHandling = DefaultValueHandling.IgnoreAndPopulate)]
        [YamlMember(Alias = "nodeSubnet", ApplyNamingConventions = false)]
        [DefaultValue(null)]
        public string NodeSubnet { get; set; }

        /// <summary>
        /// <para>
        /// Specifies the pod subnet to be used for the cluster.  This subnet will be
        /// split so that each node will be allocated its own subnet.  This defaults
        /// to <b>10.254.0.0/16</b>.
        /// </para>
        /// <note>
        /// <b>WARNING:</b> This subnet must not conflict with any other subnets
        /// provisioned within the premise network.
        /// </note>
        /// </summary>
        [JsonProperty(PropertyName = "PodSubnet", Required = Required.Default, DefaultValueHandling = DefaultValueHandling.IgnoreAndPopulate)]
        [YamlMember(Alias = "podSubnet", ApplyNamingConventions = false)]
        [DefaultValue(defaultPodSubnet)]
        public string PodSubnet { get; set; } = defaultPodSubnet;

        /// <summary>
        /// Specifies the subnet subnet to be used for the allocating service addresses
        /// within the cluster.  This defaults to <b>10.253.0.0/16</b>.
        /// </summary>
        [JsonProperty(PropertyName = "ServiceSubnet", Required = Required.Default, DefaultValueHandling = DefaultValueHandling.IgnoreAndPopulate)]
        [YamlMember(Alias = "serviceSubnet", ApplyNamingConventions = false)]
        [DefaultValue(defaultServiceSubnet)]
        public string ServiceSubnet { get; set; } = defaultServiceSubnet;

        /// <summary>
        /// The IP addresses of the upstream DNS nameservers to be used by the cluster.  This defaults to the 
        /// Google Public DNS servers: <b>[ "8.8.8.8", "8.8.4.4" ]</b> when the property is <c>null</c> or empty.
        /// </summary>
        [JsonProperty(PropertyName = "Nameservers", Required = Required.Default)]
        [YamlMember(Alias = "nameservers", ApplyNamingConventions = false)]
        [DefaultValue(null)]
        public List<string> Nameservers { get; set; } = null;

        /// <summary>
        /// Specifies the default network gateway address to be configured for hosts.  This defaults to the 
        /// first usable address in the <see cref="PremiseSubnet"/>.  For example, for the <b>10.0.0.0/24</b> 
        /// subnet, this will be set to <b>10.0.0.1</b>.  This is ignored for cloud hosting 
        /// environments.
        /// </summary>
        [JsonProperty(PropertyName = "Gateway", Required = Required.Default, DefaultValueHandling = DefaultValueHandling.IgnoreAndPopulate)]
        [YamlMember(Alias = "gateway", ApplyNamingConventions = false)]
        [DefaultValue(null)]
        public string Gateway { get; set; } = null;

        /// <summary>
        /// Optionally enable Istio mutual TLS support for cross pod communication.
        /// This defaults to <c>false</c>.
        /// </summary>
        [JsonProperty(PropertyName = "MutualPodTLS", Required = Required.Default, DefaultValueHandling = DefaultValueHandling.IgnoreAndPopulate)]
        [YamlMember(Alias = "mutualPodTLS", ApplyNamingConventions = false)]
        [DefaultValue(false)]
        public bool MutualPodTLS { get; set; } = false;

        /// <summary>
        /// 
        /// </summary>
        public string IngressNodeSelector { get; set; } = null;

        /// <summary>
        /// Optionally sets the ingress routing rules external traffic received by nodes
        /// with <see cref="NodeDefinition.Ingress"/> enabled into one or more Istio ingress
        /// gateway services which are then responsible for routing to the target Kubernetes 
        /// services.
        /// </summary>
        [JsonProperty(PropertyName = "IngressRules", Required = Required.Default, DefaultValueHandling = DefaultValueHandling.IgnoreAndPopulate)]
        [YamlMember(Alias = "ingressRules", ApplyNamingConventions = false)]
        [DefaultValue(null)]
        public List<IngressRule> IngressRules { get; set; } = new List<IngressRule>();

        /// <summary>
        /// Optionally specifies the whitelisted external addresses for outbound traffic.
        /// This defaults to allowing outboud traffic to anywhere when the property is
        /// <c>null</c> or empty.  Specify <b>0.0.0.0/32</b> to block all traffic.
        /// </summary>
        [JsonProperty(PropertyName = "EgressAddresses", Required = Required.Default, DefaultValueHandling = DefaultValueHandling.IgnoreAndPopulate)]
        [YamlMember(Alias = "egressAddresses", ApplyNamingConventions = false)]
        [DefaultValue(null)]
        public List<string> EgressAddresses { get; set; } = new List<string>();

        /// <summary>
        /// Validates the options and also ensures that all <c>null</c> properties are
        /// initialized to their default values.
        /// </summary>
        /// <param name="clusterDefinition">The cluster definition.</param>
        /// <exception cref="ClusterDefinitionException">Thrown if the definition is not valid.</exception>
        [Pure]
        public void Validate(ClusterDefinition clusterDefinition)
        {
            Covenant.Requires<ArgumentNullException>(clusterDefinition != null, nameof(clusterDefinition));

            var isCloud       = clusterDefinition.Hosting.IsCloudProvider;
            var subnets       = new List<SubnetDefinition>();
            var gateway       = (IPAddress)null;
            var premiseSubnet = (NetworkCidr)null;
            var nodeSubnet    = (NetworkCidr)null;

            // Nameservers

            Nameservers = Nameservers ?? new List<string>();

            if (!isCloud && (Nameservers == null || Nameservers.Count == 0))
            {
                Nameservers = new List<string> { "8.8.8.8", "8.8.4.4" };
            }

            foreach (var nameserver in Nameservers)
            {
                if (!IPAddress.TryParse(nameserver, out var address))
                {
                    throw new ClusterDefinitionException($"[{nameserver}] is not a valid [{nameof(NetworkOptions)}.{nameof(Nameservers)}] IP address.");
                }
            }

            // Verify [PremiseSubnet].

            if (!isCloud)
            {
                if (!NetworkCidr.TryParse(PremiseSubnet, out premiseSubnet))
                {
                    throw new ClusterDefinitionException($"[{nameof(NetworkOptions)}.{nameof(PremiseSubnet)}={PremiseSubnet}] is not a valid IPv4 subnet.");
                }

                subnets.Add(new SubnetDefinition(nameof(PremiseSubnet), premiseSubnet));
            }

            // Verify [NodeSubnet].

            if (isCloud)
            {
                if (string.IsNullOrEmpty(NodeSubnet))
                {
                    nodeSubnet = NetworkCidr.Parse(defaultCloudNodeSubnet);
                    NodeSubnet  = defaultCloudNodeSubnet;
                }
                else
                {
                    if (!NetworkCidr.TryParse(NodeSubnet, out nodeSubnet))
                    {
                        throw new ClusterDefinitionException($"[{nameof(NetworkOptions)}.{nameof(NodeSubnet)}={NodeSubnet}] is not a valid IPv4 subnet.");
                    }
                }
            }
            else
            {
                if (!NetworkCidr.TryParse(NodeSubnet, out nodeSubnet))
                {
                    throw new ClusterDefinitionException($"[{nameof(NetworkOptions)}.{nameof(NodeSubnet)}={NodeSubnet}] is not a valid IPv4 subnet.");
                }

                if (!premiseSubnet.Contains(nodeSubnet))
                {
                    throw new ClusterDefinitionException($"[{nameof(NetworkOptions)}.{nameof(NodeSubnet)}={NodeSubnet}] is not within [{nameof(NetworkOptions)}.{nameof(PremiseSubnet)}={PremiseSubnet}].");
                }
            }

            subnets.Add(new SubnetDefinition(nameof(NodeSubnet), nodeSubnet));

            // Verify [Gateway]

            if (isCloud)
            {
                gateway = nodeSubnet.FirstUsableAddress;
                Gateway = gateway.ToString();
            }
            else
            {
                if (string.IsNullOrEmpty(Gateway))
                {
                    // Default to the first valid address of the cluster nodes subnet 
                    // if this isn't already set.

                    Gateway = premiseSubnet.FirstUsableAddress.ToString();
                }

                if (!IPAddress.TryParse(Gateway, out gateway) || gateway.AddressFamily != AddressFamily.InterNetwork)
                {
                    throw new ClusterDefinitionException($"[{nameof(NetworkOptions)}.{nameof(Gateway)}={Gateway}] is not a valid IPv4 address.");
                }

                if (!premiseSubnet.Contains(gateway))
                {
                    throw new ClusterDefinitionException($"[{nameof(NetworkOptions)}.{nameof(Gateway)}={Gateway}] address is not within the [{nameof(NetworkOptions)}.{nameof(NetworkOptions.NodeSubnet)}={NodeSubnet}] subnet.");
                }
            }

            // Verify [PodSubnet].

            if (!NetworkCidr.TryParse(PodSubnet, out var podSubnet))
            {
                throw new ClusterDefinitionException($"[{nameof(NetworkOptions)}.{nameof(PodSubnet)}={PodSubnet}] is not a valid IPv4 subnet.");
            }

            subnets.Add(new SubnetDefinition(nameof(PodSubnet), podSubnet));

            // Verify [ServiceSubnet].

            if (!NetworkCidr.TryParse(ServiceSubnet, out var serviceSubnet))
            {
                throw new ClusterDefinitionException($"[{nameof(NetworkOptions)}.{nameof(ServiceSubnet)}={ServiceSubnet}] is not a valid IPv4 subnet.");
            }

            subnets.Add(new SubnetDefinition(nameof(ServiceSubnet), serviceSubnet));

            // Verify that none of the major subnets conflict.

            foreach (var subnet in subnets)
            {
                foreach (var next in subnets)
                {
                    if (subnet == next)
                    {
                        continue;   // Don't test against self.
                    }

                    if (subnet.Cidr.Overlaps(next.Cidr))
                    {
                        throw new ClusterDefinitionException($"[{nameof(NetworkOptions)}]: Subnet conflict: [{subnet.Name}={subnet.Cidr}] and [{next.Name}={next.Cidr}] overlap.");
                    }
                }
            }

            // Verify [IngressRules].

            IngressRules = IngressRules ?? new List<IngressRule>();

            foreach (var rule in IngressRules)
            {
                rule.Validate(clusterDefinition);
            }

            // Verify

            EgressAddresses = EgressAddresses ?? new List<string>();

            foreach (var address in EgressAddresses)
            {
                if (!NetworkCidr.TryParse(address, out var v1) && !IPAddress.TryParse(address, out var v2))
                {
                    throw new ClusterDefinitionException($"[{nameof(NetworkOptions)}]: [{nameof(EgressAddresses)}] includes invalid IP address or subnet [{address}].");
                }
            }
        }

        /// <summary>
        /// Ensures that for cloud deployments, an explicit node address assignment does not conflict 
        /// with any VNET addresses reserved by the cloud provider or neonKUBE.
        /// </summary>
        /// <param name="clusterDefinition">The cluster definition.</param>
        /// <param name="nodeDefinition">The node definition.</param>
        /// <exception cref="ClusterDefinitionException">Thrown for cloud deployments where the node specifies an explicit IP address that conflicts with a reserved VNET address.</exception>
        internal void ValidateCloudNodeAddress(ClusterDefinition clusterDefinition, NodeDefinition nodeDefinition)
        {
            Covenant.Requires<ArgumentNullException>(clusterDefinition != null, nameof(clusterDefinition));
            Covenant.Requires<ArgumentNullException>(nodeDefinition != null, nameof(nodeDefinition));

            if (clusterDefinition.Hosting.IsCloudProvider)
            {
                var nodeSubnet = clusterDefinition.Network.NodeSubnet;
            }
        }
    }
}
