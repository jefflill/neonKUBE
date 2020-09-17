﻿//-----------------------------------------------------------------------------
// FILE:	    AzureNodeOptions.cs
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
    /// Azure specific options for a cluster node.  These options can be used to
    /// override defaults specified by <see cref="AzureHostingOptions"/>.  The
    /// constructor initializes reasonable values.
    /// </summary>
    public class AzureNodeOptions
    {
        /// <summary>
        /// Optionally specifies the Azure virtual machine size.  The available VM sizes are listed 
        /// <a href="https://docs.microsoft.com/en-us/azure/virtual-machines/sizes-general">here</a>.
        /// This defaults to <see cref="AzureHostingOptions.DefaultVmSize"/>.
        /// </summary>
        [JsonProperty(PropertyName = "VmSize", Required = Required.Default, DefaultValueHandling = DefaultValueHandling.IgnoreAndPopulate)]
        [YamlMember(Alias = "vmSize", ApplyNamingConventions = false)]
        [DefaultValue(null)]
        public string VmSize { get; set; } = null;

        /// <summary>
        /// <para>
        /// Optionally specifies the storage type to use for any mounted drives.  This defaults to <see cref="AzureStorageType.Default"/>
        /// which indicates that <see cref="AzureHostingOptions.DefaultStorageType"/> will specify the storage type
        /// for this node.  By default, <see cref="AzureStorageType.StandardSSD"/> drives will be provisioned
        /// when storage type is not specified.
        /// </para>
        /// <note>
        /// You should really consider upgrading production clusters to one of the SSD based storage types.
        /// </note>
        /// </summary>
        /// <remarks>
        /// <note>
        /// All virtual machine sizes support <see cref="AzureStorageType.StandardHDD"/> which is why that
        /// is the default value.  Consult the consult the Azure documentation to virtual machine size specified 
        /// by <see cref="VmSize"/> can support <see cref="AzureStorageType.PremiumSSD"/>.
        /// </note>
        /// <para>
        /// <see cref="AzureStorageType.StandardHDD"/> specifies relatively slow rotating hard drives,
        /// <see cref="AzureStorageType.StandardSSD"/> specifies standard SSD based drives,
        /// <see cref="AzureStorageType.PremiumSSD"/> specifies fast SSD based drives, and finally
        /// <see cref="AzureStorageType.UltraSSD"/> specifies super fast SSD based drives.  Azure recommends that
        /// most production VMs deploy with SSDs.
        /// </para>
        /// <note>
        /// <see cref="AzureStorageType.UltraSSD"/> storage is still relatively new and your region may not be able to
        /// attach ultra drives to all VM instance types.  See this <a href="https://docs.microsoft.com/en-us/azure/virtual-machines/windows/disks-enable-ultra-ssd">note</a>
        /// for more information.
        /// </note>
        /// </remarks>
        [JsonProperty(PropertyName = "StorageType", Required = Required.Default, DefaultValueHandling = DefaultValueHandling.IgnoreAndPopulate)]
        [YamlMember(Alias = "storageType", ApplyNamingConventions = false)]
        [DefaultValue(AzureStorageType.Default)]
        public AzureStorageType StorageType { get; set; } = AzureStorageType.Default;

        /// <summary>
        /// Optionally specifies the size of the mounted managed Azure disk as <see cref="ByteUnits"/>.  This
        /// defaults to <c>null</c> which indicates that <see cref="AzureHostingOptions.DefaultDiskSize"/>
        /// will be used, and that defaults to <b>128 GiB</b>.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <see cref="AzureStorageType.StandardHDD"/>, <see cref="AzureStorageType.StandardSSD"/>, and
        /// <see cref="AzureStorageType.PremiumSSD"/> drives may be provisioned in these
        /// sizes: <b>4GiB</b>, <b>8GiB</b>, <b>16GiB</b>, <b>32GiB</b>, <b>64GiB</b>, <b>128GiB</b>, <b>256GiB</b>, <b>512GiB</b>,
        /// <b>1TiB</b>, <b>2TiB</b>, <b>4TiB</b>, <b>8TiB</b>, <b>16TiB</b>, or <b>32TiB</b>.
        /// </para>
        /// <para>
        /// <see cref="AzureStorageType.UltraSSD"/> based drives can be provisioned in these sizes:
        /// <b>4 GiB</b>,<b>8 GiB</b>,<b> GiB</b>,<b>16 GiB</b>,<b>32 GiB</b>,<b>64 GiB</b>,<b>128 GiB</b>,<b>256 GiB</b>,<b>512 GiB</b>,
        /// or from <b>1 TiB</b> to <b>64TiB</b> in increments of <b>1 TiB</b>.
        /// </para>
        /// <note>
        /// Node disks smaller than 32 GiB are not supported by neonKUBE.  We'll automatically
        /// upgrade the disk size when necessary.
        /// </note>
        /// <note>
        /// This size will be rounded up to the next valid drive size for the given storage type
        /// and set to the maximum allowed size, when necessary.
        /// </note>
        /// <note>
        /// The Azure disk sizes listed above may become out-of-date as Azure enhances their
        /// services.  Review the Azure documentation for more information about what is
        /// currently supported.
        /// </note>
        /// </remarks>
        [JsonProperty(PropertyName = "DiskSize", Required = Required.Default, DefaultValueHandling = DefaultValueHandling.IgnoreAndPopulate)]
        [YamlMember(Alias = "diskSize", ApplyNamingConventions = false)]
        [DefaultValue(null)]
        public string DiskSize { get; set; } = null;

        /// <summary>
        /// Optionally overrides the default VM generation assignment made by neonKUBE
        /// cluster setup for this node.  This defaults to <c>null</c> which allows
        /// setup to make the choice.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Azure supports two generations of VM images that correspond roughly to HYPER-V
        /// VM generations. <b>Gen1</b> VMs are are older.  These VMs use BIOS to boot,
        /// IDE to access disk drives and are somewhat slower to provision and boot.
        /// <b>Gen2</b> images use UEFI to boot (which supports PXE), OS disk drives
        /// larger than 2TiB, and accelerated netwoking but Gen2 images don't support
        /// disk encryption.  Here's a link with additional detail:
        /// </para>
        /// <para>
        /// https://docs.microsoft.com/en-us/azure/virtual-machines/windows/generation-2
        /// </para>
        /// <para>
        /// Not all Azure VM sizes support Gen1 or Gen2 VMs.  neonKUBE attempts to deploy
        /// Gen2 VMs when supported for best performance.  You can override this behavior
        /// by setting this value to <b>1</b> or <b>2</b>.
        /// </para>
        /// </remarks>
        [JsonProperty(PropertyName = "VmGen", Required = Required.Default, DefaultValueHandling = DefaultValueHandling.IgnoreAndPopulate)]
        [YamlMember(Alias = "vmGen", ApplyNamingConventions = false)]
        [DefaultValue(null)]
        public int? VmGen { get; set; } = null;

        /// <summary>
        /// Validates the options and also ensures that all <c>null</c> properties are
        /// initialized to their default values.
        /// </summary>
        /// <param name="clusterDefinition">The cluster definition.</param>
        /// <param name="nodeName">The associated node name.</param>
        /// <exception cref="ClusterDefinitionException">Thrown if the definition is not valid.</exception>
        [Pure]
        public void Validate(ClusterDefinition clusterDefinition, string nodeName)
        {
            Covenant.Requires<ArgumentNullException>(clusterDefinition != null, nameof(clusterDefinition));

            if (StorageType == AzureStorageType.Default)
            {
                StorageType = clusterDefinition.Hosting.Azure.DefaultStorageType;

                if (StorageType == AzureStorageType.Default)
                {
                    StorageType = AzureStorageType.StandardSSD;
                }
            }

            // Validate the VM size, setting the cluster default if necessary.

            var vmSize = this.VmSize;

            if (string.IsNullOrEmpty(vmSize))
            {
                vmSize = clusterDefinition.Hosting.Azure.DefaultVmSize;
            }

            this.VmSize = vmSize;

            // Validate the drive size, setting the cluster default if necessary.

            if (string.IsNullOrEmpty(this.DiskSize))
            {
                this.DiskSize = clusterDefinition.Hosting.Azure.DefaultDiskSize;
            }

            if (!ByteUnits.TryParse(this.DiskSize, out var driveSizeBytes) || driveSizeBytes <= 1)
            {
                throw new ClusterDefinitionException($"cluster node [{nodeName}] configures [{nameof(AzureNodeOptions)}.{nameof(DiskSize)}={DiskSize}] which is not valid.");
            }

            var driveSizeGiB = AzureHelper.GetDiskSizeGiB(StorageType, driveSizeBytes);

            this.DiskSize = $"{driveSizeGiB} GiB";

            // Validate the VM generation override.

            if (VmGen.HasValue && (VmGen.Value != 1 && VmGen.Value != 2))
            {
                throw new ClusterDefinitionException($"cluster node [{nodeName}] configures [{nameof(AzureHostingOptions)}.{nameof(VmGen)}={VmGen}] which is not valid.  Only values of 1 or 2 are allowed.");
            }
        }
    }
}