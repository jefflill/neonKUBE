//-----------------------------------------------------------------------------
// FILE:	    KubernetesExtensions.cs
// CONTRIBUTOR: Marcus Bowyer
// COPYRIGHT:	Copyright © 2005-2023 by NEONFORGE LLC.  All rights reserved.
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
using System.Reflection;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using System.Threading;

using Microsoft.Extensions.Logging;

using Neon.Common;
using Neon.Diagnostics;
using Neon.Kube;
using Neon.Retry;
using Neon.Tasks;

using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

using k8s;
using k8s.Autorest;
using k8s.KubeConfigModels;
using k8s.Models;

namespace Neon.Kube
{
    /// <summary>
    /// Kubernetes related extension methods.
    /// </summary>
    public static partial class KubernetesExtensions
    {
        //---------------------------------------------------------------------
        // Shared fields

        private static readonly JsonSerializerOptions serializeOptions;

        /// <summary>
        /// Static constructor.
        /// </summary>
        static KubernetesExtensions()
        {
            serializeOptions = new JsonSerializerOptions()
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            };

            serializeOptions.Converters.Add(new JsonStringEnumMemberConverter());
        }

        //---------------------------------------------------------------------
        // NamedExtension
        //
        // Note that we're persisting our extension values as YAML strings.  This works,
        // but will be pretty slow since this requires the YAML to be serialized on every
        // set and deserialized on every get.  This isn't called in inner loops right now
        // so we're going with this for now.
        //
        // Setting assigning the value as a dynamic didn't work as I expected.

        /// <summary>
        /// Sets the named extension value by adding it if it doesn't already exist or changing
        /// the existing value.
        /// </summary>
        /// <typeparam name="T">Specifies the property value type.</typeparam>
        /// <param name="extensions">Holds the extensions.</param>
        /// <param name="name">Specifies the extension name.</param>
        /// <param name="value">Specifies the value being set.</param>
        public static void Set<T>(this List<NamedExtension> extensions, string name, T value)
        {
            Covenant.Requires<ArgumentNullException>(extensions != null, nameof(extensions));
            Covenant.Requires<ArgumentNullException>(!string.IsNullOrEmpty(name), nameof(name));

            foreach (var item in extensions)
            {
                if (item.Name == name)
                {
                    item.Extension = NeonHelper.YamlSerialize(value);
                    return;
                }
            }

            extensions.Add(new NamedExtension() { Name = name, Extension = NeonHelper.YamlSerialize(value) });
        }

        /// <summary>
        /// Searches <paramref name="extensions"/> for an extension with the name passed and
        /// returns its value when found, otherwise returns <paramref name="default"/>.
        /// </summary>
        /// <typeparam name="T">Specifies the property value type.</typeparam>
        /// <param name="extensions">Holds the extensions.</param>
        /// <param name="name">Specifies the extension name.</param>
        /// <param name="default">The value to be returned when the extension doesn't exist.</param>
        /// <returns>The extension value when found, otherwise <paramref name="default"/>.</returns>
        /// <exception cref="InvalidCastException">Thrown when the extension value cannot be cast to <typeparamref name="T"/>.</exception>
        public static T Get<T>(this List<NamedExtension> extensions, string name, T @default)
        {
            Covenant.Requires<ArgumentNullException>(!string.IsNullOrEmpty(name), nameof(name));

            if (extensions == null)
            {
                return @default;
            }

            foreach (var item in extensions)
            {
                if (item.Name == name)
                {
                    if (item.Extension == null)
                    {
                        return @default;
                    }

                    return NeonHelper.YamlDeserialize<T>((string)item.Extension);
                }
            }

            return @default;
        }

        /// <summary>
        /// Removes a named extension if present.
        /// </summary>
        /// <param name="extensions">Holds the extensions.</param>
        /// <param name="name">Specifies the name of the extension being removed.</param>
        public static void Remove(this List<NamedExtension> extensions, string name)
        {
            Covenant.Requires<ArgumentNullException>(!string.IsNullOrEmpty(name), nameof(name));

            for (int i = 0; i < extensions.Count; i++)
            {
                if (extensions[i].Name == name)
                {
                    extensions.RemoveAt(i);
                    return;
                }
            }
        }

        //---------------------------------------------------------------------
        // V1ObjectMeta extensions

        /// <summary>
        /// Sets a label within the metadata, constructing the label dictionary when necessary.
        /// </summary>
        /// <param name="metadata">The metadata instance.</param>
        /// <param name="name">The label name.</param>
        /// <param name="value">Optionally specifies a label value.  This defaults to an empty string.</param>
        public static void SetLabel(this V1ObjectMeta metadata, string name, string value = null)
        {
            Covenant.Requires<ArgumentNullException>(!string.IsNullOrEmpty(name), nameof(name));

            if (metadata.Labels == null)
            {
                metadata.Labels = new Dictionary<string, string>();
            }

            metadata.Labels[name] = value ?? string.Empty;
        }

        /// <summary>
        /// Fetches the value of a label from the metadata.
        /// </summary>
        /// <param name="metadata">The metadata instance.</param>
        /// <param name="name">The label name.</param>
        /// <returns>The label value or <c>null</c> when the label doesn't exist.</returns>
        public static string GetLabel(this V1ObjectMeta metadata, string name)
        {
            Covenant.Requires<ArgumentNullException>(!string.IsNullOrEmpty(name), nameof(name));

            if (metadata.Labels == null)
            {
                return null;
            }

            if (metadata.Labels.TryGetValue(name, out var value))
            {
                return value;
            }
            else
            {
                return null;
            }
        }

        //---------------------------------------------------------------------
        // Deployment extensions

        /// <summary>
        /// Restarts a <see cref="V1Deployment"/>.
        /// </summary>
        /// <param name="deployment">The target deployment.</param>
        /// <param name="k8s">The <see cref="IKubernetes"/> client to be used for the operation.</param>
        /// <returns>The tracking <see cref="Task"/>.</returns>
        public static async Task RestartAsync(this V1Deployment deployment, IKubernetes k8s)
        {
            await SyncContext.Clear;
            Covenant.Requires<ArgumentNullException>(k8s != null, nameof(k8s));

            // $todo(jefflill):
            //
            // Fish out the k8s client from the deployment so we don't have to pass it in as a parameter.

            var generation = deployment.Status.ObservedGeneration;

            var patchStr = $@"
{{
    ""spec"": {{
        ""template"": {{
            ""metadata"": {{
                ""annotations"": {{
                    ""kubectl.kubernetes.io/restartedAt"": ""{DateTime.UtcNow.ToString("s")}""
                }}
            }}
        }}
    }}
}}";

            await k8s.AppsV1.PatchNamespacedDeploymentAsync(new V1Patch(patchStr, V1Patch.PatchType.MergePatch), deployment.Name(), deployment.Namespace());

            await NeonHelper.WaitForAsync(
                async () =>
                {
                    try
                    {
                        var newDeployment = await k8s.AppsV1.ReadNamespacedDeploymentAsync(deployment.Name(), deployment.Namespace());

                        return newDeployment.Status.ObservedGeneration > generation;
                    }
                    catch
                    {
                        return false;
                    }
                },
                timeout:      TimeSpan.FromSeconds(300),
                pollInterval: TimeSpan.FromMilliseconds(500));

            await NeonHelper.WaitForAsync(
                async () =>
                {
                    try
                    {
                        deployment = await k8s.AppsV1.ReadNamespacedDeploymentAsync(deployment.Name(), deployment.Namespace());

                        return (deployment.Status.Replicas == deployment.Status.AvailableReplicas) && deployment.Status.UnavailableReplicas == null;
                    }
                    catch
                    {
                        return false;
                    }
                },
                timeout:      TimeSpan.FromSeconds(300),
                pollInterval: TimeSpan.FromMilliseconds(500));
        }

        /// <summary>
        /// Restarts a <see cref="V1StatefulSet"/>.
        /// </summary>
        /// <param name="statefulset">The deployment being restarted.</param>
        /// <param name="k8s">The <see cref="IKubernetes"/> client to be used for the operation.</param>
        /// <returns>The tracking <see cref="Task"/>.</returns>
        public static async Task RestartAsync(this V1StatefulSet statefulset, IKubernetes k8s)
        {
            await SyncContext.Clear;
            Covenant.Requires<ArgumentNullException>(k8s != null, nameof(k8s));

            // $todo(jefflill):
            //
            // Fish out the k8s client from the statefulset so we don't have to pass it in as a parameter.

            var generation = statefulset.Status.ObservedGeneration;

            var patchStr = $@"
{{
    ""spec"": {{
        ""template"": {{
            ""metadata"": {{
                ""annotations"": {{
                    ""kubectl.kubernetes.io/restartedAt"": ""{DateTime.UtcNow.ToString("s")}""
                }}
            }}
        }}
    }}
}}";

            await k8s.AppsV1.PatchNamespacedStatefulSetAsync(new V1Patch(patchStr, V1Patch.PatchType.MergePatch), statefulset.Name(), statefulset.Namespace());

            await NeonHelper.WaitForAsync(
                async () =>
                {
                    try
                    {
                        var newDeployment = await k8s.AppsV1.ReadNamespacedStatefulSetAsync(statefulset.Name(), statefulset.Namespace());

                        return newDeployment.Status.ObservedGeneration > generation;
                    }
                    catch
                    {
                        return false;
                    }
                },
                timeout:      TimeSpan.FromSeconds(300),
                pollInterval: TimeSpan.FromMilliseconds(500));

            await NeonHelper.WaitForAsync(
                async () =>
                {
                    try
                    {
                        statefulset = await k8s.AppsV1.ReadNamespacedStatefulSetAsync(statefulset.Name(), statefulset.Namespace());

                        return (statefulset.Status.Replicas == statefulset.Status.ReadyReplicas) && statefulset.Status.UpdatedReplicas == null;
                    }
                    catch
                    {
                        return false;
                    }
                },
                timeout: TimeSpan.FromSeconds(300),
                pollInterval: TimeSpan.FromMilliseconds(500));
        }

        /// <summary>
        /// Restarts a <see cref="V1DaemonSet"/>.
        /// </summary>
        /// <param name="daemonset">The daemonset being restarted.</param>
        /// <param name="k8s">The <see cref="IKubernetes"/> client to be used for the operation.</param>
        /// <returns>The tracking <see cref="Task"/>.</returns>
        public static async Task RestartAsync(this V1DaemonSet daemonset, IKubernetes k8s)
        {
            await SyncContext.Clear;
            Covenant.Requires<ArgumentNullException>(k8s != null, nameof(k8s));

            // $todo(jefflill):
            //
            // Fish out the k8s client from the statefulset so we don't have to pass it in as a parameter.

            var generation = daemonset.Status.ObservedGeneration;

            var patchStr = $@"
{{
    ""spec"": {{
        ""template"": {{
            ""metadata"": {{
                ""annotations"": {{
                    ""kubectl.kubernetes.io/restartedAt"": ""{DateTime.UtcNow.ToString("s")}""
                }}
            }}
        }}
    }}
}}";

            await k8s.AppsV1.PatchNamespacedDaemonSetAsync(new V1Patch(patchStr, V1Patch.PatchType.MergePatch), daemonset.Name(), daemonset.Namespace());

            await NeonHelper.WaitForAsync(
                async () =>
                {
                    try
                    {
                        var newDeployment = await k8s.AppsV1.ReadNamespacedDaemonSetAsync(daemonset.Name(), daemonset.Namespace());

                        return newDeployment.Status.ObservedGeneration > generation;
                    }
                    catch
                    {
                        return false;
                    }
                },
                timeout:      TimeSpan.FromSeconds(300),
                pollInterval: TimeSpan.FromMilliseconds(500));

            await NeonHelper.WaitForAsync(
                async () =>
                {
                    try
                    {
                        daemonset = await k8s.AppsV1.ReadNamespacedDaemonSetAsync(daemonset.Name(), daemonset.Namespace());

                        return (daemonset.Status.CurrentNumberScheduled == daemonset.Status.NumberReady) && daemonset.Status.UpdatedNumberScheduled == null;
                    }
                    catch
                    {
                        return false;
                    }
                },
                timeout:      TimeSpan.FromSeconds(300),
                pollInterval: TimeSpan.FromMilliseconds(500));
        }

        //---------------------------------------------------------------------
        // IKubernetesObject extensions

        // Used to cache [KubernetesEntityAttribute] values for custom resource types
        // for better performance (avoiding unnecessary reflection).

        private class CustomResourceMetadata
        {
            public CustomResourceMetadata(KubernetesEntityAttribute attr)
            {
                this.Group           = attr.Group;
                this.ApiVersion      = attr.ApiVersion;
                this.Kind            = attr.Kind;
                this.GroupApiVersion = $"{attr.Group}/{attr.ApiVersion}";
            }

            public string Group             { get; private set; }
            public string ApiVersion        { get; private set; }
            public string Kind              { get; private set; }
            public string GroupApiVersion   { get; private set; }
        }

        private static Dictionary<Type, CustomResourceMetadata> typeToKubernetesEntity = new ();

        /// <summary>
        /// Initializes a custom Kubernetes object's metadata <b>Group</b>, <b>ApiVersion</b>, and
        /// <b>Kind</b> properties from the <see cref="KubernetesEntityAttribute"/> attached to the
        /// object's type.
        /// </summary>
        /// <param name="obj">The object.</param>
        /// <exception cref="InvalidDataException">Thrown when the object's type does not have a <see cref="KubernetesEntityAttribute"/>.</exception>
        /// <remarks>
        /// <para>
        /// This should be called in all custom object constructors to ensure that the object's
        /// metadata is configured and matches what was specified in the attribute.  Here's
        /// what this will look like:
        /// </para>
        /// <code language="C#">
        /// [KubernetesEntity(Group = "mygroup.io", ApiVersion = "v1", Kind = "my-resource", PluralName = "my-resources")]
        /// [KubernetesEntityShortNames]
        /// [EntityScope(EntityScope.Cluster)]
        /// [Description("My custom resource.")]
        /// public class V1MyCustomResource : CustomKubernetesEntity&lt;V1ContainerRegistry.V1ContainerRegistryEntitySpec&gt;
        /// {
        ///     public V1ContainerRegistry()
        ///     {
        ///         ((IKubernetesObject)this).InitializeMetadata();
        ///     }
        ///
        ///     ...
        /// </code>
        /// </remarks>
        public static void SetMetadata(this IKubernetesObject obj)
        {
            var objType = obj.GetType();

            CustomResourceMetadata customMetadata;

            lock (typeToKubernetesEntity)
            {
                if (!typeToKubernetesEntity.TryGetValue(objType, out customMetadata))
                {
                    var entityAttr = objType.GetCustomAttribute<KubernetesEntityAttribute>();

                    if (entityAttr == null)
                    {
                        throw new InvalidDataException($"Custom Kubernetes resource type [{objType.FullName}] does not have a [{nameof(KubernetesEntityAttribute)}].");
                    }

                    customMetadata = new CustomResourceMetadata(entityAttr);

                    typeToKubernetesEntity.Add(objType, customMetadata);
                }
            }

            obj.ApiVersion = customMetadata.GroupApiVersion;
            obj.Kind       = customMetadata.Kind;
        }

        //---------------------------------------------------------------------
        // IKubernetes client extensions.

        // $note(jefflill):
        //
        // These methods are not currently added automatically to the generated [KubernetesWithRetry]
        // class.  We need to add these manually in the [KubernetesWithRetry.manual.cs] file.

        /// <summary>
        /// Adds a new Kubernetes secret or updates an existing secret.
        /// </summary>
        /// <param name="k8s">The <see cref="Kubernetes"/> client.</param>
        /// <param name="secret">The secret.</param>
        /// <param name="namespaceParameter">Optionally overrides the default namespace.</param>
        /// <returns>The updated secret.</returns>
        public static async Task<V1Secret> UpsertSecretAsync(this ICoreV1Operations k8s, V1Secret secret, string namespaceParameter = null)
        {
            await SyncContext.Clear;
            Covenant.Requires<ArgumentNullException>(secret != null, nameof(secret));

            if ((await k8s.ListNamespacedSecretAsync(namespaceParameter)).Items.Any(s => s.Metadata.Name == secret.Name()))
            {
                return await k8s.ReplaceNamespacedSecretAsync(secret, secret.Name(), namespaceParameter);
            }
            else
            {
                return await k8s.CreateNamespacedSecretAsync(secret, namespaceParameter);
            }
        }

        /// <summary>
        /// Waits for a service deployment to start successfully.
        /// </summary>
        /// <param name="k8sAppsV1">The <see cref="Kubernetes"/> client's <see cref="IAppsV1Operations"/>.</param>
        /// <param name="namespaceParameter">The namespace.</param>
        /// <param name="name">Optionally specifies the deployment name.</param>
        /// <param name="labelSelector">Optionally specifies a label selector.</param>
        /// <param name="fieldSelector">Optionally specifies a field selector.</param>
        /// <param name="pollInterval">Optionally specifies the polling interval.  This defaults to 1 second.</param>
        /// <param name="timeout">Optopnally specifies the operation timeout.  This defaults to 30 seconds.</param>
        /// <param name="cancellationToken">Optionally specifies the cancellation token.</param>
        /// <returns>The tracking <see cref="Task"/>.</returns>x
        /// <remarks>
        /// One of <paramref name="name"/>, <paramref name="labelSelector"/>, or <paramref name="fieldSelector"/>
        /// must be specified.
        /// </remarks>
        public static async Task WaitForDeploymentAsync(
            this IAppsV1Operations  k8sAppsV1, 
            string                  namespaceParameter, 
            string                  name              = null, 
            string                  labelSelector     = null,
            string                  fieldSelector     = null,
            TimeSpan                pollInterval      = default,
            TimeSpan                timeout           = default,
            CancellationToken       cancellationToken = default)
        {
            await SyncContext.Clear;
            Covenant.Requires<ArgumentException>(!string.IsNullOrEmpty(name) || labelSelector != null || fieldSelector != null, "One of [name], [labelSelector] or [fieldSelector] must be specified.");
            Covenant.Requires<ArgumentNullException>(!string.IsNullOrEmpty(namespaceParameter), nameof(namespaceParameter));

            if (pollInterval <= TimeSpan.Zero)
            {
                pollInterval = TimeSpan.FromSeconds(1);
            }

            if (timeout <= TimeSpan.Zero)
            {
                timeout = TimeSpan.FromSeconds(30);
            }

            if (!string.IsNullOrEmpty(name))
            {
                if (!string.IsNullOrEmpty(fieldSelector))
                {
                    fieldSelector += $",metadata.name={name}";
                }
                else
                {
                    fieldSelector = $"metadata.name={name}";
                }
            }

            await NeonHelper.WaitForAsync(
                async () =>
                {
                    try
                    {
                        var deployments = await k8sAppsV1.ListNamespacedDeploymentAsync(namespaceParameter, fieldSelector: fieldSelector, labelSelector: labelSelector);

                        if (deployments == null || deployments.Items.Count == 0)
                        {
                            return false;
                        }

                        return deployments.Items.All(deployment => deployment.Status.AvailableReplicas == deployment.Spec.Replicas);
                    }
                    catch
                    {
                        return false;
                    }
                            
                },
                timeout:           timeout,
                pollInterval:      pollInterval,
                cancellationToken: cancellationToken);
        }

        /// <summary>
        /// Waits for a stateful set to start successfully.
        /// </summary>
        /// <param name="k8sAppsV1">The <see cref="Kubernetes"/> client's <see cref="IAppsV1Operations"/>.</param>
        /// <param name="namespaceParameter">The namespace.</param>
        /// <param name="name">Optionally specifies the stateful set name..</param>
        /// <param name="labelSelector">Optionally specifies a label selector.</param>
        /// <param name="fieldSelector">Optionally specifies a field selector.</param>
        /// <param name="pollInterval">Optionally specifies the polling interval.  This defaults to 1 second.</param>
        /// <param name="timeout">Optopnally specifies the operation timeout.  This defaults to 30 seconds.</param>
        /// <param name="cancellationToken">Optionally specifies the cancellation token.</param>
        /// <returns>The tracking <see cref="Task"/>.</returns>
        /// <remarks>
        /// One of <paramref name="name"/>, <paramref name="labelSelector"/>, or <paramref name="fieldSelector"/>
        /// must be specified.
        /// </remarks>
        public static async Task WaitForStatefulSetAsync(
            this IAppsV1Operations  k8sAppsV1,
            string                  namespaceParameter,
            string                  name              = null,
            string                  labelSelector     = null,
            string                  fieldSelector     = null,
            TimeSpan                pollInterval      = default,
            TimeSpan                timeout           = default,
            CancellationToken       cancellationToken = default)
        {
            await SyncContext.Clear;
            Covenant.Requires<ArgumentException>(!string.IsNullOrEmpty(name) || labelSelector != null || fieldSelector != null, "One of [name], [labelSelector] or [fieldSelector] must be passed.");
            Covenant.Requires<ArgumentNullException>(!string.IsNullOrEmpty(namespaceParameter), nameof(namespaceParameter));

            if (pollInterval <= TimeSpan.Zero)
            {
                pollInterval = TimeSpan.FromSeconds(1);
            }

            if (timeout <= TimeSpan.Zero)
            {
                timeout = TimeSpan.FromSeconds(30);
            }

            if (!string.IsNullOrEmpty(name))
            {
                if (!string.IsNullOrEmpty(fieldSelector))
                {
                    fieldSelector += $",metadata.name={name}";
                }
                else
                {
                    fieldSelector = $"metadata.name={name}";
                }
            }

            await NeonHelper.WaitForAsync(
                async () =>
                {
                    try
                    {
                        var statefulsets = await k8sAppsV1.ListNamespacedStatefulSetAsync(namespaceParameter, fieldSelector: fieldSelector, labelSelector: labelSelector);

                        if (statefulsets == null || statefulsets.Items.Count == 0)
                        {
                            return false;
                        }

                        return statefulsets.Items.All(@set => @set.Status.ReadyReplicas == @set.Spec.Replicas);
                    }
                    catch
                    {
                        return false;
                    }
                },
                timeout:           timeout,
                pollInterval:      pollInterval,
                cancellationToken: cancellationToken);
        }

        /// <summary>
        /// Waits for a daemon set to start successfully.
        /// </summary>
        /// <param name="k8sAppsV1">The <see cref="Kubernetes"/> client's <see cref="IAppsV1Operations"/>.</param>
        /// <param name="namespaceParameter">The namespace.</param>
        /// <param name="name">Optionally specifies the daemonset name.</param>
        /// <param name="labelSelector">Optionally specifies a label selector.</param>
        /// <param name="fieldSelector">Optionally specifies a field selector.</param>
        /// <param name="pollInterval">Optionally specifies the polling interval.  This defaults to 1 second.</param>
        /// <param name="timeout">Optopnally specifies the operation timeout.  This defaults to 30 seconds.</param>
        /// <param name="cancellationToken">Optionally specifies the cancellation token.</param>
        /// <returns>The tracking <see cref="Task"/>.</returns>
        /// <remarks>
        /// One of <paramref name="name"/>, <paramref name="labelSelector"/>, or <paramref name="fieldSelector"/>
        /// must be specified.
        /// </remarks>
        public static async Task WaitForDaemonsetAsync(

            this IAppsV1Operations  k8sAppsV1,
            string                  namespaceParameter,
            string                  name              = null,
            string                  labelSelector     = null,
            string                  fieldSelector     = null,
            TimeSpan                pollInterval      = default,
            TimeSpan                timeout           = default,
            CancellationToken       cancellationToken = default)
        {
            await SyncContext.Clear;
            Covenant.Requires<ArgumentException>(!string.IsNullOrEmpty(name) || labelSelector != null || fieldSelector != null, "One of [name], [labelSelector] or [fieldSelector] must be passed.");
            Covenant.Requires<ArgumentNullException>(!string.IsNullOrEmpty(namespaceParameter), nameof(namespaceParameter));

            if (pollInterval <= TimeSpan.Zero)
            {
                pollInterval = TimeSpan.FromSeconds(1);
            }

            if (timeout <= TimeSpan.Zero)
            {
                timeout = TimeSpan.FromSeconds(30);
            }

            if (!string.IsNullOrEmpty(name))
            {
                if (!string.IsNullOrEmpty(fieldSelector))
                {
                    fieldSelector += $",metadata.name={name}";
                }
                else
                {
                    fieldSelector = $"metadata.name={name}";
                }
            }
            await NeonHelper.WaitForAsync(
                async () =>
                {
                    try
                    {
                        var daemonsets = await k8sAppsV1.ListNamespacedDaemonSetAsync(namespaceParameter, fieldSelector: fieldSelector, labelSelector: labelSelector);

                        if (daemonsets == null || daemonsets.Items.Count == 0)
                        {
                            return false;
                        }

                        return daemonsets.Items.All(@set => @set.Status.NumberAvailable == @set.Status.DesiredNumberScheduled);
                    }
                    catch
                    {
                        return false;
                    }
                },
                timeout:           timeout,
                pollInterval:      pollInterval,
                cancellationToken: cancellationToken);
        }

        /// <summary>
        /// Waits for a pod to start successfully.
        /// </summary>
        /// <param name="k8sCoreV1">The <see cref="Kubernetes"/> client's <see cref="ICoreV1Operations"/>.</param>
        /// <param name="name">The pod name.</param>
        /// <param name="namespaceParameter">The namespace.</param>
        /// <param name="pollInterval">Optionally specifies the polling interval.  This defaults to 1 second.</param>
        /// <param name="timeout">Optopnally specifies the operation timeout.  This defaults to 30 seconds.</param>
        /// <param name="cancellationToken">Optionally specifies the cancellation token.</param>
        /// <returns>The tracking <see cref="Task"/>.</returns>x
        public static async Task WaitForPodAsync(
            this ICoreV1Operations  k8sCoreV1, 
            string                  name, 
            string                  namespaceParameter, 
            TimeSpan                pollInterval      = default,
            TimeSpan                timeout           = default,
            CancellationToken       cancellationToken = default)
        {
            await SyncContext.Clear;
            Covenant.Requires<ArgumentException>(!string.IsNullOrEmpty(name), nameof(name));
            Covenant.Requires<ArgumentNullException>(!string.IsNullOrEmpty(namespaceParameter), nameof(namespaceParameter));

            if (pollInterval <= TimeSpan.Zero)
            {
                pollInterval = TimeSpan.FromSeconds(1);
            }

            if (timeout <= TimeSpan.Zero)
            {
                timeout = TimeSpan.FromSeconds(30);
            }

            await NeonHelper.WaitForAsync(
                async () =>
                {
                    try
                    {
                        var pod = await k8sCoreV1.ReadNamespacedPodAsync(name, namespaceParameter, cancellationToken: cancellationToken);

                        return pod.Status.Phase == "Running";
                    }
                    catch
                    {
                        return false;
                    }
                            
                },
                timeout:           timeout,
                pollInterval:      pollInterval,
                cancellationToken: cancellationToken);
        }

        /// <summary>
        /// Waits for a custom resource definition to be created in the API server.
        /// </summary>
        /// <typeparam name="TEntity">Specifies the custom resource type.</typeparam>
        /// <param name="k8sApiextensionsV1">The <see cref="Kubernetes"/> client's <see cref="IApiextensionsV1Operations"/>.</param>
        /// <param name="pollInterval">Optionally specifies the polling interval.  This defaults to 5 seconds.</param>
        /// <param name="timeout">Optionally specifies the maximum time to wait.  This defaults to 90 seconds.</param>
        /// <param name="cancellationToken">Optionally specifies a cancellation token.</param>
        /// <returns>The tracking <see cref="Task"/>.</returns>
        public static async Task WaitForCustomResourceDefinitionAsync<TEntity>(
            this IApiextensionsV1Operations k8sApiextensionsV1,
            TimeSpan                        pollInterval      = default,
            TimeSpan                        timeout           = default,
            CancellationToken               cancellationToken = default)

            where TEntity : IKubernetesObject<V1ObjectMeta>
        {
            await SyncContext.Clear;

            if (pollInterval <= TimeSpan.Zero)
            {
                pollInterval = TimeSpan.FromSeconds(5);
            }

            if (timeout <= TimeSpan.Zero)
            {
                timeout = TimeSpan.FromSeconds(90);
            }

            await NeonHelper.WaitForAsync(
                async () =>
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    try
                    {
                        var typeMetadata                     = typeof(TEntity).GetKubernetesTypeMetadata();
                        var pluralNameGroup                  = string.IsNullOrEmpty(typeMetadata.Group) ? typeMetadata.PluralName : $"{typeMetadata.PluralName}.{typeMetadata.Group}";
                        var existingList                     = await k8sApiextensionsV1.ListCustomResourceDefinitionAsync(fieldSelector: $"metadata.name={pluralNameGroup}");
                        var existingCustomResourceDefinition = existingList?.Items?.SingleOrDefault();

                        if (existingCustomResourceDefinition != null)
                        {
                            return true;
                        }
                        else
                        {
                            return false;
                        }
                    }
                    catch
                    {
                        return false;
                    }
                },
                timeout:        timeout,
                pollInterval:   pollInterval,
                timeoutMessage: $"Timeout waiting for CRD: {typeof(TEntity).FullName}");
        }

        /// <summary>
        /// Returns a running pod within the specified namespace that matches a label selector. 
        /// </summary>
        /// <param name="k8sCoreV1">The <see cref="Kubernetes"/> client's <see cref="ICoreV1Operations"/>.</param>
        /// <param name="namespaceParameter">Specifies the namespace hosting the pod.</param>
        /// <param name="labelSelector">
        /// Specifies the label selector to constrain the set of pods to be targeted.
        /// This is required.
        /// </param>
        /// <returns>The <see cref="V1Pod"/>.</returns>
        /// <exception cref="KubernetesException">Thrown when no healthy pods exist.</exception>
        public static async Task<V1Pod> GetNamespacedRunningPodAsync(
            this ICoreV1Operations  k8sCoreV1,
            string                  namespaceParameter,
            string                  labelSelector)
        {
            await SyncContext.Clear;
            Covenant.Requires<ArgumentNullException>(!string.IsNullOrEmpty(namespaceParameter), nameof(namespaceParameter));
            Covenant.Requires<ArgumentNullException>(!string.IsNullOrEmpty(labelSelector), nameof(labelSelector));

            var pods = (await k8sCoreV1.ListNamespacedPodAsync(namespaceParameter, labelSelector: labelSelector)).Items;
            var pod  =  pods.FirstOrDefault(pod => pod.Status.Phase == "Running");

            if (pod == null)
            {
                throw new KubernetesException(pods.Count > 0 ? $"[0 of {pods.Count}] pods are running." : "No deployed pods.");
            }

            return pod;
        }

        /// <summary>
        /// Executes a command within a pod container.
        /// </summary>
        /// <param name="k8s">The <see cref="Kubernetes"/> client.</param>
        /// <param name="name">Specifies the target pod name.</param>
        /// <param name="namespaceParameter">Specifies the namespace hosting the pod.</param>
        /// <param name="container">Identifies the target container within the pod.</param>
        /// <param name="command">Specifies the program and arguments to be executed.</param>
        /// <param name="noSuccessCheck">Optionally disables the <see cref="ExecuteResponse.EnsureSuccess"/> check.</param>
        /// <param name="cancellationToken">Optionally specifies a cancellation token.</param>
        /// <returns>An <see cref="ExecuteResponse"/> with the command exit code and output and error text.</returns>
        /// <exception cref="ExecuteException">Thrown if the exit code isn't zero and <paramref name="noSuccessCheck"/><c>=false</c>.</exception>
        public static async Task<ExecuteResponse> NamespacedPodExecAsync(
            this IKubernetes        k8s,
            string                  name,
            string                  namespaceParameter,
            string                  container,
            string[]                command,
            bool                    noSuccessCheck    = false,
            CancellationToken       cancellationToken = default)
        {
            await SyncContext.Clear;
            Covenant.Requires<ArgumentNullException>(name != null, nameof(name));
            Covenant.Requires<ArgumentNullException>(!string.IsNullOrEmpty(namespaceParameter), nameof(namespaceParameter));
            Covenant.Requires<ArgumentNullException>(command != null, nameof(command));
            Covenant.Requires<ArgumentException>(command.Length > 0, nameof(command));
            Covenant.Requires<ArgumentException>(!string.IsNullOrEmpty(command[0]), nameof(command));
            Covenant.Requires<ArgumentNullException>(container != null, nameof(container));

            var stdOut = "";
            var stdErr = "";

            var handler = new ExecAsyncCallback(async (_stdIn, _stdOut, _stdError) =>
            {
                stdOut = Encoding.UTF8.GetString(await _stdOut.ReadToEndAsync());
                stdErr = Encoding.UTF8.GetString(await _stdError.ReadToEndAsync());
            });

            var exitCode = await k8s.NamespacedPodExecAsync(
                name:              name,
                @namespace:        namespaceParameter,
                container:         container,
                command:           command,
                tty:               false,
                action:            handler,
                cancellationToken: cancellationToken);

            var response = new ExecuteResponse(exitCode, stdOut, stdErr);

            if (!noSuccessCheck)
            {
                response.EnsureSuccess();
            }

            return response;
        }

        /// <summary>
        /// Executes a command within a pod container with a <see cref="IRetryPolicy"/>
        /// </summary>
        /// <param name="k8s">The <see cref="Kubernetes"/> client.</param>
        /// <param name="retryPolicy">The <see cref="IRetryPolicy"/>.</param>
        /// <param name="name">Specifies the target pod name.</param>
        /// <param name="namespaceParameter">Specifies the namespace hosting the pod.</param>
        /// <param name="container">Identifies the target container within the pod.</param>
        /// <param name="command">Specifies the program and arguments to be executed.</param>
        /// <param name="cancellationToken">Optionally specifies a cancellation token.</param>
        /// <returns>An <see cref="ExecuteResponse"/> with the command exit code and output and error text.</returns>
        /// <exception cref="ExecuteException">Thrown if the exit code isn't zero.</exception>
        public static async Task<ExecuteResponse> NamespacedPodExecWithRetryAsync(
            this IKubernetes    k8s,
            IRetryPolicy        retryPolicy,
            string              name,
            string              namespaceParameter,
            string              container,
            string[]            command,
            CancellationToken   cancellationToken = default)
        {
            await SyncContext.Clear;
            Covenant.Requires<ArgumentNullException>(retryPolicy != null, nameof(retryPolicy));

            return await retryPolicy.InvokeAsync(
                async () =>
                {
                    return await k8s.NamespacedPodExecAsync(
                        name:               name,
                        namespaceParameter: namespaceParameter,
                        container:          container,
                        command:            command,
                        cancellationToken:  cancellationToken,
                        noSuccessCheck:     true);
                });
        }

        /// <summary>
        /// Watches a Kubernetes resource with a callback.
        /// </summary>
        /// <typeparam name="T">The type parameter.</typeparam>
        /// <param name="k8s">The <see cref="IKubernetes"/> instance.</param>
        /// <param name="actionAsync">The async action called as watch events are received.</param>
        /// <param name="namespaceParameter">Optionally specifies a Kubernetes namespace.</param>
        /// <param name="fieldSelector">Optionally specifies a field selector</param>
        /// <param name="labelSelector">Optionally specifies a label selector</param>
        /// <param name="resourceVersion">Optionally specifies a resource version.</param>
        /// <param name="resourceVersionMatch">Optionally specifies a <b>resourceVersionMatch</b> setting.</param>
        /// <param name="timeoutSeconds">Optionally specifies a timeout override.</param>
        /// <param name="cancellationToken">Optionally specifies a cancellation token.</param>
        /// <param name="logger">Optionally specifies a <see cref="ILogger"/>.</param>
        /// <returns>The tracking <see cref="Task"/>.</returns>
        public static async Task WatchAsync<T>(
            this IKubernetes            k8s,
            Func<WatchEvent<T>, Task>   actionAsync,
            string                      namespaceParameter   = null,
            string                      fieldSelector        = null,
            string                      labelSelector        = null,
            string                      resourceVersion      = null,
            string                      resourceVersionMatch = null,
            int?                        timeoutSeconds       = null,
            CancellationToken           cancellationToken    = default,
            ILogger                     logger               = null) 
            
            where T : IKubernetesObject<V1ObjectMeta>, new()
        {
            await new Watcher<T>(k8s, logger).WatchAsync(actionAsync,
                namespaceParameter,
                fieldSelector:        fieldSelector,
                labelSelector:        labelSelector,
                resourceVersion:      resourceVersion,
                resourceVersionMatch: resourceVersionMatch,
                timeoutSeconds:       timeoutSeconds,
                cancellationToken:    cancellationToken);
        }

        /// <summary>
        /// Lists pods from all cluster namespaces.
        /// </summary>
        /// <param name="k8sCoreV1">The <see cref="IKubernetes"/> instance.</param>
        /// <returns>The <see cref="V1PodList"/>.</returns>
        public static async Task<V1PodList> ListAllPodsAsync(this ICoreV1Operations k8sCoreV1)
        {
            await SyncContext.Clear;

            // Clusters may have hundreds or even thousands of namespaces, so we don't
            // want to query for pods one namespace at a time because that may take too
            // long.  But we also don't want to slam the API server with potentially
            // thousands of pod queries all at once.
            //
            // We're going to queries for all of the namespaces and then perform pod
            // queries in parallel, but limiting that concurrency to something reasonable.

            const int podListConcurency = 100;

            var namespaces = (await k8sCoreV1.ListNamespaceAsync()).Items;
            var pods       = new V1PodList() { Items = new List<V1Pod>() };

            await Parallel.ForEachAsync(namespaces, new ParallelOptions() { MaxDegreeOfParallelism = podListConcurency },
                async (@namespace, cancellationToken) =>
                {
                    var namespacedPods = await k8sCoreV1.ListNamespacedPodAsync(@namespace.Name());

                    lock (pods)
                    {
                        foreach (var pod in namespacedPods.Items)
                        {
                            pods.Items.Add(pod);
                        }
                    }
                });

            return pods;
        }
    }
}
