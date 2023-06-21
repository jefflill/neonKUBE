//-----------------------------------------------------------------------------
// FILE:        PrometheusResponse.cs
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
using Newtonsoft.Json.Linq;

namespace Neon.Kube
{
    /// <summary>
    /// Models a prometheus time series value.
    /// </summary>
    [JsonConverter(typeof(PrometheusTimeSeriesValueConverter))]
    public struct PrometheusTimeSeriesValue
    {
        /// <summary>
        /// Constructor.
        /// </summary>
        public PrometheusTimeSeriesValue(int time, string value)
        {
            Time = time;
            Value = value;
        }

        /// <summary>
        ///  The Time.
        /// </summary>
        public int Time { get; set; }

        /// <summary>
        /// The value.
        /// </summary>
        public string Value { get; set; }
    }

    /// <summary>
    /// A JSON converter for converting PrometheusTimeSeriesValue.
    /// </summary>
    public class PrometheusTimeSeriesValueConverter : JsonConverter
    {
        /// <inheritdoc/>
        public override bool CanConvert(Type objectType)
        {
            return true;
        }

        /// <inheritdoc/>
        public override object ReadJson(JsonReader reader, Type objectType, object existingValue, JsonSerializer serializer)
        {
            JToken token = JToken.Load(reader);
            var time     = token.First.ToObject<int>();
            var value    = token.Last.ToObject<string>();

            return new PrometheusTimeSeriesValue(time, value);
        }

        /// <inheritdoc/>
        public override void WriteJson(JsonWriter writer, object value, JsonSerializer serializer)
        {
            var list = new List<object>()
            {
                ((PrometheusTimeSeriesValue)value).Time,
                ((PrometheusTimeSeriesValue)value).Value
            };
            serializer.Serialize(writer, list);
        }
    }
}


