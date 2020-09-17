﻿//-----------------------------------------------------------------------------
// FILE:	    AwsHostingOptions.cs
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

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics.Contracts;
using System.IO;
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
    /// Specifies the Amazon Web Services hosting settings.
    /// </summary>
    public class AwsHostingOptions
    {
        /// <summary>
        /// The maximum number of placement group partitions supported by AWS.
        /// </summary>
        internal const int MaxPlacementPartitions = 7;

        private const string            defaultInstanceType = "t3a.medium";
        private const AwsVolumeType     defaultVolumeType   = AwsVolumeType.Gp2;
        private const string            defaultVolumeSize   = "128 GiB";

        /// <summary>
        /// Constructor.
        /// </summary>
        public AwsHostingOptions()
        {
        }

        /// <summary>
        /// The AWS access key ID that identifies the IAM key created for the IAM
        /// user assigned to neonKUBE for management activities, including creating
        /// the cluster.  This combined with <see cref="SecretAccessKey"/> will be
        /// used to confirm the identity.
        /// </summary>
        [JsonProperty(PropertyName = "AccessKeyId", Required = Required.Default, DefaultValueHandling = DefaultValueHandling.IgnoreAndPopulate)]
        [YamlMember(Alias = "accessKeyId", ApplyNamingConventions = false)]
        [DefaultValue(null)]
        public string AccessKeyId { get; set; }

        /// <summary>
        /// The AWS secret used to confirm the <see cref="AccessKeyId"/> identity.
        /// </summary>
        [JsonProperty(PropertyName = "SecretAccessKey", Required = Required.Default, DefaultValueHandling = DefaultValueHandling.IgnoreAndPopulate)]
        [YamlMember(Alias = "secretAccessKey", ApplyNamingConventions = false)]
        [DefaultValue(null)]
        public string SecretAccessKey { get; set; }

        /// <summary>
        /// Specifies the AWS zone where the cluster will be provisioned.  This is required.
        /// </summary>
        [JsonProperty(PropertyName = "AvailabilityZone", Required = Required.Default, DefaultValueHandling = DefaultValueHandling.IgnoreAndPopulate)]
        [YamlMember(Alias = "availabilityZone", ApplyNamingConventions = false)]
        [DefaultValue(null)]
        public string AvailabilityZone { get; set; }

        /// <summary>
        /// Returns the AWS region where the cluster will be provisioned.  This is
        /// derived from <see cref="AvailabilityZone"/> by removing the last character, which
        /// is the zone suffix.
        /// </summary>
        [JsonIgnore]
        [YamlIgnore]
        public string Region => AvailabilityZone?.Substring(0, AvailabilityZone.Length - 1);

        /// <summary>
        /// Specifies the number of master placement group partitions the cluster master node
        /// instances will be deployed to.  This defaults to <b>-1</b> which means that the number 
        /// of partitions will equal the number of master nodes.  AWS supports a maximum of 7
        /// placement partitions.
        /// </summary>
        /// <remarks>
        /// <para>
        /// AWS provides three different types of <b>placement groups</b> to user help manage
        /// where virtual machine instances are provisioned within an AWS availability zone
        /// to customize fault tolerance due to AWS hardware failures.
        /// </para>
        /// <para>
        /// <a href="https://docs.aws.amazon.com/AWSEC2/latest/UserGuide/placement-groups.html">AWS Placement groups</a>
        /// </para>
        /// <para>
        /// neonKUBE provisions instances using two <b>partition placement groups</b>, one for
        /// the cluster master nodes and the other for the workers.  The idea is that master
        /// nodes should be deployed on separate hardware for fault tolerance because if the
        /// majority of master nodes go offline, the entire cluster will be dramatically impacted.
        /// In general, the number of <see cref="MasterPlacementPartitions"/> partitions should
        /// equal the number of master nodes.
        /// </para>
        /// <para>
        /// Worker nodes are distributed across <see cref="WorkerPlacementPartitions"/> partitions
        /// in a separate placement group.  The number of worker partitions defaults to 1, potentially
        /// limiting the resilience to AWS hardware failures while making it more likely that AWS
        /// will be able to actually satisfy the conditions to provision the cluster node instances.
        /// </para>
        /// <para>
        /// Unfortunately, AWS may not have enough distinct hardware available to satisfy 
        /// your requirements.  In this case, we recommend that you try another availability
        /// zone first and if that doesn't work try reducing the number of partitions which
        /// can be as low as 1 partition.  
        /// </para>
        /// </remarks>
        [JsonProperty(PropertyName = "MasterPlacementPartitions", Required = Required.Default, DefaultValueHandling = DefaultValueHandling.IgnoreAndPopulate)]
        [YamlMember(Alias = "masterPlacementPartitions", ApplyNamingConventions = false)]
        [DefaultValue(-1)]
        public int MasterPlacementPartitions { get; set; } = -1;

        /// <summary>
        /// Specifies the number of worker placement group partitions the cluster worker node
        /// instances will be deployed to.  This defaults to <b>1</b> trading off resilience
        /// to hardware failures against increasing the chance that AWS will actually be able
        /// to provision the cluster nodes.  AWS supports a maximum of 7 placement partitions.
        /// </summary>
        /// <remarks>
        /// <para>
        /// AWS provides three different types of <b>placement groups</b> to user help manage
        /// where virtual machine instances are provisioned within an AWS availability zone
        /// to customize fault tolerance due to AWS hardware failures.
        /// </para>
        /// <para>
        /// <a href="https://docs.aws.amazon.com/AWSEC2/latest/UserGuide/placement-groups.html">AWS Placement groups</a>
        /// </para>
        /// <para>
        /// neonKUBE provisions instances using two <b>partition placement groups</b>, one for
        /// the cluster master nodes and the other for the workers.  The idea is that master
        /// nodes should be deployed on separate hardware for fault tolerance because if the
        /// majority of master nodes go offline, the entire cluster will be dramatically impacted.
        /// In general, the number of <see cref="MasterPlacementPartitions"/> partitions should
        /// equal the number of master nodes.
        /// </para>
        /// <para>
        /// Worker nodes are distributed across <see cref="WorkerPlacementPartitions"/> partitions
        /// in a separate placement group.  The number of worker partitions defaults to 1, potentially
        /// limiting the resilience to AWS hardware failures while making it more likely that AWS
        /// will be able to actually satisfy the conditions to provision the cluster node instances.
        /// </para>
        /// <para>
        /// Unfortunately, AWS may not have enough distinct hardware available to satisfy 
        /// your requirements.  In this case, we recommend that you try another availability
        /// zone first and if that doesn't work try reducing the number of partitions which
        /// can be as low as 1 partition.  
        /// </para>
        /// </remarks>
        [JsonProperty(PropertyName = "WorkerPlacementPartitions", Required = Required.Default, DefaultValueHandling = DefaultValueHandling.IgnoreAndPopulate)]
        [YamlMember(Alias = "workerPlacementPartitions", ApplyNamingConventions = false)]
        [DefaultValue(1)]
        public int WorkerPlacementPartitions { get; set; } = 1;

        /// <summary>
        /// AWS resource group where all cluster components are to be provisioned.  This defaults
        /// to "neon-" plus the cluster name but can be customized as required.
        /// </summary>
        [JsonProperty(PropertyName = "ResourceGroup", Required = Required.Default, DefaultValueHandling = DefaultValueHandling.IgnoreAndPopulate)]
        [YamlMember(Alias = "resourceGroup", ApplyNamingConventions = false)]
        [DefaultValue(null)]
        public string ResourceGroup { get; set; }

        /// <summary>
        /// Identifies the default AWS instance type to be provisioned for cluster nodes that don't
        /// specify an instance type.  This defaults to <b>t3a.medium</b> which includes 2 virtual
        /// cores and 4 GiB RAM.
        /// </summary>
        [JsonProperty(PropertyName = "DefaultInstanceType", Required = Required.Default, DefaultValueHandling = DefaultValueHandling.IgnoreAndPopulate)]
        [YamlMember(Alias = "defaultInstanceType", ApplyNamingConventions = false)]
        [DefaultValue(defaultInstanceType)]
        public string DefaultInstanceType { get; set; } = defaultInstanceType;

        /// <summary>
        /// Specifies the default EBS volume type to use for cluster node disks.  This defaults
        /// to <see cref="AwsVolumeType.Gp2"/> which is SSD based and offers a reasonable
        /// compromise between performance and cost.
        /// </summary>
        [JsonProperty(PropertyName = "DefaultVolumeType", Required = Required.Default, DefaultValueHandling = DefaultValueHandling.IgnoreAndPopulate)]
        [YamlMember(Alias = "defaultVolumeType", ApplyNamingConventions = false)]
        [DefaultValue(defaultVolumeType)]
        public AwsVolumeType DefaultVolumeType { get; set; } = defaultVolumeType;

        /// <summary>
        /// Specifies the default AWS disk size to be used when creating a
        /// node that does not specify a disk size in its <see cref="NodeOptions"/>.
        /// This defaults to <b>128 GiB</b>.
        /// </summary>
        /// <remarks>
        /// <note>
        /// Node disks smaller than 32 GiB are not supported by neonKUBE.  We'll automatically
        /// upgrade the disk size when necessary.
        /// </note>
        /// </remarks>
        [JsonProperty(PropertyName = "DefaultVolumeSize", Required = Required.Default, DefaultValueHandling = DefaultValueHandling.IgnoreAndPopulate)]
        [YamlMember(Alias = "defaultVolumeSize", ApplyNamingConventions = false)]
        [DefaultValue(defaultVolumeSize)]
        public string DefaultVolumeSize { get; set; } = defaultVolumeSize;

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

            foreach (var ch in clusterDefinition.Name)
            {
                if (char.IsLetterOrDigit(ch) || ch == '-' || ch == '_')
                {
                    continue;
                }

                throw new ClusterDefinitionException($"cluster name [{clusterDefinition.Name}] is not valid for AWS deployment.  Only letters, digits, dashes, or underscores are allowed.");
            }

            if (string.IsNullOrEmpty(AccessKeyId))
            {
                throw new ClusterDefinitionException($"AWS hosting [{nameof(AccessKeyId)}] is required.");
            }

            if (string.IsNullOrEmpty(SecretAccessKey))
            {
                throw new ClusterDefinitionException($"AWS hosting [{nameof(SecretAccessKey)}] is required.");
            }

            if (string.IsNullOrEmpty(AvailabilityZone))
            {
                throw new ClusterDefinitionException($"AWS hosting [{nameof(AvailabilityZone)}] is required.");
            }

            // Verify [ResourceGroup].

            if (string.IsNullOrEmpty(ResourceGroup))
            {
                ResourceGroup = clusterDefinition.Name;
            }

            if (ResourceGroup.Length > 64)
            {
                throw new ClusterDefinitionException($"AWS hosting [{nameof(ResourceGroup)}={ResourceGroup}] is longer than 64 characters.");
            }

            if (!char.IsLetter(ResourceGroup.First()))
            {
                throw new ClusterDefinitionException($"AWS hosting [{nameof(ResourceGroup)}={ResourceGroup}] does not begin with a letter.");
            }

            if (ResourceGroup.Last() == '_' || ResourceGroup.Last() == '-')
            {
                throw new ClusterDefinitionException($"AWS hosting [{nameof(ResourceGroup)}={ResourceGroup}] ends with a dash or underscore.");
            }

            foreach (var ch in ResourceGroup)
            {
                if (!(char.IsLetterOrDigit(ch) || ch == '_' || ch == '-'))
                {
                    throw new ClusterDefinitionException($"AWS hosting [{nameof(ResourceGroup)}={ResourceGroup}] includes characters other than letters, digits, dashes and underscores.");
                }
            }

            // Verify [MasterPlacementPartitions]

            if (MasterPlacementPartitions < 0)
            {
                MasterPlacementPartitions = Math.Min(MaxPlacementPartitions, clusterDefinition.Masters.Count());
            }
            else
            {
                if (MasterPlacementPartitions < 1 || MaxPlacementPartitions < MasterPlacementPartitions)
                {
                    throw new ClusterDefinitionException($"AWS hosting [{nameof(MasterPlacementPartitions)}={MasterPlacementPartitions}] cannot be in the range [1...{MaxPlacementPartitions}]");
                }
            }

            // Verify [MasterPlacementPartitions]

            if (WorkerPlacementPartitions < 1 || MaxPlacementPartitions < WorkerPlacementPartitions)
            {
                throw new ClusterDefinitionException($"AWS hosting [{nameof(WorkerPlacementPartitions)}={WorkerPlacementPartitions}] cannot be in the range [1...{MaxPlacementPartitions}]");
            }

            // Verify [DefaultInstanceType]

            if (string.IsNullOrEmpty(DefaultInstanceType))
            {
                DefaultInstanceType = defaultInstanceType;
            }

            // Verify [DefaultVolumeSize].

            if (string.IsNullOrEmpty(DefaultVolumeSize))
            {
                DefaultVolumeSize = defaultVolumeSize;
            }

            if (!ByteUnits.TryParse(DefaultVolumeSize, out var volumeSize) || volumeSize <= 0)
            {
                throw new ClusterDefinitionException($"AWS hosting [{nameof(DefaultVolumeSize)}={DefaultVolumeSize}] is not valid.");
            }

            // Check AWS cluster limits.

            if (clusterDefinition.Masters.Count() > KubeConst.MaxMasters)
            {
                throw new ClusterDefinitionException($"cluster master count [{clusterDefinition.Masters.Count()}] exceeds the [{KubeConst.MaxMasters}] limit for clusters.");
            }

            if (clusterDefinition.Nodes.Count() > AwsHelper.MaxClusterNodes)
            {
                throw new ClusterDefinitionException($"cluster node count [{clusterDefinition.Nodes.Count()}] exceeds the [{AwsHelper.MaxClusterNodes}] limit for clusters deployed to AWS.");
            }
        }
    }
}