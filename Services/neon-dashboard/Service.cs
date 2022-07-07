//-----------------------------------------------------------------------------
// FILE:	    Service.cs
// CONTRIBUTOR: Marcus Bowyer
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

using System.Threading.Tasks;
using System.Net;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

using Microsoft.AspNetCore;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;

using Neon.Common;
using Neon.Cryptography;
using Neon.Diagnostics;
using Neon.Kube;
using Neon.Kube.Resources;
using Neon.Service;
using Neon.Tasks;

using NeonDashboard.Shared.Components;

using k8s;
using k8s.Models;

using Prometheus;
using Prometheus.DotNetRuntime;

namespace NeonDashboard
{
    /// <summary>
    /// Implements the <b>neon-dashboard</b> service.
    /// </summary>
    public class Service : NeonService
    {
        // class fields
        private IWebHost webHost;

        /// <summary>
        /// The Kubernetes client.
        /// </summary>
        public KubernetesWithRetry Kubernetes;

        /// <summary>
        /// Information about the cluster.
        /// </summary>
        public ClusterInfo ClusterInfo;

        /// <summary>
        /// Dashboards available.
        /// </summary>
        public List<Dashboard> Dashboards;

        /// <summary>
        /// SSO Client Secret.
        /// </summary>
        public string SsoClientSecret;

        /// <summary>
        /// AES Cipher for protecting cookies..
        /// </summary>
        public AesCipher AesCipher;

        /// <summary>
        /// USe to turn off Segment tracking.
        /// </summary>
        public bool DoNotTrack;

        /// <summary>
        /// Prometheus Client.
        /// </summary>
        public PrometheusClient PrometheusClient;

        /// <summary>
        /// Session cookie name.
        /// </summary>
        public const string sessionCookieName = ".NeonKUBE.Dashboard.Session.Cookie";

        /// <summary>
        /// Dashboard view counter.
        /// </summary>
        public readonly Counter DashboardViewCounter;

        /// <summary>
        /// Constructor.
        /// </summary>
        /// <param name="name">The service name.</param>
        public Service(string name)
             : base(name, version: KubeVersions.NeonKube, metricsPrefix: "neondashboard")
        {
            DashboardViewCounter = Metrics.CreateCounter($"{MetricsPrefix}external_dashboard_view", "External dashboard views.",
                new CounterConfiguration
                {
                    LabelNames = new[] { "dashboard" }
                });
        }

        /// <inheritdoc/>
        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);

            // Dispose web host if it's still running.

            if (webHost != null)
            {
                webHost.Dispose();
                webHost = null;
            }
        }

        /// <inheritdoc/>
        protected async override Task<int> OnRunAsync()
        {
            await SetStatusAsync(NeonServiceStatus.Starting);

            var port = 80;

            Kubernetes = new KubernetesWithRetry(KubernetesClientConfiguration.BuildDefaultConfig());

            _ = Kubernetes.WatchAsync<V1ConfigMap>(async (@event) =>
            {
                await SyncContext.Clear;

                ClusterInfo = TypeSafeConfigMap<ClusterInfo>.From(@event.Value).Config;
                Log.LogInfo($"Updated cluster info");
            },
            KubeNamespace.NeonStatus,
            fieldSelector: $"metadata.name={KubeConfigMapName.ClusterInfo}");

            Dashboards = new List<Dashboard>();
            Dashboards.Add(
                new Dashboard(
                    id:           "neonkube", 
                    name:         "neonKUBE",
                    displayOrder: 0));

            _ = Kubernetes.WatchAsync<V1NeonDashboard>(async (@event) =>
            {
                await SyncContext.Clear;

                switch (@event.Type)
                {
                    case WatchEventType.Added:

                        await AddDashboardAsync(@event.Value);
                        break;

                    case WatchEventType.Deleted:

                        await RemoveDashboardAsync(@event.Value);
                        break;

                    case WatchEventType.Modified:

                        await RemoveDashboardAsync(@event.Value);
                        await AddDashboardAsync(@event.Value);
                        break;

                    default:

                        break;
                }

                Dashboards = Dashboards.OrderBy(d => d.DisplayOrder)
                                        .ThenBy(d => d.Name).ToList();
            });

            if (NeonHelper.IsDevWorkstation)
            {
                port = 11001;
                SetEnvironmentVariable("LOG_LEVEL", "debug");
                SetEnvironmentVariable("DO_NOT_TRACK", "true");
                await ConfigureDevAsync();
            }

            var metricsHost = GetEnvironmentVariable("METRICS_HOST", "http://mimir-query-frontend.neon-monitor.svc.cluster.local:8080");
            PrometheusClient = new PrometheusClient($"{metricsHost}/prometheus/");

            SsoClientSecret = GetEnvironmentVariable("SSO_CLIENT_SECRET", redacted: !Log.IsLogDebugEnabled);

            AesCipher = new AesCipher(GetEnvironmentVariable("COOKIE_CIPHER", AesCipher.GenerateKey(), redacted: !Log.IsLogDebugEnabled));

            // Start the web service.

            webHost = new WebHostBuilder()
                .ConfigureAppConfiguration(
                    (hostingcontext, config) =>
                    {
                        config.Sources.Clear();
                    })
                .UseStartup<Startup>()
                .UseKestrel(options => options.Listen(IPAddress.Any, port))
                .ConfigureServices(services => services.AddSingleton(typeof(Service), this))
                .UseStaticWebAssets()
                .Build();

            _ = webHost.RunAsync();

            Log.LogInfo($"Listening on {IPAddress.Any}:{port}");

            // Indicate that the service is running.

            await StartedAsync();

            // Handle termination gracefully.

            await Terminator.StopEvent.WaitAsync();
            Terminator.ReadyToExit();

            return 0;
        }

        private async Task AddDashboardAsync(V1NeonDashboard dashboard)
        {
            await SyncContext.Clear;

            if (string.IsNullOrEmpty(dashboard.Spec.DisplayName))
            {
                dashboard.Spec.DisplayName = dashboard.Name();
            }

            Dashboards.Add(
                new Dashboard(
                    id:           dashboard.Name(),
                    name:         dashboard.Spec.DisplayName,
                    url:          dashboard.Spec.Url,
                    displayOrder: dashboard.Spec.DisplayOrder));
        }
        private async Task RemoveDashboardAsync(V1NeonDashboard dashboard)
        {
            await SyncContext.Clear;

            Dashboards.Remove(
                Dashboards.Where(
                    d => d.Id == dashboard.Name())?.First());
        }

        public async Task ConfigureDevAsync()
        {
            await SyncContext.Clear;

            Log.LogInfo("Configuring cluster SSO for development.");

            // wait for cluster info to be set
            await NeonHelper.WaitForAsync(async () =>
            {
                await SyncContext.Clear;

                return (ClusterInfo != null);
            }, 
            timeout: TimeSpan.FromSeconds(60),
            pollInterval: TimeSpan.FromMilliseconds(250));

            try
            {
                var secret = await Kubernetes.ReadNamespacedSecretAsync("neon-sso-dex", KubeNamespace.NeonSystem);

                SetEnvironmentVariable("SSO_CLIENT_SECRET", Encoding.UTF8.GetString(secret.Data["KUBERNETES_CLIENT_SECRET"]));

                // Configure cluster callback url to allow local dev
                
                var dexConfigMap = await Kubernetes.ReadNamespacedConfigMapAsync("neon-sso-dex", KubeNamespace.NeonSystem);
                var dexConfig    = NeonHelper.YamlDeserializeViaJson<DexConfig>(dexConfigMap.Data["config.yaml"]);
                var clientConfig = dexConfig.StaticClients.Where(c => c.Id == "kubernetes").First();

                if (!clientConfig.RedirectUris.Contains("http://localhost:11001/oauth2/callback"))
                {
                    clientConfig.RedirectUris.Add("http://localhost:11001/oauth2/callback");
                    dexConfigMap.Data["config.yaml"] = NeonHelper.ToLinuxLineEndings(NeonHelper.YamlSerialize(dexConfig));
                    await Kubernetes.ReplaceNamespacedConfigMapAsync(dexConfigMap, dexConfigMap.Metadata.Name, KubeNamespace.NeonSystem);
                }

                Log.LogInfo("SSO configured.");
            }
            catch (Exception e)
            {
                Log.LogError("Error configuring SSO", e);
            }

            Log.LogInfo("Configure metrics.");

            var virtualServices = await Kubernetes.ListNamespacedCustomObjectAsync<VirtualService>(KubeNamespace.NeonIngress);
            if (!virtualServices.Items.Any(vs => vs.Name() == "metrics-external"))
            {
                var virtualService = new VirtualService()
                {
                    Metadata = new V1ObjectMeta()
                    {
                        Name = "metrics-external",
                        NamespaceProperty = KubeNamespace.NeonIngress
                    },
                    Spec = new VirtualServiceSpec()
                    {
                        Gateways = new List<string>() { "neoncluster-gateway" },
                        Hosts = new List<string>() { $"metrics.{ClusterInfo.Domain}" },
                        Http = new List<HTTPRoute>()
                    {
                        new HTTPRoute()
                        {
                            Match = new List<HTTPMatchRequest>()
                            {
                                new HTTPMatchRequest()
                                {
                                    Uri = new StringMatch()
                                    {
                                        Prefix = "/"
                                    }
                                }
                            },
                            Route = new List<HTTPRouteDestination>()
                            {
                                new HTTPRouteDestination()
                                {
                                    Destination = new Destination()
                                    {
                                        Host = "mimir-query-frontend.neon-monitor.svc.cluster.local",
                                        Port = new PortSelector()
                                        {
                                            Number = 8080
                                        }
                                    }
                                }
                            }
                        }
                    }
                    }
                };

                await Kubernetes.CreateNamespacedCustomObjectAsync<VirtualService>(virtualService, virtualService.Name(), KubeNamespace.NeonIngress);
            }
            SetEnvironmentVariable("METRICS_HOST", $"https://metrics.{ClusterInfo.Domain}");
        }
    }
}