//-----------------------------------------------------------------------------
// FILE:        NodeLog.cs
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
using System.Diagnostics.Contracts;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Neon.Common;

namespace Neon.Kube.Setup
{
    /// <summary>
    /// Holds the setup related log for a specific cluster node.
    /// </summary>
    public struct NodeLog
    {
        /// <summary>
        /// Constructor.
        /// </summary>
        /// <param name="nodeName">The node name.</param>
        /// <param name="logText">The log text.</param>
        public NodeLog(string nodeName, string logText)
        {
            Covenant.Requires<ArgumentNullException>(!string.IsNullOrEmpty(nodeName), nameof(nodeName));

            this.NodeName = nodeName;
            this.LogText  = logText ?? string.Empty;
        }

        /// <summary>
        /// Returns the node name.
        /// </summary>
        public string NodeName { get; private set; }

        /// <summary>
        /// Returns the node's log text.
        /// </summary>
        public string LogText { get; private set; }
    }
}
