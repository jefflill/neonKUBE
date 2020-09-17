﻿//-----------------------------------------------------------------------------
// FILE:	    NodeDefinition.cs
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
using System.Net;
using System.Linq;
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
    /// Describes a cluster node.
    /// </summary>
    public class NodeDefinition
    {
        //---------------------------------------------------------------------
        // Static methods

        /// <summary>
        /// Parses a <see cref="NodeDefinition"/> from Kubernetes node labels.
        /// </summary>
        /// <param name="labels">The node labels.</param>
        /// <returns>The parsed <see cref="NodeDefinition"/>.</returns>
        public static NodeDefinition ParseFromLabels(Dictionary<string, string> labels)
        {
            var node = new NodeDefinition();

            return node;
        }

        //---------------------------------------------------------------------
        // Instance methods

        private string name;

        /// <summary>
        /// Constructor.
        /// </summary>
        public NodeDefinition()
        {
            Labels = new NodeLabels(this);
        }

        /// <summary>
        /// Uniquely identifies the node within the cluster.
        /// </summary>
        /// <remarks>
        /// <note>
        /// The name may include only letters, numbers, periods, dashes, and underscores and
        /// also that all names will be converted to lower case.
        /// </note>
        /// </remarks>
        [JsonProperty(PropertyName = "Name", Required = Required.Default, DefaultValueHandling = DefaultValueHandling.IgnoreAndPopulate)]
        [YamlMember(Alias = "name", ApplyNamingConventions = false)]
        [DefaultValue(null)]
        public string Name
        {
            get { return name; }

            set
            {
                if (value != null)
                {
                    name = value.ToLowerInvariant();
                }
                else
                {
                    name = null;
                }
            }
        }

        /// <summary>
        /// The node's IP address or <c>null</c> if one has not been assigned yet.
        /// Note that an node's IP address cannot be changed once the node has
        /// been added to the cluster.
        /// </summary>
        [JsonProperty(PropertyName = "Address", Required = Required.Default)]
        [YamlMember(Alias = "address", ApplyNamingConventions = false)]
        [DefaultValue(null)]
        public string Address { get; set; } = null;

        /// <summary>
        /// Indicates that the node will act as a master node (defaults to <c>false</c>).
        /// </summary>
        /// <remarks>
        /// <para>
        /// Master nodes are reponsible for managing service discovery and coordinating 
        /// pod deployment across the cluster.
        /// </para>
        /// <para>
        /// An odd number of master nodes must be deployed in a cluster (to help prevent
        /// split-brain).  One master node may be deployed for non-production environments,
        /// but to enable high-availability, three or five master nodes may be deployed.
        /// </para>
        /// </remarks>
        [JsonIgnore]
        [YamlIgnore]
        public bool IsMaster
        {
            get { return Role.Equals(NodeRole.Master, StringComparison.InvariantCultureIgnoreCase); }
        }

        /// <summary>
        /// Returns <c>true</c> for worker nodes.
        /// </summary>
        [JsonIgnore]
        [YamlIgnore]
        public bool IsWorker
        {
            get { return Role.Equals(NodeRole.Worker, StringComparison.InvariantCultureIgnoreCase); }
        }

        /// <summary>
        /// Returns the node's <see cref="NodeRole"/>.  This defaults to <see cref="NodeRole.Worker"/>.
        /// </summary>
        [JsonProperty(PropertyName = "Role", Required = Required.Default, DefaultValueHandling = DefaultValueHandling.IgnoreAndPopulate)]
        [YamlMember(Alias = "role", ApplyNamingConventions = false)]
        [DefaultValue(NodeRole.Worker)]
        public string Role { get; set; } = NodeRole.Worker;

        /// <summary>
        /// <para>
        /// Indicates whether this node should be configured to accept external network traffic
        /// on node ports and route that into the cluster.
        /// </para>
        /// <note>
        /// If all nodes have <see cref="Ingress"/> set to <c>false</c> and the cluster defines
        /// one or more <see cref="NetworkOptions.IngressRules"/> then neonKUBE will choose a
        /// reasonable set of nodes to accept ibound traffic.
        /// </note>
        /// </summary>
        public bool Ingress { get; set; } = false;

        /// <summary>
        /// Specifies the labels to be assigned to the cluster node.  These can provide
        /// detailed information such as the host CPU, RAM, storage, etc.  <see cref="NodeLabels"/>
        /// for more information.
        /// </summary>
        [JsonProperty(PropertyName = "Labels")]
        [YamlMember(Alias = "labels", ApplyNamingConventions = false)]
        [DefaultValue(null)]
        public NodeLabels Labels { get; set; }

        /// <summary>
        /// Specifies the taints to be assigned to the cluster node.  
        /// </summary>
        [JsonProperty(PropertyName = "Taints")]
        [YamlMember(Alias = "taints", ApplyNamingConventions = false)]
        [DefaultValue(null)]
        public List<string> Taints { get; set; }

        /// <summary>
        /// Hypervisor hosting related options for environments like Hyper-V and XenServer.
        /// </summary>
        [JsonProperty(PropertyName = "Vm")]
        [YamlMember(Alias = "vm", ApplyNamingConventions = false)]
        [DefaultValue(null)]
        public VmNodeOptions Vm { get; set; }

        /// <summary>
        /// Azure provisioning options for this node, or <c>null</c> to use reasonable defaults.
        /// </summary>
        [JsonProperty(PropertyName = "Azure")]
        [YamlMember(Alias = "azure", ApplyNamingConventions = false)]
        [DefaultValue(null)]
        public AzureNodeOptions Azure { get; set; }

        /// <summary>
        /// AWS provisioning options for this node, or <c>null</c> to use reasonable defaults.
        /// </summary>
        [JsonProperty(PropertyName = "Aws")]
        [YamlMember(Alias = "aws", ApplyNamingConventions = false)]
        [DefaultValue(null)]
        public AwsNodeOptions Aws { get; set; }

        /// <summary>
        /// <b>HACK:</b> This used by <see cref="SetupController{T}"/> to introduce a delay for this
        /// node when executing the next setup step.
        /// </summary>
        [JsonIgnore]
        [YamlIgnore]
        internal TimeSpan StepDelay { get; set; }

        /// <summary>
        /// Validates the node definition.
        /// </summary>
        /// <param name="clusterDefinition">The cluster definition.</param>
        /// <exception cref="ArgumentException">Thrown if the definition is not valid.</exception>
        [Pure]
        public void Validate(ClusterDefinition clusterDefinition)
        {
            Covenant.Requires<ArgumentNullException>(clusterDefinition != null, nameof(clusterDefinition));

            // Ensure that the labels are wired up to the parent node.

            if (Labels == null)
            {
                Labels = new NodeLabels(this);
            }
            else
            {
                Labels.Node = this;
            }

            if (Name == null)
            {
                throw new ClusterDefinitionException($"The [{nameof(NodeDefinition)}.{nameof(Name)}] property is required.");
            }

            if (!ClusterDefinition.IsValidName(Name))
            {
                throw new ClusterDefinitionException($"The [{nameof(NodeDefinition)}.{nameof(Name)}={Name}] property is not valid.  Only letters, numbers, periods, dashes, and underscores are allowed.");
            }

            if (name == "localhost")
            {
                throw new ClusterDefinitionException($"The [{nameof(NodeDefinition)}.{nameof(Name)}={Name}] property is not valid.  [localhost] is reserved.");
            }

            if (Name.StartsWith("neon-", StringComparison.InvariantCultureIgnoreCase))
            {
                throw new ClusterDefinitionException($"The [{nameof(NodeDefinition)}.{nameof(Name)}={Name}] property is not valid because node names starting with [node-] are reserved.");
            }

            if (string.IsNullOrEmpty(Role))
            {
                Role = NodeRole.Worker;
            }

            if (!Role.Equals(NodeRole.Master, StringComparison.InvariantCultureIgnoreCase) && !Role.Equals(NodeRole.Worker, StringComparison.InvariantCultureIgnoreCase))
            {
                throw new ClusterDefinitionException($"Node [{Name}] has invalid [{nameof(Role)}={Role}].  This must be [{NodeRole.Master}] or [{NodeRole.Worker}].");
            }

            if (clusterDefinition.Hosting.IsOnPremiseProvider)
            {
                if (string.IsNullOrEmpty(Address))
                {
                    throw new ClusterDefinitionException($"Node [{Name}] requires [{nameof(Address)}] when hosting in an on-premise facility.");
                }

                if (!IPAddress.TryParse(Address, out var nodeAddress))
                {
                    throw new ClusterDefinitionException($"Node [{Name}] has invalid IP address [{Address}].");
                }
            }

            switch (clusterDefinition.Hosting.Environment)
            {
                case HostingEnvironment.Aws:

                    Aws = Aws ?? new AwsNodeOptions();
                    Aws.Validate(clusterDefinition, this.Name);
                    break;

                case HostingEnvironment.Azure:

                    Azure = Azure ?? new AzureNodeOptions();
                    Azure.Validate(clusterDefinition, this.Name);
                    break;

                case HostingEnvironment.Google:

                    // $todo(jefflill: Implement this
                    break;

                case HostingEnvironment.Machine:

                    // No machine options to check at this time.
                    break;

                case HostingEnvironment.HyperV:
                case HostingEnvironment.HyperVLocal:
                case HostingEnvironment.XenServer:

                    Vm = Vm ?? new VmNodeOptions();
                    Vm.Validate(clusterDefinition, this.Name);
                    break;

                default:

                    throw new NotImplementedException($"Hosting environment [{clusterDefinition.Hosting.Environment}] hosting option check is not implemented.");
            }
        }
    }
}