﻿//-----------------------------------------------------------------------------
// FILE:	    NodeTaskController.cs
// CONTRIBUTOR: Jeff Lill
// COPYRIGHT:   Copyright (c) 2005-2022 by neonFORGE LLC.  All rights reserved.
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
using System.Diagnostics;
using System.Diagnostics.Contracts;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;

using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.JsonPatch;
using Microsoft.Extensions.Logging;

using JsonDiffPatch;

using Neon.Common;
using Neon.Diagnostics;
using Neon.IO;
using Neon.Kube;
using Neon.Kube.Operator;
using Neon.Kube.ResourceDefinitions;
using Neon.Retry;
using Neon.Tasks;
using Neon.Time;

using k8s;
using k8s.Models;

using KubeOps.Operator.Controller;
using KubeOps.Operator.Controller.Results;
using KubeOps.Operator.Finalizer;
using KubeOps.Operator.Rbac;

using Newtonsoft.Json;
using Prometheus;

namespace NeonNodeAgent
{
    /// <summary>
    /// Manages <see cref="V1NeonNodeTask"/> command execution on cluster nodes.
    /// </summary>
    /// <remarks>
    /// This controller relies on a lease named like <b>neon-node-agent.nodetask-NODENAME</b> where <b>NODENAME</b>
    /// is the name of the node where the <b>neon-node-agent</b> operator is running.  This lease will be
    /// persisted in the <see cref="KubeNamespace.NeonSystem"/> namespace and will be used to
    /// elect a leader for the node in case there happens to be two agents running on the same
    /// node for some reason.
    /// </remarks>
    [EntityRbac(typeof(V1NeonNodeTask), Verbs = RbacVerb.Get | RbacVerb.List | RbacVerb.Patch | RbacVerb.Watch | RbacVerb.Update)]
    public class NodeTaskController : IResourceController<V1NeonNodeTask>, IExtendedController<V1NeonNodeTask>
    {
        //---------------------------------------------------------------------
        // Static members

        private static readonly INeonLogger log = Program.Service.LogManager.GetLogger<NodeTaskController>();

        private static ResourceManager<V1NeonNodeTask, NodeTaskController>  resourceManager;

        // Paths to relevant folders in the host file system.

        private static readonly string      hostNeonRunFolder;
        private static readonly string      hostAgentFolder;
        private static readonly string      hostAgentTasksFolder;

        /// <summary>
        /// Static constructor.
        /// </summary>
        static NodeTaskController()
        {
            hostNeonRunFolder    = Path.Combine(Node.HostMount, KubeNodeFolder.NeonRun.Substring(1));
            hostAgentFolder      = Path.Combine(hostNeonRunFolder, "node-agent");
            hostAgentTasksFolder = Path.Combine(hostAgentFolder, "node-tasks");
        }

        /// <summary>
        /// Starts the controller.
        /// </summary>
        /// <param name="k8s">The <see cref="IKubernetes"/> client to use.</param>
        /// <returns>The tracking <see cref="Task"/>.</returns>
        public static async Task StartAsync(IKubernetes k8s)
        {
            Covenant.Requires<ArgumentNullException>(k8s != null, nameof(k8s));

            // Ensure that the [/var/run/neonkube/neon-node-agent/nodetask] folder exists on the node.

            var scriptPath = Path.Combine(Node.HostMount, $"tmp/node-agent-folder-{NeonHelper.CreateBase36Guid()}.sh");
            var script     =
$@"#!/bin/bash

set -euo pipefail

# Ensure that the node runtime folders exist and have the correct permissions.

if [ ! -d {hostNeonRunFolder} ]; then

mkdir -p {hostNeonRunFolder}
chmod 700 {hostNeonRunFolder}
fi

if [ ! -d {hostAgentFolder} ]; then

mkdir -p {hostAgentFolder}
chmod 700 {hostAgentFolder}
fi

if [ ! -d {hostAgentTasksFolder} ]; then

mkdir -p {hostAgentTasksFolder}
chmod 700 {hostAgentTasksFolder}
fi

# Remove this script.

rm $0
";
            File.WriteAllText(scriptPath, NeonHelper.ToLinuxLineEndings(script));
            try
            {
                Node.BashExecuteCapture(scriptPath).EnsureSuccess();
            }
            finally
            {
                NeonHelper.DeleteFile(scriptPath);
            }

            // Load the configuration settings.

            var leaderConfig = 
                new LeaderElectionConfig(
                    k8s,
                    @namespace: KubeNamespace.NeonSystem,
                    leaseName:        $"{Program.Service.Name}.nodetask-{Node.Name}",
                    identity:         Pod.Name,
                    promotionCounter: Metrics.CreateCounter($"{Program.Service.MetricsPrefix}nodetask_promoted", "Leader promotions"),
                    demotionCounter:  Metrics.CreateCounter($"{Program.Service.MetricsPrefix}nodetask_demoted", "Leader demotions"),
                    newLeaderCounter: Metrics.CreateCounter($"{Program.Service.MetricsPrefix}nodetask_newLeader", "Leadership changes"));

            var options = new ResourceManagerOptions()
            {
                Mode                       = ResourceManagerMode.Normal,
                IdleInterval               = Program.Service.Environment.Get("NODETASK_IDLE_INTERVAL", TimeSpan.FromSeconds(60)),
                ErrorMinRequeueInterval    = Program.Service.Environment.Get("NODETASK_ERROR_MIN_REQUEUE_INTERVAL", TimeSpan.FromSeconds(15)),
                ErrorMaxRetryInterval      = Program.Service.Environment.Get("NODETASK_ERROR_MAX_REQUEUE_INTERVAL", TimeSpan.FromSeconds(60)),
                ReconcileErrorCounter      = Metrics.CreateCounter($"{Program.Service.MetricsPrefix}nodetask_reconciled_error", "Failed NodeTask reconcile event processing."),
                DeleteErrorCounter         = Metrics.CreateCounter($"{Program.Service.MetricsPrefix}nodetask_deleted_error", "Failed NodeTask deleted event processing."),
                StatusModifiedErrorCounter = Metrics.CreateCounter($"{Program.Service.MetricsPrefix}nodetask_statusmodified_error", "Failed NodeTask status-modified events processing.")
            };

            resourceManager = new ResourceManager<V1NeonNodeTask, NodeTaskController>(
                k8s,
                options:      options,
                filter:       NodeTaskFilter,
                leaderConfig: leaderConfig);

            await resourceManager.StartAsync();
        }

        /// <summary>
        /// Selects only tasks assigned to the current node to be handled by the resource manager.
        /// </summary>
        /// <param name="task">The task being filtered.</param>
        /// <returns><b>true</b> if the task is assigned to the current node.</returns>
        private static bool NodeTaskFilter(V1NeonNodeTask task)
        {
            Covenant.Requires<ArgumentNullException>(task != null, nameof(task));

            return task.Spec.Node.Equals(Node.Name, StringComparison.InvariantCultureIgnoreCase);
        }

        //---------------------------------------------------------------------
        // Instance members

        private readonly IKubernetes k8s;

        /// <summary>
        /// Constructor.
        /// </summary>
        public NodeTaskController(IKubernetes k8s)
        {
            Covenant.Requires(k8s != null, nameof(k8s));
            Covenant.Requires<InvalidOperationException>(resourceManager != null, $"[{nameof(NodeTaskController)}] must be started before KubeOps.");

            this.k8s = k8s;
        }

        /// <summary>
        /// Called for each existing custom resource when the controller starts so that the controller
        /// can maintain the status of all resources and then afterwards, this will be called whenever
        /// a resource is added or has a non-status update.
        /// </summary>
        /// <param name="resource">The new entity or <c>null</c> when nothing has changed.</param>
        /// <returns>The controller result.</returns>
        public async Task<ResourceControllerResult> ReconcileAsync(V1NeonNodeTask resource)
        {
            return await resourceManager.ReconciledAsync(resource,
                async (resource, resources) =>
                {
                    var name = resource?.Name();

                    log.LogInfo($"RECONCILED: {name ?? "[IDLE]"} count={resources.Count}");

                    if (name == null)
                    {
                        // This is an IDLE event: we'll use this as a signal to do any cleanup.

                        // Execute the youngest node task that's pending (if there is one).

                        await CleanupTasksAsync(resources);
                    }
                    else
                    {
                        // We have a new node task targeting the host node:
                        //
                        //      1. Ensure that it's valid, delete if bad
                        //      2. Add a status property as necessary
                        //      3. Remove the task if it's been retained long enough
                        //      4. Execute the task if it's pending

                        var nodeTask = resources[name];

                        // Verify that task is well structured.

                        try
                        {
                            nodeTask.Validate();
                        }
                        catch (Exception e)
                        {
                            log.LogWarn($"Invalid NodeTask: [{name}]", e);
                            log.LogWarn($"Deleting invalid NodeTask: [{name}]");
                            await k8s.DeleteClusterCustomObjectAsync(nodeTask);

                            return null;
                        }

                        // For new tasks, update the status to PENDING and also add the
                        // node's owner reference to the object.
                        
                        if (nodeTask.Status.Phase == V1NeonNodeTask.Phase.New)
                        {
                            var patch = OperatorHelper.CreatePatch<V1NeonNodeTask>();

                            patch.Replace(path => path.Status, new V1NeonNodeTask.TaskStatus());
                            patch.Replace(path => path.Status.Phase, V1NeonNodeTask.Phase.Pending);

                            nodeTask = await k8s.PatchClusterCustomObjectStatusAsync<V1NeonNodeTask>(OperatorHelper.ToV1Patch<V1NeonNodeTask>(patch), nodeTask.Name());

                            var nodeOwnerReference = await Node.GetOwnerReferenceAsync(k8s);

                            if (nodeOwnerReference != null)
                            {
                                if (nodeTask.Metadata.OwnerReferences == null)
                                {
                                    nodeTask.Metadata.OwnerReferences = new List<V1OwnerReference>();
                                }

                                nodeTask.Metadata.OwnerReferences.Add(await Node.GetOwnerReferenceAsync(k8s));
                            }

                            nodeTask = await k8s.ReplaceClusterCustomObjectAsync<V1NeonNodeTask>(nodeTask, nodeTask.Name());
                        }

                        if (nodeTask.Status.FinishTimestamp.HasValue)
                        {
                            var retentionTime = DateTime.UtcNow - nodeTask.Status.FinishTimestamp;

                            if (retentionTime >= nodeTask.Spec.GetRetentionTime())
                            {
                                log.LogInfo($"NodeTask [{name}] retained for [{retentionTime}] (deleting now).");
                                await k8s.DeleteClusterCustomObjectAsync(nodeTask);

                                return null;
                            }
                        }

                        // Execute the task if it's pending.

                        if (nodeTask.Status.Phase == V1NeonNodeTask.Phase.Pending)
                        {
                            await ExecuteTaskAsync(nodeTask);
                        }
                    }

                    return null;
                });
        }

        /// <summary>
        /// Called when a custom resource is removed from the API Server.
        /// </summary>
        /// <param name="resource">The deleted entity.</param>
        /// <returns>The tracking <see cref="Task"/>.</returns>
        public async Task DeletedAsync(V1NeonNodeTask resource)
        {
            Covenant.Requires<ArgumentNullException>(resource != null, nameof(resource));
            
            await resourceManager.DeletedAsync(resource,
                async (name, resources) =>
                {
                    log.LogInfo($"DELETED: {name}");

                    // This is a NOP.

                    await Task.CompletedTask;
                });
        }

        /// <summary>
        /// Called when a custom resource's status has been modified.
        /// </summary>
        /// <param name="resource">The updated entity.</param>
        /// <returns>The tracking <see cref="Task"/>.</returns>
        public async Task StatusModifiedAsync(V1NeonNodeTask resource)
        {
            Covenant.Requires<ArgumentNullException>(resource != null, nameof(resource));

            await resourceManager.DeletedAsync(resource,
                async (name, resources) =>
                {
                    log.LogInfo($"STATUS-MODIFIED: {name}");

                    // This is a NOP.

                    await Task.CompletedTask;
                });
        }

        /// <summary>
        /// <para>
        /// Handles the cleanup of tasks targeting the current cluster node:
        /// </para>
        /// <list type="bullet">
        /// <item>
        /// Tasks whose <see cref="V1NeonNodeTask.TaskStatus.AgentId"/> doesn't match
        /// the ID for the current agent will be marked as <see cref="V1NeonNodeTask.Phase.Orphaned"/>
        /// and the finish time will be set to now.  This sets the task up for eventual
        /// deletion.
        /// </item>
        /// <item>
        /// Tasks with a finish time that is older than <see cref="V1NeonNodeTask.TaskSpec.RetentionTime"/>
        /// will be removed.
        /// </item>
        /// </list>
        /// </summary>
        /// <param name="nodeTasks">The existing tasks.</param>
        /// <returns>The tracking <see cref="Task"/>.</returns>
        private async Task CleanupTasksAsync(IReadOnlyDictionary<string, V1NeonNodeTask> nodeTasks)
        {
            Covenant.Requires<ArgumentNullException>(nodeTasks != null, nameof(nodeTasks));

            var utcNow = DateTime.UtcNow;

log.LogDebug($"CLEANUP: 0: count={nodeTasks.Count}");
            foreach (var nodeTask in nodeTasks.Values)
            {
                var taskName = nodeTask.Name();
log.LogDebug($"CLEANUP: 1: {taskName} phase={nodeTask.Status?.Phase}");

                //-------------------------------------------------------------
                // Remove invalid tasks.

                try
                {
log.LogDebug($"CLEANUP: 2:");
                    nodeTask.Validate();
                }
                catch (Exception e)
                {
                    log.LogWarn($"Invalid NodeTask: [{taskName}]", e);
                    log.LogWarn($"Deleting invalid NodeTask: [{taskName}]");
                    await k8s.DeleteClusterCustomObjectAsync(nodeTask);
                    continue;
                }
log.LogDebug($"CLEANUP: 3:");

                if (nodeTask.Status.Phase == V1NeonNodeTask.Phase.Running)
                {
log.LogDebug($"CLEANUP: 4:");
                    //---------------------------------------------------------
                    // Detect and kill orphaned tasks.

                    if (nodeTask.Status.AgentId != Node.AgentId)
                    {
log.LogDebug($"CLEANUP: 5A:");
                        log.LogWarn($"Detected orphaned [nodetask={taskName}]: task [agentID={nodeTask.Status.AgentId}] does not match operator [agentID={Node.AgentId}]");
                        await KillTaskAsync(nodeTask);
log.LogDebug($"CLEANUP: 5B:");

                        // Update the node task status to: ORPHANED

                        var patch = OperatorHelper.CreatePatch<V1NeonNodeTask>();

                        patch.Replace(path => path.Status.Phase, V1NeonNodeTask.Phase.Orphaned);
                        patch.Replace(path => path.Status.FinishTimestamp, utcNow);
                        patch.Replace(path => path.Status.ExitCode, -1);

                        await k8s.PatchClusterCustomObjectStatusAsync<V1NeonNodeTask>(OperatorHelper.ToV1Patch<V1NeonNodeTask>(patch), nodeTask.Name());
log.LogDebug($"CLEANUP: 6:");
                        continue;
                    }
log.LogDebug($"CLEANUP: 7:");

                    //---------------------------------------------------------
                    // Kill tasks that have been running for too long.

                    if (utcNow - nodeTask.Status.StartTimestamp >= nodeTask.Spec.GetTimeout())
                    {
log.LogDebug($"CLEANUP: 8:");
                        log.LogWarn($"Detected timeout [nodetask={taskName}]: execution time exceeds [{nodeTask.Spec.Timeout}].");
                        await KillTaskAsync(nodeTask);

                        // Update the node task status to: TIMEOUT

                        var patch = OperatorHelper.CreatePatch<V1NeonNodeTask>();

                        patch.Replace(path => path.Status.Phase, V1NeonNodeTask.Phase.Timeout);
                        patch.Replace(path => path.Status.FinishTimestamp, utcNow);
                        patch.Replace(path => path.Status.Runtime, GoDuration.FromTimeSpan((utcNow - nodeTask.Status.StartTimestamp.Value)).ToPretty());
                        patch.Replace(path => path.Status.ExitCode, -1);

                        await k8s.PatchClusterCustomObjectStatusAsync<V1NeonNodeTask>(OperatorHelper.ToV1Patch<V1NeonNodeTask>(patch), nodeTask.Name());
                        continue;
                    }
                }
            }
log.LogDebug($"CLEANUP: 9:");

            //-----------------------------------------------------------------
            // Remove tasks that have been retained long enough.

            foreach (var nodeTask in nodeTasks.Values
                .Where(task => task.Status.Phase != V1NeonNodeTask.Phase.New && task.Status.Phase != V1NeonNodeTask.Phase.Running)
                .Where(task => (utcNow - task.Status.FinishTimestamp) >= task.Spec.GetRetentionTime()))
            {
                await k8s.DeleteClusterCustomObjectAsync(nodeTask);
            }

            //-----------------------------------------------------------------
            // Remove any script folders whose node task no longer exists.

            var nodeTaskExecuteIds = new HashSet<string>();

            foreach (var nodeTask in nodeTasks.Values.Where(task => !string.IsNullOrEmpty(task.Status.RunId)))
            {
                nodeTaskExecuteIds.Add(nodeTask.Status.RunId);
            }

            foreach (var scriptFolderPath in Directory.GetDirectories(hostAgentTasksFolder, "*", SearchOption.TopDirectoryOnly))
            {
                var scriptFolderName = LinuxPath.GetFileName(scriptFolderPath);

                if (!nodeTaskExecuteIds.Contains(scriptFolderName))
                {
                    log.LogWarn($"Removing node task host script folder: {scriptFolderName}");
                    NeonHelper.DeleteFolder(scriptFolderPath);
                }
            }
log.LogDebug($"CLEANUP: 10:");
        }

        /// <summary>
        /// Kills the node task's process if it is running.
        /// </summary>
        /// <param name="nodeTask">The node task.</param>
        /// <returns>The tracking <see cref="Task"/>.</returns>
        private async Task KillTaskAsync(V1NeonNodeTask nodeTask)
        {
            Covenant.Requires<ArgumentNullException>(nodeTask != null, nameof(nodeTask));

            var taskName = nodeTask.Name();

log.LogDebug($"KILL: 0: {taskName}");
            if (nodeTask.Status != null && nodeTask.Status.Phase != V1NeonNodeTask.Phase.Running)
            {
log.LogDebug($"KILL: 1:");
                return;
            }
log.LogDebug($"KILL: 2: processID={nodeTask.Status.ProcessId}");

            // Try to locate the task process by process ID and command line.  Note that
            // we can't use the process ID by itself because it possible that the process
            // ID has been recycled and is currently assigned to an entirely unrelated
            // process.
            //
            // We're going to use the [ps --pid=PROCESSID --format cmd=] command.  This
            // will return an empty line when the process doesn't exist and a single line
            // with the process command line when the process exists.

            var result = await Node.ExecuteCaptureAsync("ps", new object[] { $"--pid {nodeTask.Status.ProcessId}", "--format cmd=" });
log.LogDebug($"KILL: 3A: {result.ExitCode}");
log.LogDebug($"KILL: 3B: {result.OutputText}");
log.LogDebug($"KILL: 3C: {result.ErrorText}");

            if (result.ExitCode == 0)
            {
                using (var reader = new StringReader(result.OutputText))
                {
                    var commandLine = reader.Lines().FirstOrDefault();

log.LogDebug($"KILL: 4:");
                    if (commandLine == nodeTask.Status.CommandLine)
                    {
log.LogDebug($"KILL: 5:");
                        // The process ID and command line match, so kill it.

                        result = await Node.ExecuteCaptureAsync("kill", new object[] { "-s", "SIGTERM", nodeTask.Status.ProcessId });

log.LogDebug($"KILL: 6:");
                        if (result.ExitCode != 0)
                        {
log.LogDebug($"KILL: 7:");
                            log.LogWarn($"[NodeTask: {taskName}]: Cannot kill orphaned task process [{nodeTask.Status.ProcessId}]. [exitcode={result.ExitCode}]");
                            return;
                        }
                    }
                }
            }
log.LogDebug($"KILL: 8:");
        }

        /// <summary>
        /// Initiates execution of a node task in the background when the task is still pending.
        /// </summary>
        /// <param name="nodeTask">The node task.</param>
        /// <returns>The tracking <see cref="Task"/>.</returns>
        private async Task ExecuteTaskAsync(V1NeonNodeTask nodeTask)
        {
            Covenant.Requires<ArgumentNullException>(nodeTask != null, nameof(nodeTask));

            var taskName = nodeTask.Name();

            if (nodeTask.Status.Phase != V1NeonNodeTask.Phase.Pending)
            {
                return;
            }

            // Start and execute the command.  The trick here is that we need the
            // ID of the process launched before we can update the status.

            var process = (Process)null;

            // Generate the execution UUID and determine where the script will be located.

            var executionId = Guid.NewGuid().ToString("d");
            var taskFolder  = LinuxPath.Combine(hostAgentTasksFolder, executionId);
            var scriptPath  = LinuxPath.Combine(taskFolder, "task.sh");

            // Prepend the script to be deployed with code that sets the special
            // environment variables.

            var deployedScript =
$@"
#------------------------------------------------------------------------------
# neon-node-task: Initialze special script variables

export NODE_ROOT={Node.HostMount}
export SCRIPT_DIR={taskFolder}

#------------------------------------------------------------------------------

{nodeTask.Spec.BashScript}
";
            Directory.CreateDirectory(taskFolder);
            File.WriteAllText(scriptPath, NeonHelper.ToLinuxLineEndings(deployedScript));

            // Start the command process.

            var task = (Task<ExecuteResponse>)null;

            try
            {
                // This callback will be executed once the [Node.ExecuteCaptureAsync()]
                // call has the process details.  We'll save the details, update the node task
                // status and persist the status changes to the API server.

                var processCallback =
                    (Process newProcess) =>
                    {
                        process = newProcess;

                        log.LogInfo($"Starting [nodetask={taskName}]: [command={nodeTask.Status.CommandLine}] [processID={process.Id}]");
                    };

                task = Node.BashExecuteCaptureAsync(
                    path:            scriptPath, 
                    timeout:         nodeTask.Spec.GetTimeout(),
                    processCallback: processCallback);
            }
            catch (Exception e)
            {
                // We shouldn't ever see an error here because [/bin/bash] should always
                // exist, but we'll log something just in case.

                log.LogWarn(e);

                var failedPatch = OperatorHelper.CreatePatch<V1NeonNodeTask>();

                failedPatch.Replace(path => path.Status.Phase, V1NeonNodeTask.Phase.Failed);
                failedPatch.Replace(path => path.Status.FinishTimestamp, DateTime.UtcNow);
                failedPatch.Replace(path => path.Status.ExitCode, -1);
                failedPatch.Replace(path => path.Status.Error, $"EXECUTE FAILED: {e.Message}");

                await k8s.PatchClusterCustomObjectStatusAsync<V1NeonNodeTask>(OperatorHelper.ToV1Patch<V1NeonNodeTask>(failedPatch), nodeTask.Name());
                return;
            }

            // We need to wait for the [Node.BashExecuteCaptureAsync()] call above to 
            // report the process for the executed script.

            try
            {
                NeonHelper.WaitFor(() => process != null, timeout: TimeSpan.FromSeconds(15), pollInterval: TimeSpan.FromMilliseconds(150));
            }
            catch (TimeoutException e)
            {
                // It's possible but unlikely that the update above failed for some reason.
                // We'll log this and then hope for the best.

                log.LogWarn(e);
            }

            // Update the node task status to: RUNNING

            var patch = OperatorHelper.CreatePatch<V1NeonNodeTask>();

            patch.Replace(path => path.Status.Phase, V1NeonNodeTask.Phase.Running);
            patch.Replace(path => path.Status.StartTimestamp, DateTime.UtcNow);
            patch.Replace(path => path.Status.AgentId, Node.AgentId);
            patch.Replace(path => path.Status.CommandLine, Node.GetBashCommandLine(scriptPath).Trim());
            patch.Replace(path => path.Status.RunId, executionId);

            nodeTask = await k8s.PatchClusterCustomObjectStatusAsync<V1NeonNodeTask>(OperatorHelper.ToV1Patch<V1NeonNodeTask>(patch), nodeTask.Name());

            // Wait for the command to complete and the update the node task status.

            try
            {
                var result  = (ExecuteResponse)null;
                var timeout = false;

                try
                {
                    result = await task;

                    log.LogInfo($"Finished [nodetask={taskName}]: [command={nodeTask.Status.CommandLine}] [exitcode={result.ExitCode}]");
                }
                catch (TimeoutException)
                {
                    timeout = true;

                    log.LogWarn($"Timeout [nodetask={taskName}]");
                }

                if (nodeTask.Status.Phase == V1NeonNodeTask.Phase.Running)
                {
                    nodeTask.Status.FinishTimestamp = DateTime.UtcNow;
                    nodeTask.Status.SetRuntime(nodeTask.Status.FinishTimestamp.Value - nodeTask.Status.StartTimestamp.Value);

                    patch = OperatorHelper.CreatePatch<V1NeonNodeTask>();

                    patch.Replace(path => path.Status.FinishTimestamp, DateTime.UtcNow);

                    if (timeout)
                    {
                        patch.Replace(path => path.Status.Phase, V1NeonNodeTask.Phase.Timeout);
                        patch.Replace(path => path.Status.Runtime, GoDuration.FromTimeSpan(DateTime.UtcNow - nodeTask.Status.StartTimestamp.Value).ToPretty());
                        patch.Replace(path => path.Status.ExitCode, -1);
                    }
                    else
                    {
                        patch.Replace(path => path.Status.Phase, result.ExitCode == 0 ? V1NeonNodeTask.Phase.Finished : V1NeonNodeTask.Phase.Failed);
                        patch.Replace(path => path.Status.Runtime, GoDuration.FromTimeSpan(nodeTask.Status.FinishTimestamp.Value - nodeTask.Status.StartTimestamp.Value).ToPretty());
                        patch.Replace(path => path.Status.ExitCode, result.ExitCode);

                        if (nodeTask.Spec.CaptureOutput)
                        {
                            patch.Replace(path => path.Status.Output, result.OutputText);
                            patch.Replace(path => path.Status.Error, result.ErrorText);
                        }
                    }

                    nodeTask = await k8s.PatchClusterCustomObjectStatusAsync<V1NeonNodeTask>(OperatorHelper.ToV1Patch<V1NeonNodeTask>(patch), nodeTask.Name());
                }
            }
            catch (Exception e)
            {
                log.LogWarn(e);
            }
        }

        /// <inheritdoc/>
        public V1NeonNodeTask CreateIgnorable()
        {
            var ignorable = new V1NeonNodeTask();

            ignorable.Spec.Node          = "ignored";
            ignorable.Spec.BashScript    = "ignored";
            ignorable.Spec.Timeout       = "0s";
            ignorable.Spec.RetentionTime = "0s";
            ignorable.Spec.CaptureOutput = false;

            return ignorable;
        }
    }
}
