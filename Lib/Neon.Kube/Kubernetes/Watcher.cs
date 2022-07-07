﻿//-----------------------------------------------------------------------------
// FILE:	    WatchStream.cs
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
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

using Neon.Common;
using Neon.Diagnostics;
using Neon.Tasks;

using k8s;
using k8s.Models;
using k8s.Autorest;

namespace Neon.Kube
{
    /// <summary>
    /// A kubernetes watch event.
    /// </summary>
    /// <typeparam name="T">Specifies the Kubernetes entity type being watched.</typeparam>
    public class WatchEvent<T>
    {
        /// <summary>
        /// The <see cref="WatchEventType"/>
        /// </summary>
        public WatchEventType Type { get; internal set; }

        /// <summary>
        /// The watch event value.
        /// </summary>
        public T Value { get; internal set; }
    }

    /// <summary>
    /// A generic Kubernetes watcher.
    /// </summary>
    /// <typeparam name="T">Specifies the Kubernetes entity type being watched.</typeparam>
    public sealed class Watcher<T> : IDisposable 
        where T : IKubernetesObject<V1ObjectMeta>, new()
    {
        private string                  resourceVersion;
        private AsyncAutoResetEvent     eventReady;
        private Queue<WatchEvent<T>>    eventQueue;
        private IKubernetes             k8s;
        private INeonLogger             logger;

        /// <summary>
        /// Constructor.
        /// </summary>
        /// <param name="k8s">The Kubernetes clien.</param>
        /// <param name="logger">Optionally specifies the logger to use.</param>
        public Watcher(IKubernetes k8s, INeonLogger logger = null)
        {
            this.k8s    = k8s;
            this.logger = logger;
            eventReady  = new AsyncAutoResetEvent();
            eventQueue  = new Queue<WatchEvent<T>>();
        }

        /// <inheritdoc/>
        public void Dispose()
        {
            if (eventQueue != null)
            {
                eventQueue = null;
            }

            if (eventReady != null)
            {
                eventReady.Dispose();
                eventReady = null;
            }
        }

        /// <summary>
        /// A generic Watcher to watch Kubernetes resources, and respond with a custom (async) callback method.
        /// </summary>
        /// <param name="actionAsync">The async action called as watch events are received..</param>
        /// <param name="namespaceParameter">That target Kubernetes namespace.</param>
        /// <param name="fieldSelector">The optional field selector</param>
        /// <param name="labelSelector">The optional label selector</param>
        /// <param name="resourceVersion">The start resource version.</param>
        /// <param name="resourceVersionMatch">The optional resourceVersionMatch setting.</param>
        /// <param name="timeoutSeconds">Optional timeout override.</param>
        /// <param name="cancellationToken">Optionally specifies a cancellation token.</param>
        /// <returns>The tracking <see cref="Task"/>.</returns>
        public async Task WatchAsync(
            Func<WatchEvent<T>, Task>   actionAsync, 
            string                      namespaceParameter   = null,
            string                      fieldSelector        = null,
            string                      labelSelector        = null,
            string                      resourceVersion      = null,
            string                      resourceVersionMatch = null,
            int?                        timeoutSeconds       = null,
            CancellationToken           cancellationToken    = default)
        {
            await SyncContext.Clear;

            // Validate the resource version we're being given.

            if (!string.IsNullOrEmpty(resourceVersion))
            {
                await ValidateResourceVersionAsync(
                    fieldSelector:        fieldSelector,
                    labelSelector:        labelSelector,
                    resourceVersion:      resourceVersion,
                    resourceVersionMatch: resourceVersionMatch,
                    timeoutSeconds:       timeoutSeconds);
            }

            // Start the loop that handles the async action callbacks.

            _ = EventHandlerAsync(actionAsync);

            // This is where you'll actually listen for watch events from Kubernetes.
            // When you receive an event, do this:

            while (true)
            {
                this.resourceVersion = resourceVersion ?? "0";

                while (!string.IsNullOrEmpty(this.resourceVersion))
                {
                    try
                    {
                        Task<HttpOperationResponse<object>> listResponse;

                        if (string.IsNullOrEmpty(namespaceParameter))
                        {
                            listResponse = k8s.ListClusterCustomObjectWithHttpMessagesAsync<T>(
                                allowWatchBookmarks:  true,
                                fieldSelector:        fieldSelector,
                                labelSelector:        labelSelector,
                                resourceVersion:      this.resourceVersion,
                                resourceVersionMatch: resourceVersionMatch,
                                timeoutSeconds:       timeoutSeconds,
                                watch:                true,
                                cancellationToken:    cancellationToken);
                        }
                        else
                        {
                            listResponse = k8s.ListNamespacedCustomObjectWithHttpMessagesAsync<T>(
                            namespaceParameter,
                            allowWatchBookmarks:  true,
                            fieldSelector:        fieldSelector,
                            labelSelector:        labelSelector,
                            resourceVersion:      this.resourceVersion,
                            resourceVersionMatch: resourceVersionMatch,
                            timeoutSeconds:       timeoutSeconds,
                            cancellationToken:    cancellationToken,
                            watch:                true);
                        }

                        using (listResponse.Watch(
                            (WatchEventType type, T item) =>
                            {
                                lock (eventQueue)
                                {
                                    eventQueue.Enqueue(new WatchEvent<T>() { Type = type, Value = item });
                                    eventReady.Set();
                                }
                            }))
                        {
                            while (true)
                            {
                                await Task.Delay(TimeSpan.FromHours(1));
                            }
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        // This is the signal to quit.

                        return;
                    }
                    catch (KubernetesException kubernetesException)
                    {
                        logger?.LogError(kubernetesException);

                        // Deal with this non-recoverable condition "too old resource version"

                        if (string.Equals(kubernetesException.Status.Reason, "Expired", StringComparison.Ordinal))
                        {
                            // force control back to outer loop
                            this.resourceVersion = null;
                        }
                    }
                }
            }
        }

        /// <summary>
        /// Handles received events.
        /// </summary>
        /// <param name="action"></param>
        /// <returns></returns>
        /// <exception cref="KubernetesException"></exception>
        private async Task EventHandlerAsync(Func<WatchEvent<T>, Task> action)
        {
            await SyncContext.Clear;

            try
            {
                while (true)
                {
                    WatchEvent<T> @event;

                    await eventReady.WaitAsync();

                    lock (eventQueue)
                    {
                        @event = eventQueue.Dequeue();
                    }

                    switch (@event.Type)
                    {
                        case WatchEventType.Bookmark:

                            resourceVersion = @event.Value.ResourceVersion();
                            break;

                        case WatchEventType.Error:

                            break;

                        case WatchEventType.Added:
                        case WatchEventType.Modified:
                        case WatchEventType.Deleted:

                            resourceVersion = @event.Value.ResourceVersion();

                            await action(@event);
                            break;

                        default:

                            throw new KubernetesException();
                    }
                }
            }
            catch (TaskCanceledException)
            {
                // This normal: we'll see this when the watcher is disposed.

                logger?.LogInfo("Disposing");
            }
        }

        /// <summary>
        /// Used to validate a given resource version by sending a simple request to the Kubernetes API server.
        /// </summary>
        /// <param name="resourceVersion">The resource version.</param>
        /// <param name="namespaceParameter">The object namespace, or null for cluster objects</param>
        /// <param name="fieldSelector">Optional field selector</param>
        /// <param name="labelSelector">Optional label selector.</param>
        /// <param name="resourceVersionMatch">Optional resourceVersionMatch parameter.</param>
        /// <param name="timeoutSeconds">Optional timeout.</param>
        /// <returns>The tracking <see cref="Task"/>.</returns>
        private async Task ValidateResourceVersionAsync(
            string  resourceVersion,
            string  namespaceParameter   = null,
            string  fieldSelector        = null,
            string  labelSelector        = null,
            string  resourceVersionMatch = null,
            int?    timeoutSeconds       = null)
        {
            await SyncContext.Clear;

            try
            {
                if (string.IsNullOrEmpty(namespaceParameter))
                {
                    await k8s.ListClusterCustomObjectWithHttpMessagesAsync<T>(
                    fieldSelector:        fieldSelector,
                    labelSelector:        labelSelector,
                    limit:                1,
                    resourceVersion:      resourceVersion,
                    resourceVersionMatch: resourceVersionMatch,
                    timeoutSeconds:       timeoutSeconds,
                    watch:                false);
                }
                else
                {
                    await k8s.ListNamespacedCustomObjectWithHttpMessagesAsync<T>(
                    namespaceParameter,
                    fieldSelector:        fieldSelector,
                    labelSelector:        labelSelector,
                    limit:                1,
                    resourceVersion:      resourceVersion,
                    resourceVersionMatch: resourceVersionMatch,
                    timeoutSeconds:       timeoutSeconds,
                    watch:                false);
                }
            }
            catch (KubernetesException kubernetesException)
            {
                logger?.LogError(kubernetesException);

                // Deal with this non-recoverable condition "too old resource version"

                if (string.Equals(kubernetesException.Status.Reason, "Expired", StringComparison.Ordinal))
                {
                    throw;
                }
            }
        }
    }
}
