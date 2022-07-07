﻿//-----------------------------------------------------------------------------
// FILE:	    V1NeonDashboard.cs
// CONTRIBUTOR: Marcus Bowyer
// COPYRIGHT:	Copyright (c) 2005-2022 by neonFORGE LLC.  All rights reserved.
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
using System.Text;

using k8s;
using k8s.Models;

#if KUBEOPS
using DotnetKubernetesClient.Entities;
using KubeOps.Operator.Entities;
using KubeOps.Operator.Entities.Annotations;
#endif

#if KUBEOPS
namespace Neon.Kube.ResourceDefinitions
#else
namespace Neon.Kube.Resources
#endif
{
    /// <summary>
    /// Describes a Dashboard that will be accesible via the Neon Dashboard.
    /// </summary>
    [KubernetesEntity(Group = KubeGroup, ApiVersion = KubeApiVersion, Kind = KubeKind, PluralName = KubePlural)]
#if KUBEOPS
    [KubernetesEntityShortNames]
    [EntityScope(EntityScope.Cluster)]
    [Description("Describes a Dashboard that will be accesible via the Neon Dashboard.")]
#endif
    public class V1NeonDashboard : CustomKubernetesEntity<V1NeonDashboard.NeonDashboardSpec>
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
        public const string KubeKind = "NeonDashboard";

        /// <summary>
        /// Object plural name.
        /// </summary>
        public const string KubePlural = "neondashboards";

        /// <summary>
        /// Default constructor.
        /// </summary>
        public V1NeonDashboard()
        {
            this.SetMetadata();
        }

        /// <summary>
        /// The dashboard specification.
        /// </summary>
        public class NeonDashboardSpec
        {
            private const string urlRegex = @"https?:\/\/(www\.)?[-a-zA-Z0-9@:%._\+~#=]{1,256}\.[a-zA-Z0-9()]{1,6}\b([-a-zA-Z0-9()@:%_\+.~#?&//=]*)";

            /// <summary>
            /// <para>
            /// The target dashboard's Url. This is required.
            /// </para>
            /// </summary>
#if KUBEOPS
            [Required]
            [Pattern(urlRegex)]
#endif
            public string Url { get; set; } = null;

            /// <summary>
            /// The display name. This is what will show up in the Neon Dashboard.
            /// </summary>
            public string DisplayName { get; set; }

            /// <summary>
            /// <para>
            /// Optionally indicates that the order in which the dashboard will be displayed.
            /// </para>
            /// </summary>
            public int DisplayOrder { get; set; } = int.MaxValue;

            /// <summary>
            /// <para>
            /// Optionally indicates whether the dashboard is enabled or disabled.
            /// </para>
            /// </summary>
            public bool Enabled { get; set; } = true;
        }
    }
}
