//-----------------------------------------------------------------------------
// FILE:        AzureImageDetails.cs
// CONTRIBUTOR: Marcus Bowyer
// COPYRIGHT:   Copyright © 2005-2023 by NEONFORGE LLC.  All rights reserved.
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
using System.Linq;
using System.Text;
using System.Threading.Tasks;

using Neon.Kube;
using Neon.ModelGen;
using Newtonsoft.Json;

namespace Neon.Kube.Models
{
    /// <summary>
    /// Holds the information required to deploy a node from an Azure VM image.
    /// </summary>
    [Target("all")]
    public interface AzureImageDetails
    {
        /// <summary>
        /// Identifies the Azure marketplace compute plan.
        /// </summary>
        [JsonProperty(PropertyName = "ComputePlan", Required = Required.Always, DefaultValueHandling = DefaultValueHandling.IgnoreAndPopulate)]
        [DefaultValue(null)]
        AzureComputePlan ComputePlan { get; set; }

        /// <summary>
        /// Identifies the Azure VM image.
        /// </summary>
        [JsonProperty(PropertyName = "ImageReference", Required = Required.Always, DefaultValueHandling = DefaultValueHandling.IgnoreAndPopulate)]
        [DefaultValue(null)]
        AzureImageReference ImageReference { get; set; }
    }
}
