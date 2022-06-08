﻿//-----------------------------------------------------------------------------
// FILE:	    PrometheusResponseStatus.cs
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
    /// Specifies response status from Prometheus HTTP API.
    /// </summary>
    [JsonConverter(typeof(StringEnumConverter))]
    public enum PrometheusResponseStatus
    {
        /// <summary>
        /// Indicates that the request was processed successfully.
        /// </summary>
        [EnumMember(Value = "success")]
        Success = 0,

        /// <summary>
        /// Indicates that there was an error processing the request.
        /// </summary>
        [EnumMember(Value = "error")]
        Error
    }
}
