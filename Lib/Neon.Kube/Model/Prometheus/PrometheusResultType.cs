﻿//-----------------------------------------------------------------------------
// FILE:	    PrometheusResultType.cs
// CONTRIBUTOR: Marcus Bowyer
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
// limitations under the License.

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
using YamlDotNet.Serialization;

using Neon.Common;
using Neon.IO;
using System.Runtime.Serialization;

namespace Neon.Kube
{
    /// <summary>
    /// Specifies the result type.
    /// </summary>
    [JsonConverter(typeof(StringEnumConverter))]
    public enum PrometheusResultType
    {
#pragma warning disable CS1570 // XML comment has badly formed XML
        /// <summary>
        /// The result is a matrix value.
        /// [
        ///     {
        ///         "metric": { "<label_name>": "<label_value>", ... },
        ///         "values": [ [ <unix_time>, "<sample_value>" ], ... ]
        ///     },
        ///      ...
        /// ]
        /// </summary>
        [EnumMember(Value = "matrix")]
        Matrix = 0,

        /// <summary>
        /// The result is a vector value.
        /// [
        ///     {
        ///         "metric": { "<label_name>": "<label_value>", ... },
        ///         "value": [ <unix_time>, "<sample_value>" ]
        ///     },
        ///      ...
        /// ]
        /// </summary>
        [EnumMember(Value = "vector")]
        Vector,

        /// <summary>
        /// The result is a scalar value. [ <unix_time>, "<scalar_value>" ]
        /// </summary>
        [EnumMember(Value = "scalar")]
        Scalar,

        /// <summary>
        /// The result is a string value. [ <unix_time>, "<string_value>" ]
        /// </summary>
        [EnumMember(Value = "string")]
        String
#pragma warning restore CS1570 // XML comment has badly formed XML
    }
}
