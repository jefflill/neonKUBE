﻿//-----------------------------------------------------------------------------
// FILE:	    ContainerRegistryController.cs
// CONTRIBUTOR: Jeff Lill
// COPYRIGHT:   Copyright (c) 2005-2022 by neonFORGE LLC.  All rights reserved.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using Neon.Common;
using Neon.Diagnostics;
using Neon.IO;
using Neon.Kube;
using Neon.Kube.Resources;
using Neon.Kube.Operator;

using k8s.Models;

using KubeOps.Operator.Controller;
using KubeOps.Operator.Controller.Results;
using KubeOps.Operator.Finalizer;
using KubeOps.Operator.Rbac;

using Prometheus;

namespace NeonNodeAgent
{
    /// <summary>
    /// Manages <see cref="V1ContainerRegistry"/> entities on the Kubernetes API Server.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This operator controller is responsible for managing the upstream CRI-O container registry
    /// configuration located at <b>/etc/containers/registries.conf.d/00-neon-cluster.conf</b>.
    /// on the host node.
    /// </para>
    /// <note>
    /// The host node file system is mounted into the container at: <see cref="Program.HostMount"/>.
    /// </note>
    /// <para>
    /// This works by monitoring by:
    /// </para>
    /// <list type="bullet">
    /// <item>
    /// Monitoring the <see cref="V1ContainerRegistry"/> resources for potential changes
    /// and then performing the steps below a change is detected.
    /// </item>
    /// <item>
    /// Regenerate the contents of the <b>/etc/containers/registries.conf.d/00-neon-cluster.conf</b> file.
    /// </item>
    /// <item>
    /// Compare the contents of the current file with the new generated config.
    /// </item>
    /// <item>
    /// If the contents differ, update the file on the host's filesystem and then signal
    /// CRI-O to reload its configuration.
    /// </item>
    /// </list>
    /// </remarks>
    [EntityRbac(typeof(V1ContainerRegistry), Verbs = RbacVerb.Get | RbacVerb.List | RbacVerb.Watch | RbacVerb.Update)]
    public class ContainerRegistryController : IResourceController<V1ContainerRegistry>
    {
        //---------------------------------------------------------------------
        // Static members

        private static readonly INeonLogger                             log             = Program.Service.LogManager.GetLogger<ContainerRegistryController>();
        private static readonly ResourceManager<V1ContainerRegistry>    resourceManager = new ResourceManager<V1ContainerRegistry>();
        private static readonly string                                  configMountPath = LinuxPath.Combine(Program.HostMount, "etc/containers/registries.conf.d/00-neon-cluster.conf");

        // Configuration settings

        private static bool         configured = false;
        private static TimeSpan     reconcileInterval;
        private static TimeSpan     modifiedInterval;
        private static TimeSpan     errorDelay;

        // Metrics counters

        private static readonly Counter reconciledReceivedCounter      = Metrics.CreateCounter("container_registry_reconciled_received", "Received ContainerRegistry reconcile events.");
        private static readonly Counter deletedReceivedCounter         = Metrics.CreateCounter("container_registry_deleted_received", "Received ContainerRegistry deleted events.");
        private static readonly Counter statusModifiedReceivedCounter  = Metrics.CreateCounter("container_registry_statusmodified_received", "Received ContainerRegistry status-modified events.");

        private static readonly Counter reconciledProcessedCounter     = Metrics.CreateCounter("container_registry_reconciled_changes", "Processed ContainerRegistry reconcile events due to change.");
        private static readonly Counter deletedProcessedCounter        = Metrics.CreateCounter("container_registry_deleted_changes", "Processed ContainerRegistry deleted events due to change.");
        private static readonly Counter statusModifiedProcessedCounter = Metrics.CreateCounter("container_registry_statusmodified_changes", "Processed ContainerRegistry status-modified events due to change.");

        private static readonly Counter reconciledErrorCounter         = Metrics.CreateCounter("container_registry_reconciled_error", "Failed ContainerRegistry reconcile event processing.");
        private static readonly Counter deletedErrorCounter            = Metrics.CreateCounter("container_registry_deleted_error", "Failed ContainerRegistry deleted event processing.");
        private static readonly Counter statusModifiedErrorCounter     = Metrics.CreateCounter("container_registry_statusmodified_error", "Failed ContainerRegistry status-modified events processing.");

        private static readonly Counter configUpdateCounter            = Metrics.CreateCounter("container_registry_node_updated", "Number of node config updates.");
        private static readonly Counter loginErrorCounter              = Metrics.CreateCounter("container_registry_login_error", "Number of failed container registry logins.");

        //---------------------------------------------------------------------
        // Instance members

        /// <summary>
        /// Coinstructor.
        /// </summary>
        public ContainerRegistryController()
        {
            // Load the configuration settings the first time a controller instance is created.

            if (!configured)
            {
                reconcileInterval = Program.Service.Environment.Get("CONTAINERREGISTRY_RECONCILE_REQUEUE_INTERVAL", TimeSpan.FromMinutes(5));
                modifiedInterval  = Program.Service.Environment.Get("CONTAINERREGISTRY_MODIFIED_REQUEUE_INTERVAL", TimeSpan.FromMinutes(5));
                errorDelay        = Program.Service.Environment.Get("CONTAINERREGISTRY_ERROR_DELAY", TimeSpan.FromMinutes(1));
                configured        = true;
            }
        }

        /// <summary>
        /// Called for each existing custom resource when the controller starts so that the controller
        /// can maintain the status of all resources and then afterwards, this will be called whenever
        /// a resource is added or has a non-status update.
        /// </summary>
        /// <param name="resource">The new entity.</param>
        /// <returns>The controller result.</returns>
        public async Task<ResourceControllerResult> ReconcileAsync(V1ContainerRegistry resource)
        {
            reconciledReceivedCounter.Inc();

            await resourceManager.ReconciledAsync(resource,
                async (name, resources) =>
                {
                    log.LogInfo($"RECONCILED: {name}");
                    reconciledProcessedCounter.Inc();

                    UpdateContainerRegistries(resources);

                    return await Task.FromResult<ResourceControllerResult>(null);
                },
                errorCounter: reconciledErrorCounter);

            return ResourceControllerResult.RequeueEvent(modifiedInterval);
        }

        /// <summary>
        /// Called when a custom resource is removed from the API Server.
        /// </summary>
        /// <param name="resource">The deleted entity.</param>
        /// <returns>The tracking <see cref="Task"/>.</returns>
        public async Task DeletedAsync(V1ContainerRegistry resource)
        {
            deletedReceivedCounter.Inc();

            await resourceManager.DeletedAsync(resource,
                async (name, resources) =>
                {
                    log.LogInfo($"DELETED: {name}");
                    deletedProcessedCounter.Inc();

                    UpdateContainerRegistries(resources);

                    return await Task.FromResult<ResourceControllerResult>(null);
                },
                errorCounter: deletedErrorCounter);
        }

        /// <summary>
        /// Called when a custom resource's status has been modified.
        /// </summary>
        /// <param name="resource">The updated entity.</param>
        /// <returns>The controller result.</returns>
        public async Task<ResourceControllerResult> StatusModifiedAsync(V1ContainerRegistry resource)
        {
            statusModifiedReceivedCounter.Inc();

            await resourceManager.DeletedAsync(resource,
                async (name, resources) =>
                {
                    log.LogInfo($"DELETED: {name}");
                    statusModifiedProcessedCounter.Inc();

                    UpdateContainerRegistries(resources);

                    return await Task.FromResult<ResourceControllerResult>(null);
                },
                errorCounter: statusModifiedErrorCounter);

            return ResourceControllerResult.RequeueEvent(modifiedInterval);
        }

        /// <summary>
        /// Rebuilds the host node's <b>/etc/containers/registries.conf.d/00-neon-cluster.conf</b> file,
        /// using the container registries passed and then signals CRI-O to reload any changes.
        /// </summary>
        /// <param name="registries">The current registry configurations.</param>
        private void UpdateContainerRegistries(IReadOnlyDictionary<string, V1ContainerRegistry> registries)
        {
            // NOTE: Here's the documentation for the config file we're generating:
            //
            //      https://github.com/containers/image/blob/main/docs/containers-registries.conf.5.md
            //

            var sbRegistryConfig   = new StringBuilder();
            var sbSearchRegistries = new StringBuilder();

            // Specify any unqualified search registries.

            foreach (var registry in registries.Values
                .Where(registry => registry.Spec.SearchOrder >= 0)
                .OrderBy(registry => registry.Spec.SearchOrder))
            {
                sbSearchRegistries.AppendWithSeparator($"\"{registry.Spec.Prefix}\"", ", ");
            }

            sbRegistryConfig.Append(
$@"unqualified-search-registries = [{sbSearchRegistries}]
");

            // Specify the built-in cluster registry.

            sbRegistryConfig.Append(
$@"
[[registry]]
prefix   = ""{KubeConst.LocalClusterRegistry}""
insecure = true
blocked  = false
location = ""{KubeConst.LocalClusterRegistry}""
");

            // Specify any custom upstream registries.

            foreach (var registry in registries.Values)
            {
                sbRegistryConfig.Append(
$@"
[[registry]]
prefix   = ""{registry.Spec.Prefix}""
insecure = {NeonHelper.ToBoolString(registry.Spec.Insecure)}
blocked  = {NeonHelper.ToBoolString(registry.Spec.Blocked)}
");

                if (!string.IsNullOrEmpty(registry.Spec.Location))
                {
                    sbRegistryConfig.AppendLine($"location = \"{registry.Spec.Location}\"");
                }
            }

            // Convert the generated config to Linux line endings and then compare the new
            // config against what's already configured on the host node.  We'll rewrite the
            // host file and then signal CRI-O to reload its config when the files differ.

            var newConfig = NeonHelper.ToLinuxLineEndings(sbRegistryConfig.ToString());

            if (File.ReadAllText(configMountPath) != newConfig)
            {
                configUpdateCounter.Inc();

                File.WriteAllText(configMountPath, newConfig);
                Program.HostExecuteCapture("/usr/bin/pkill", new object[] { "-HUP", "crio" }).EnsureSuccess();
            }

            // We also need to log into each of the registries that require credentials
            // via [podman] on the node.  We'll log individual login failures but continue
            // to try logging into any remaining registries.

            foreach (var registry in registries.Values)
            {
                try
                {
                    if (string.IsNullOrEmpty(registry.Spec.Username))
                    {
                        // The registry doesn't have a username so we'll logout to clear any old credentials.
                        // We're going to ignore any errors here in case we're not currently logged into
                        // the registry.

                        Program.HostExecuteCapture("podman", "logout", registry.Spec.Location);
                    }
                    else
                    {
                        // The registry has credentials so login using them.

                        Program.HostExecuteCapture("podman", "login", registry.Spec.Location, "--username", registry.Spec.Username, "--password", registry.Spec.Password).EnsureSuccess();
                    }
                }
                catch (Exception e)
                {
                    loginErrorCounter.Inc();
                    log.LogError(e);
                }
            }
        }
    }
}
