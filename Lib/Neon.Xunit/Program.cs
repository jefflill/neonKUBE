﻿//-----------------------------------------------------------------------------
// FILE:	    Program.cs
// CONTRIBUTOR: Jeff Lill
// COPYRIGHT:	Copyright (c) 2005-2020 by neonFORGE, LLC.  All rights reserved.
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
using System.IO;
using System.Text;
using System.Threading.Tasks;

namespace Neon.Xunit
{
    /// <summary>
    /// We apparently need a main program entry when building with the 
    /// <b>Microsoft.NET.Sdk.Web</b> SDK.  We'll fake one here.
    /// </summary>
    internal static class Program
    {
        /// <summary>
        /// Fake program entry point.
        /// </summary>
        /// <param name="args">Command line arguments.</param>
        /// <returns>The exit code.</returns>
        public static int Main(string[] args)
        {
            return 0;
        }
    }
}
