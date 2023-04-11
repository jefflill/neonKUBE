﻿//-----------------------------------------------------------------------------
// FILE:	    KubeConfigContext.cs
// CONTRIBUTOR: Jeff Lill
// COPYRIGHT:	Copyright © 2005-2023 by NEONFORGE LLC.  All rights reserved.
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

using k8s.KubeConfigModels;

using Neon.Common;
using Neon.Cryptography;
using Neon.Kube;

using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using Newtonsoft.Json.Serialization;
using YamlDotNet.Serialization;

namespace Neon.Kube.Login
{
    /// <summary>
    /// Describes a Kubernetes context.
    /// </summary>
    public class KubeConfigContext
    {
        private bool            loginsLoaded;
        private ClusterLogin    cachedLogins;

        /// <summary>
        /// Default constructor.
        /// </summary>
        public KubeConfigContext()
        {
            Properties = new KubeConfigContextProperties();
        }

        /// <summary>
        /// Constructs a configuration from a structured name.
        /// </summary>
        /// <param name="contextName">The structured context name.</param>
        public KubeConfigContext(KubeContextName contextName)
            : this()
        {
            Covenant.Requires<ArgumentNullException>(contextName != null, nameof(contextName));

            this.Name = contextName.ToString();
        }

        /// <summary>
        /// The local nickname for the context.
        /// </summary>
        [JsonProperty(PropertyName = "name", Required = Required.Always)]
        [YamlMember(Alias = "name", ApplyNamingConventions = false)]
        public string Name { get; set; }

        /// <summary>
        /// The context properties.
        /// </summary>
        [JsonProperty(PropertyName = "context", Required = Required.Always)]
        [YamlMember(Alias = "context", ApplyNamingConventions = false)]
        public KubeConfigContextProperties Properties { get; set; }

        /// <summary>
        /// Lists any custom extension properties.  Extensions are name/value pairs added
        /// by vendors to hold arbitrary information.  Take care to choose property names
        /// that are unlikely to conflict with properties created by other vendors by adding
        /// a custom sffix like <b>my-property.neonkube.io</b>, where <b>my-property</b> 
        /// identifies the property and <b>neonkibe.io</b> helps avoid conflicts.
        /// </summary>
        [JsonProperty(PropertyName = "Extensions", Required = Required.Default)]
        [YamlMember(Alias = "extensions", ApplyNamingConventions = false)]
        public List<NamedExtension> Extensions { get; set; } = new List<NamedExtension>();

        /// <summary>
        /// Indicates whether the Kubernetes context references a neonKUBE cluster.
        /// </summary>
        [JsonIgnore]
        [YamlIgnore]
        public bool IsNeonKube => KubeContextName.Parse(Name).IsNeonKube && Extension != null;

        /// <summary>
        /// Indicates whether the Kubernetes context references a neon-desktop built-in cluster.
        /// </summary>
        [JsonIgnore]
        [YamlIgnore]
        public bool IsDesktop => IsNeonKube && Extension != null && Extension.ClusterDefinition.IsDesktop;

        /// <summary>
        /// The cluster login information for the context.
        /// </summary>
        [JsonIgnore]
        [YamlIgnore]
        public ClusterLogin Extension
        {
            get
            {
                if (cachedLogins != null)
                {
                    return cachedLogins;
                }

                if (loginsLoaded)
                {
                    return null;
                }

                var loginPath = KubeHelper.GetClusterLoginPath((KubeContextName)Name);

                if (File.Exists(loginPath))
                {
                    cachedLogins = NeonHelper.YamlDeserializeViaJson<ClusterLogin>(KubeHelper.ReadFileTextWithRetry(loginPath));

                    // Validate the extension's cluster definition.

                    cachedLogins.ClusterDefinition?.Validate();

                    // We need to fixup some references.

                    foreach (var nodeDefinition in cachedLogins.ClusterDefinition.NodeDefinitions.Values)
                    {
                        nodeDefinition.Labels.Node = nodeDefinition;
                    }
                }

                loginsLoaded = true;

                return cachedLogins;
            }

            set
            {
                loginsLoaded = true;
                cachedLogins       = value;
            }
        }
    }
}
