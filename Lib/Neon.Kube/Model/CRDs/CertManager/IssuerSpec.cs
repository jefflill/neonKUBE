﻿//-----------------------------------------------------------------------------
// FILE:	    IssuerSpec.cs
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
using System.ComponentModel;
using System.Diagnostics.Contracts;
using System.Linq;
using System.Text;

using k8s;
using k8s.Models;
using Neon.JsonConverters;
using Newtonsoft.Json;

namespace Neon.Kube
{
    /// <summary>
    /// The kubernetes spec for a cert-manager Issuer.
    /// </summary>
    public class IssuerSpec
    {
        /// <summary>
        /// Constructor.
        /// </summary>
        public IssuerSpec()
        {
        }

        /// <summary>
        /// ACME configures this issuer to communicate with a RFC8555 (ACME) server to obtain signed x509 certificates.
        /// </summary>
        [JsonProperty(PropertyName = "Acme", Required = Required.Default, DefaultValueHandling = DefaultValueHandling.IgnoreAndPopulate)]
        [DefaultValue(null)]
        public AcmeIssuer Acme { get; set; } = null;

        /// <summary>
        /// Validates the options and also ensures that all <c>null</c> properties are
        /// initialized to their default values.
        /// </summary>
        /// <param name="clusterDefinition">The cluster definition.</param>
        /// <exception cref="ClusterDefinitionException">Thrown if the definition is not valid.</exception>
        public void Validate(ClusterDefinition clusterDefinition)
        {
            Covenant.Requires<ArgumentNullException>(clusterDefinition != null, nameof(clusterDefinition));

            var issuerSpecPrefix = $"{nameof(IssuerSpec)}";

            Acme = Acme ?? new AcmeIssuer();
            Acme.Validate(clusterDefinition);
        }
    }
}
