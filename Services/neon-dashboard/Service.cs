//-----------------------------------------------------------------------------
// FILE:	    Service.cs
// CONTRIBUTOR: Marcus Bowyer
// COPYRIGHT:   Copyright (c) 2005-2022 by neonFORGE LLC.  All rights reserved.

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
using Neon.Diagnostics;
using Neon.Kube;
using Neon.Service;
using Neon.Tasks;

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
        /// SSO Client Secret.
        /// </summary>
        public string SsoClientSecret;

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

            if (NeonHelper.IsDevWorkstation)
            {
                port = 11001;
                SetEnvironmentVariable("LOG_LEVEL", "debug");
                SetEnvironmentVariable("DO_NOT_TRACK", "true");
                await ConfigureDevAsync();
            }

            var metricsHost = GetEnvironmentVariable("METRICS_HOST", "http://mimir-query-frontend.neon-monitor.svc.cluster.local:8080");
            PrometheusClient = new PrometheusClient($"{metricsHost}/prometheus/");

            SsoClientSecret = GetEnvironmentVariable("SSO_CLIENT_SECRET", redacted: true);

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
                // set config map
                var configMap = await Kubernetes.ReadNamespacedConfigMapAsync("neon-dashboard", KubeNamespace.NeonSystem);

                SetConfigFile("/etc/neon-dashboard/dashboards.yaml", configMap.Data["dashboards.yaml"]);

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

                await Kubernetes.CreateNamespacedCustomObjectAsync<VirtualService>(virtualService, KubeNamespace.NeonIngress);
            }
            SetEnvironmentVariable("METRICS_HOST", $"https://metrics.{ClusterInfo.Domain}");
        }
    }
}