﻿//-----------------------------------------------------------------------------
// FILE:	    KubeSshKey.cs
// CONTRIBUTOR: Jeff Lill
// COPYRIGHT:	Copyright © 2005-2022 by NEONFORGE LLC.  All rights reserved.
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
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using Newtonsoft.Json.Serialization;
using YamlDotNet.Core;
using YamlDotNet.Serialization;

using Neon.Common;
using Neon.IO;

namespace Neon.Kube
{
    /// <summary>
    /// Describes a client key used for SSH public key authentication.
    /// </summary>
    /// <remarks>
    /// <note>
    /// Only <b>RSA</b> keys should be used in production.  Other keys like DSA are
    /// no longer considered secure.
    /// </note>
    /// <para>
    /// SSH authentication keys have two parts, the public key that needs to be deployed
    /// to every server machine and the private key that will be retained on client
    /// machines which will be used to sign authentication challenges by servers.
    /// </para>
    /// <para>
    /// The <see cref="PublicPUB"/> property holds the public key.  This key has a 
    /// standard format can can be appended directly to the <b>authorized_keys</b>
    /// file on a Linux machine.  <see cref="PrivateOpenSSH"/> holds the private key.
    /// </para>
    /// <para>
    /// <see cref="Passphrase"/> is not currently used but eventually, this will
    /// enable an additional level of encryption at rest.
    /// </para>
    /// </remarks>
    public class KubeSshKey
    {
        /// <summary>
        /// The RSA public key to deployed on the server for authenticating SSH clients.
        /// This is formatted as <b>PUB</b> format as generated by the Linux <b>ssh-keygen</b>
        /// tool.
        /// </summary>
        [JsonProperty(PropertyName = "PublicPUB", Required = Required.Default, DefaultValueHandling = DefaultValueHandling.IgnoreAndPopulate)]
        [YamlMember(Alias = "publicPUB", ScalarStyle = ScalarStyle.Literal, ApplyNamingConventions = false)]
        [DefaultValue(null)]
        public string PublicPUB { get; set; }

        /// <summary>
        /// The RSA public key to deployed on the server for authenticating SSH clients.
        /// This is formatted for <b>OpenSSH</b> as generated by the Linux <b>ssh-keygen</b>
        /// tool.
        /// </summary>
        [JsonProperty(PropertyName = "PublicOpenSSH", Required = Required.Default, DefaultValueHandling = DefaultValueHandling.IgnoreAndPopulate)]
        [YamlMember(Alias = "publicOpenSSH", ScalarStyle = ScalarStyle.Literal, ApplyNamingConventions = false)]
        [DefaultValue(null)]
        public string PublicOpenSSH { get; set; }

        /// <summary>
        /// The RSA public key formatted as <b>SSH2</b> as defined by <a href="https://tools.ietf.org/html/rfc4716">RFC 4716</a>.
        /// </summary>
        [JsonProperty(PropertyName = "PublicSSH2", Required = Required.Default, DefaultValueHandling = DefaultValueHandling.IgnoreAndPopulate)]
        [YamlMember(Alias = "publicSSH2", ScalarStyle = ScalarStyle.Literal, ApplyNamingConventions = false)]
        public string PublicSSH2 { get; set; }

        /// <summary>
        /// The private key formatted for <b>OpenSSH</b>.  
        /// </summary>
        [JsonProperty(PropertyName = "PrivateOpenSSH", Required = Required.Default, DefaultValueHandling = DefaultValueHandling.IgnoreAndPopulate)]
        [YamlMember(Alias = "privateOpenSSH", ScalarStyle = ScalarStyle.Literal, ApplyNamingConventions = false)]
        [DefaultValue(null)]
        public string PrivateOpenSSH { get; set; }

        /// <summary>
        /// The private key formatted for <b>SSH2</b> as defined by <a href="https://tools.ietf.org/html/rfc4716">RFC 4716</a>.
        /// </summary>
        [JsonProperty(PropertyName = "PrivatePEM", Required = Required.Default, DefaultValueHandling = DefaultValueHandling.IgnoreAndPopulate)]
        [YamlMember(Alias = "privatePEM", ScalarStyle = ScalarStyle.Literal, ApplyNamingConventions = false)]
        [DefaultValue(null)]
        public string PrivatePEM { get; set; }

        /// <summary>
        /// SHA256 fingerprint of the public key formatted as base64.
        /// </summary>
        [JsonProperty(PropertyName = "Fingerprint-SHA256", Required = Required.Default, DefaultValueHandling = DefaultValueHandling.IgnoreAndPopulate)]
        [YamlMember(Alias = "fingerprint-SHA256", ScalarStyle = ScalarStyle.Literal, ApplyNamingConventions = false)]
        [DefaultValue(null)]
        public string FingerprintSha256 { get; set; }

        /// <summary>
        /// MD5 fingerprint of the public key formatted as colon separated HEX bytes.
        /// </summary>
        [JsonProperty(PropertyName = "Fingerprint-MD5", Required = Required.Default, DefaultValueHandling = DefaultValueHandling.IgnoreAndPopulate)]
        [YamlMember(Alias = "fingerprint-MD5", ScalarStyle = ScalarStyle.Literal, ApplyNamingConventions = false)]
        [DefaultValue(null)]
        public string FingerprintMd5 { get; set; }

        /// <summary>
        /// <b>Not Implemented Yet:</b> The optional passphrase used for additional security.
        /// </summary>
        [JsonProperty(PropertyName = "Passphrase", Required = Required.Default, DefaultValueHandling = DefaultValueHandling.IgnoreAndPopulate)]
        [YamlMember(Alias = "passphrase", ApplyNamingConventions = false)]
        [DefaultValue(null)]
        public string Passphrase { get; set; }
    }
}
