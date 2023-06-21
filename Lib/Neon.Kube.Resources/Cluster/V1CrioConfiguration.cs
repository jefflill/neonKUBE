//-----------------------------------------------------------------------------
// FILE:        V1CrioConfiguration.cs
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
using System.ComponentModel.DataAnnotations;
using System.Text;

using k8s;
using k8s.Models;

using NJsonSchema.Annotations;

namespace Neon.Kube.Resources.Cluster
{
    /// <summary>
    /// Describes A Crio Configuration.
    /// </summary>
    [KubernetesEntity(Group = KubeGroup, ApiVersion = KubeApiVersion, Kind = KubeKind, PluralName = KubePlural)]
    [EntityScope(EntityScope.Cluster)]
    public class V1CrioConfiguration : IKubernetesObject<V1ObjectMeta>, ISpec<V1CrioConfiguration.CrioConfigurationSpec>
    {
        /// <summary>
        /// Object API group.
        /// </summary>
        public const string KubeGroup = ResourceHelper.NeonKubeResourceGroup;

        /// <summary>
        /// Object API version.
        /// </summary>
        public const string KubeApiVersion = "v1alpha1";

        /// <summary>
        /// Object API kind.
        /// </summary>
        public const string KubeKind = "CrioConfiguration";

        /// <summary>
        /// Object plural name.
        /// </summary>
        public const string KubePlural = "crioconfigurations";

        /// <summary>
        /// Default constructor.
        /// </summary>
        public V1CrioConfiguration()
        {
            ApiVersion = $"{KubeGroup}/{KubeApiVersion}";
            Kind       = KubeKind;
        }

        /// <summary>
        /// Gets or sets APIVersion defines the versioned schema of this
        /// representation of an object. Servers should convert recognized
        /// schemas to the latest internal value, and may reject unrecognized
        /// values. More info:
        /// https://git.k8s.io/community/contributors/devel/sig-architecture/api-conventions.md#resources
        /// </summary>
        public string ApiVersion { get; set; }

        /// <summary>
        /// Gets or sets kind is a string value representing the REST resource
        /// this object represents. Servers may infer this from the endpoint
        /// the client submits requests to. Cannot be updated. In CamelCase.
        /// More info:
        /// https://git.k8s.io/community/contributors/devel/sig-architecture/api-conventions.md#types-kinds
        /// </summary>
        public string Kind { get; set; }

        /// <summary>
        /// Gets or sets standard object metadata.
        /// </summary>
        public V1ObjectMeta Metadata { get; set; }

        /// <summary>
        /// The spec.
        /// </summary>
        public CrioConfigurationSpec Spec { get; set; }

        /// <summary>
        /// The CRIO configuration specification.
        /// </summary>
        public class CrioConfigurationSpec
        {
            /// <summary>
            /// The list of <see cref="V1NeonContainerRegistry.RegistrySpec"/>.
            /// </summary>
            [Required]
            public List<KeyValuePair<string, V1NeonContainerRegistry.RegistrySpec>> Registries{ get; set; } 
                = new List<KeyValuePair<string, V1NeonContainerRegistry.RegistrySpec>>();
        }
    }
}
