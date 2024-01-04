//-----------------------------------------------------------------------------
// FILE:        Oauth2ProxyHeaderValue.cs
// CONTRIBUTOR: Marcus Bowyer
// COPYRIGHT:   Copyright © 2005-2024 by NEONFORGE LLC.  All rights reserved.
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
using System.IO;
using System.Linq;
using System.Security.Claims;
using System.Text;

using Newtonsoft.Json;

using YamlDotNet.Serialization;

namespace Neon.Kube.Oauth2Proxy
{
    /// <summary>
    /// Oauth2Proxy header value model.
    /// </summary>
    public class Oauth2ProxyHeaderValue
    {
        /// <summary>
        /// Constructor.
        /// </summary>
        public Oauth2ProxyHeaderValue()
        {
        }

        /// <summary>
        /// A base64 encoded string value.
        /// </summary>
        [JsonProperty(PropertyName = "Value", Required = Required.Default, DefaultValueHandling = DefaultValueHandling.Ignore)]
        [YamlMember(Alias = "value", ApplyNamingConventions = false, DefaultValuesHandling = DefaultValuesHandling.OmitNull)]
        [DefaultValue(null)]
        public string Value { get; set; }

        /// <summary>
        /// Expects the name of an environment variable.
        /// </summary>
        [JsonProperty(PropertyName = "FromEnv", Required = Required.Default, DefaultValueHandling = DefaultValueHandling.Ignore)]
        [YamlMember(Alias = "fromEnv", ApplyNamingConventions = false, DefaultValuesHandling = DefaultValuesHandling.OmitNull)]
        [DefaultValue(false)]
        public string FromEnv { get; set; }

        /// <summary>
        /// Expects a path to a file containing the secret value.
        /// </summary>
        [JsonProperty(PropertyName = "FromFile", Required = Required.Default, DefaultValueHandling = DefaultValueHandling.Ignore)]
        [YamlMember(Alias = "fromFile", ApplyNamingConventions = false, DefaultValuesHandling = DefaultValuesHandling.OmitNull)]
        [DefaultValue(null)]
        public string FromFile { get; set; }

        /// <summary>
        /// The name of the claim in the session that the value should be loaded from.
        /// </summary>
        [JsonProperty(PropertyName = "Claim", Required = Required.Default, DefaultValueHandling = DefaultValueHandling.Ignore)]
        [YamlMember(Alias = "claim", ApplyNamingConventions = false, DefaultValuesHandling = DefaultValuesHandling.OmitNull)]
        [DefaultValue(null)]
        public string Claim { get; set; }

        /// <summary>
        /// An optional prefix that will be prepended to the value of the
        /// claim if it is non-empty.
        /// </summary>
        [JsonProperty(PropertyName = "Prefix", Required = Required.Default, DefaultValueHandling = DefaultValueHandling.Ignore)]
        [YamlMember(Alias = "prefix", ApplyNamingConventions = false, DefaultValuesHandling = DefaultValuesHandling.OmitNull)]
        [DefaultValue(null)]
        public string Prefix { get; set; }

        /// <summary>
        /// Converts this claim into a basic auth header.
        /// Note the value of claim will become the basic auth username and the
        /// basicAuthPassword will be used as the password value.
        /// </summary>
        [JsonProperty(PropertyName = "BasicAuthPassword", Required = Required.Default, DefaultValueHandling = DefaultValueHandling.Ignore)]
        [YamlMember(Alias = "basicAuthPassword", ApplyNamingConventions = false, DefaultValuesHandling = DefaultValuesHandling.OmitNull)]
        [DefaultValue(null)]
        public Oauth2ProxySecretSource BasicAuthPassword { get; set; }
    }
}
