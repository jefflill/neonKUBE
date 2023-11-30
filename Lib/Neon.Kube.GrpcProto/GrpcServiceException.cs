//-----------------------------------------------------------------------------
// FILE:        GrpcServiceException.cs
// CONTRIBUTOR: Jeff Lill
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
using System.ServiceModel;
using System.Threading.Tasks;

using Neon.Common;
using Neon.Net;

using ProtoBuf.Grpc;

namespace Neon.Kube.GrpcProto
{
    /// <summary>
    /// Thrown by <see cref="GrpcErrorExtensions.EnsureSuccess(GrpcError)"/> for non-null errors.
    /// </summary>
    public class GrpcServiceException : Exception
    {
        /// <summary>
        /// Constructor.
        /// </summary>
        /// <param name="message">The exception message.</param>
        /// <param name="innerException">Optionally specifies an inner exception.</param>
        public GrpcServiceException(string message, Exception? innerException = null)
            : base(message, innerException)
        {
        }
    }
}
