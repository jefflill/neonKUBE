﻿//-----------------------------------------------------------------------------
// FILE:	    NeonVersionException.cs
// CONTRIBUTOR: Jeff Lill
// COPYRIGHT:	Copyright (c) 2016-2018 by neonFORGE, LLC.  All rights reserved.

using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;

using Neon.Common;

namespace Neon.Cluster
{
    /// <summary>
    /// Indicates a client or other version incompatiblity.
    /// </summary>
    public class NeonVersionException : Exception
    {
        /// <summary>
        /// Default constructor.
        /// </summary>
        public NeonVersionException()
        {
        }

        /// <summary>
        /// Constructs and instance with a message and an optional inner exception.
        /// </summary>
        /// <param name="message">The message.</param>
        /// <param name="innerException">The optional inner exception.</param>
        public NeonVersionException(string message, Exception innerException = null)
            : base(message, innerException)
        {
        }
    }
}
