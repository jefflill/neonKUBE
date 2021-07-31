﻿//-----------------------------------------------------------------------------
// FILE:	    SetupClusterStatus.cs
// CONTRIBUTOR: Jeff Lill
// COPYRIGHT:	Copyright (c) 2005-2021 by neonFORGE LLC.  All rights reserved.
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
using System.Diagnostics.Contracts;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Newtonsoft.Json;

using Neon.Common;
using Neon.Data;

namespace Neon.Kube
{
    /// <summary>
    /// Describes the current state of cluster setup.
    /// </summary>
    public partial class SetupClusterStatus : NotifyPropertyChanged
    {
        private object              syncLock = new object();
        private bool                isClone;
        private ISetupController    controller;
        private ClusterProxy        cluster;
        private bool                isFaulted;
        private SetupStepStatus     currentStep;
        private string              globalStatus;

        /// <summary>
        /// Default constructor used by <see cref="Clone"/>.
        /// </summary>
        private SetupClusterStatus()
        {
            isClone = true;
        }

        /// <summary>
        /// Constructor.
        /// </summary>
        /// <param name="controller">The setup controller.</param>
        internal SetupClusterStatus(ISetupController controller)
        {
            Covenant.Requires<ArgumentNullException>(controller != null, nameof(controller));

            this.isClone      = false;
            this.controller   = controller;
            this.cluster      = controller.Get<ClusterProxy>(KubeSetupProperty.ClusterProxy);
            this.GlobalStatus = controller.GlobalStatus;
            this.CurrentStep  = Steps.SingleOrDefault(step => step.Number == controller.CurrentStepNumber);
            this.globalStatus = string.Empty;

            // Initialize the cluster nodes.

            this.Nodes = new List<SetupNodeStatus>();

            foreach (var node in cluster.Nodes)
            {
                Nodes.Add(new SetupNodeStatus(node, node.NodeDefinition));
            }

            // Initialize the setup steps.

            this.Steps = new List<SetupStepStatus>();

            foreach (var step in controller.GetStepStatus().Where(step => !step.IsQuiet))
            {
                Steps.Add(step);
            }
        }

        /// <summary>
        /// Indicates whether cluster setup has failed.
        /// </summary>
        public bool IsFaulted
        {
            get
            {
                lock (syncLock)
                {
                    return isFaulted;
                }
            }

            set
            {
                var changed = false;

                lock (syncLock)
                {
                    changed   = isFaulted != value;
                    isFaulted = value;
                }

                if (changed)
                {
                    RaisePropertyChanged();
                }
            }
        }

        /// <summary>
        /// Returns the current node setup state.
        /// </summary>
        public List<SetupNodeStatus> Nodes { get; private set; }

        /// <summary>
        /// Returns information about the setup steps in order of execution. 
        /// </summary>
        public List<SetupStepStatus> Steps { get; private set; }

        /// <summary>
        /// Returns the currently executing step status (or <c>null</c>).
        /// </summary>
        public SetupStepStatus CurrentStep
        {
            get => currentStep;

            set
            {
                if (value != currentStep)
                {
                    currentStep = value;
                    RaisePropertyChanged();
                }
            }
        }

        /// <summary>
        /// Returns any status for the overall setup operation.
        /// </summary>
        public string GlobalStatus
        {
            get => globalStatus;

            set
            {
                if (value != globalStatus)
                {
                    globalStatus = value;
                    RaisePropertyChanged();
                }
            }
        }

        /// <summary>
        /// Returns a deep(ish) clone of the instance.
        /// </summary>
        /// <returns>The clone.</returns>
        public SetupClusterStatus Clone()
        {
            Covenant.Assert(!isClone, "Cannot clone a cloned instance.");

            var clone = new SetupClusterStatus();

            clone.controller   = this.controller;
            clone.cluster      = this.cluster;
            clone.GlobalStatus = this.globalStatus;
            clone.currentStep  = this.currentStep;
            clone.globalStatus = this.globalStatus;

            // Initialize the cluster nodes.

            clone.Nodes = new List<SetupNodeStatus>();

            foreach (var node in this.Nodes)
            {
                clone.Nodes.Add(node.Clone());
            }

            // Initialize the setup steps.

            clone.Steps = new List<SetupStepStatus>();

            foreach (var step in this.Steps)
            {
                clone.Steps.Add(step.Clone());
            }

            return clone;
        }
    }
}
