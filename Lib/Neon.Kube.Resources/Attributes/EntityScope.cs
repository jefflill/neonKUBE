//-----------------------------------------------------------------------------
// FILE:        EntityScope.cs
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
using System.Linq;
using System.Runtime.Serialization;
using System.Text;
using System.Threading.Tasks;

namespace Neon.Kube.Resources
{
    /// <summary>
    /// <para>
    /// Enumerates the possible the scopes for a Kubernetes resource.
    /// </para>
    /// <note>
    /// You may also use <b>"*"</b> to indicate that there's no scope restriction.
    /// </note>
    /// </summary>
    public enum EntityScope
    {
        /// <summary>
        /// The resource is Namespaced.
        /// </summary>
        [EnumMember(Value = "Namespaced")]
        Namespaced,
        
        /// <summary>
        /// The resource is cluster wide.
        /// </summary>
        [EnumMember(Value = "Cluster")]
        Cluster
    }
}
