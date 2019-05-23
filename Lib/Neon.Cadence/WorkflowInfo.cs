﻿//-----------------------------------------------------------------------------
// FILE:	    WorkflowInfo.cs
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
using System.ComponentModel;
using System.Diagnostics;
using System.Diagnostics.Contracts;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using Neon.Common;
using Neon.Diagnostics;
using Neon.IO;
using Neon.Net;
using Neon.Tasks;

using Neon.Cadence.Internal;

namespace Neon.Cadence
{
    /// <summary>
    /// Describes the current state of a workflow.
    /// </summary>
    public class WorkflowInfo
    {
        /// <summary>
        /// Describes the original workflow ID as well as the currrent run ID.
        /// </summary>
        public WorkflowRun Execution { get; set; }

        /// <summary>
        /// Identifies the workflow implementation.
        /// </summary>
        public string Name { get; set; }

        /// <summary>
        /// Workflow start time or <c>null</c> if the workflow hasn't started yet.
        /// </summary>
        public DateTime? StartTime { get; set; }

        /// <summary>
        /// Workflow close time or <c>null</c> if the workflow hasn't completed yet.
        /// </summary>
        public DateTime? CloseTime { get; set; }

        /// <summary>
        /// Returns <c>true</c> if the workflow has been started and is still running
        /// or has already completed.
        /// </summary>
        public bool HasStarted => StartTime != null;

        /// <summary>
        /// Returns <c>true</c> if the workflow has been completed.
        /// </summary>
        public bool IsClosed => CloseTime == null;

        /// <summary>
        /// Returns <c>true</c> if the workflow is currently running.
        /// </summary>
        public bool IsRunning => HasStarted && !IsClosed;

        /// <summary>
        /// The status for a closed workflow.
        /// </summary>
        public WorkflowCloseStatus WorkflowCloseStatus { get; set; }

        /// <summary>
        /// Workflow history length.
        /// </summary>
        public long HistoryLength { get; set; }

        /// <summary>
        /// Identifies the domain where the parent workflow is running.
        /// </summary>
        public string ParentDomain { get; set; }

        /// <summary>
        /// Identfies the parent workflow.
        /// </summary>
        public WorkflowRun ParentExecution { get; set; }

        /// <summary>
        /// The workflow execution time.
        /// </summary>
        public TimeSpan ExecutionTime { get; set; }

        /// <summary>
        /// Optional workflow metadata.
        /// </summary>
        public Dictionary<string, byte[]> Memo { get; set; }

        /// <summary>
        /// Not sure what these are.
        /// </summary>
        public List<ResetPoint> AutoResetPoints { get; set; }
    }
}
