﻿//-----------------------------------------------------------------------------
// FILE:	    DateTimeOffsetJsonConverter.cs
// CONTRIBUTOR: Jeff Lill
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
using System.Diagnostics.Contracts;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using Newtonsoft.Json.Serialization;

namespace Neon.Data
{
    /// <summary>
    /// <b>Newtonsoft:</b> Implements a type converter for <see cref="DateTimeOffset"/> using the culture
    /// invariant <b>yyyy-MM-ddTHH:mm:ss.fffzzz</b> format.
    /// </summary>
    public class DateTimeOffsetJsonConverter : JsonConverter<DateTimeOffset>, IEnhancedJsonConverter
    {
        private const string format = "yyyy-MM-ddTHH:mm:ss.fffzzz";

        /// <inheritdoc/>
        public Type Type => typeof(DateTimeOffset);

        /// <inheritdoc/>
        public override DateTimeOffset ReadJson(JsonReader reader, Type objectType, DateTimeOffset existingValue, bool hasExistingValue, JsonSerializer serializer)
        {
            return (DateTimeOffset)reader.Value;
        }

        /// <inheritdoc/>
        public override void WriteJson(JsonWriter writer, DateTimeOffset value, JsonSerializer serializer)
        {
            writer.WriteValue(value.ToString(format));
        }

        /// <inheritdoc/>
        public string ToSimpleString(object instance)
        {
            Covenant.Requires<ArgumentNullException>(instance != null, nameof(instance));

            return ((DateTimeOffset)instance).ToString(format);
        }
    }
}
