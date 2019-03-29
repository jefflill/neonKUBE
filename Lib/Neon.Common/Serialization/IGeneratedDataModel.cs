﻿//-----------------------------------------------------------------------------
// FILE:	    IGeneratedDataModel.cs
// CONTRIBUTOR: Jeff Lill
// COPYRIGHT:	Copyright (c) 2016-2019 by neonFORGE, LLC.  All rights reserved.
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
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.Extensions.DependencyInjection;

using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json.Serialization;

using Neon.Diagnostics;

namespace Neon.Serialization
{
    /// <summary>
    /// Used by the <b>Neon.CodeGen</b> assembly to indicate that a class
    /// was generated as a data model.
    /// </summary>
    public interface IGeneratedDataModel
    {
        /// <summary>
        /// Renders the instance as JSON text, optionally formatting the output.
        /// </summary>
        /// <param name="indented">Optionally pass <c>true</c> to format the output.</param>
        /// <returns>The serialized JSON string.</returns>
        string ToString(bool indented);

        /// <summary>
        /// Renders the instance as a <see cref="JObject"/>.
        /// </summary>
        /// <returns>The cloned <see cref="JObject"/>.</returns>
        JObject ToJObject(bool noClone = false);
    }
}
