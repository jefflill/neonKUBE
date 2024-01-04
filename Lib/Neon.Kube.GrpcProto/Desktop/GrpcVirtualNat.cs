//-----------------------------------------------------------------------------
// FILE:        GrpcVirtualNat.cs
// CONTRIBUTOR: Jeff Lill
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
using System.Linq;
using System.Runtime.Serialization;
using System.ServiceModel;
using System.Threading.Tasks;

using Neon.Common;
using Neon.Net;

using ProtoBuf.Grpc;

namespace Neon.Kube.GrpcProto.Desktop
{
    /// <summary>
    /// Describes a Hyper-V virtual NAT.
    /// </summary>
    [DataContract]
    public class GrpcVirtualNat
    {
        /// <summary>
        /// Default constructor.
        /// </summary>
        public GrpcVirtualNat()
        {
        }

        /// <summary>
        /// Constructor.
        /// </summary>
        /// <param name="name">The NAT's name.</param>
        /// <param name="subnet">The NAT source subnet.</param>
        public GrpcVirtualNat(string name, string subnet)
        {
            this.Name   = name;
            this.Subnet = subnet;
        }

        /// <summary>
        /// The NAT's name.
        /// </summary>
        [DataMember(Order = 1)]
        public string? Name { get; set; }

        /// <summary>
        /// The NAT source subnet.
        /// </summary>
        [DataMember(Order = 2)]
        public string? Subnet { get; set; }
    }
}
