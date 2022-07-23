﻿//-----------------------------------------------------------------------------
// FILE:	    DnsEndpointSpec.cs
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
using System.Linq;
using System.Text;

using k8s;
using k8s.Models;

namespace Neon.Kube
{
    /// <summary>
    /// The Endpoint Spec.
    /// </summary>
    public class DnsEndpointSpec
    {
        /// <summary>
        /// Constructor.
        /// </summary>
        public DnsEndpointSpec()
        {
        }

        /// <summary>
        /// The list of <see cref="DnsEndpoint"/>.
        /// </summary>
        public List<DnsEndpoint> Endpoints { get; set; }
    }
}
