﻿//-----------------------------------------------------------------------------
// FILE:	    AppState.cs
// CONTRIBUTOR: Marcus Bowyer
// COPYRIGHT:   Copyright (c) 2005-2022 by neonFORGE LLC.  All rights reserved.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography.X509Certificates;
using System.Threading.Tasks;

using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Http;
using Microsoft.JSInterop;
using Microsoft.AspNetCore.Hosting;

using Neon.Diagnostics;
using Neon.Kube;

using NeonDashboard.Shared;
using NeonDashboard.Shared.Components;

using Blazor.Analytics;
using Blazor.Analytics.Components;

namespace NeonDashboard
{
    public partial class AppState
    {
        /// <summary>
        /// The Neon Dashboard Service.
        /// </summary>
        public NeonDashboardService NeonDashboardService;

        /// <summary>
        /// The Navigation Manager.
        /// </summary>
        public NavigationManager NavigationManager;

        /// <summary>
        /// The Google Analytics Tracker.
        /// </summary>
        public IAnalytics Analytics;

        /// <summary>
        /// The Context Accessor.
        /// </summary>
        public IHttpContextAccessor HttpContextAccessor;

        /// <summary>
        /// The Navigation Manager.
        /// </summary>
        public INeonLogger Logger;

        /// <summary>
        /// The Web Host Environment.
        /// </summary>
        public IWebHostEnvironment WebHostEnvironment;

        /// <summary>
        /// Javascript interop.
        /// </summary>
        public IJSRuntime JSRuntime;

        /// <summary>
        /// Bool to check whether it's ok to run javascript.
        /// </summary>
        public bool JsEnabled => HttpContextAccessor.HttpContext.WebSockets.IsWebSocketRequest;

        /// <summary>
        /// List of dashboards that can be displayed.
        /// </summary>
        public List<Dashboard> Dashboards;

        /// <summary>
        /// List of dashboards that have been loaded.
        /// </summary>
        public List<Dashboard> DashboardFrames { get; set; } = new List<Dashboard>();

        /// <summary>
        /// The name of the currently selected dashboard.
        /// </summary>
        public string CurrentDashboard;

        public AppState(
            NeonDashboardService neonDashboardService,
            IHttpContextAccessor httpContextAccessor,
            INeonLogger neonLogger,
            IJSRuntime jSRuntime,
            NavigationManager navigationManager,
            IWebHostEnvironment webHostEnv,
            IAnalytics analytics
            )
        {
            this.NeonDashboardService = neonDashboardService;
            this.NavigationManager = navigationManager;
            this.Logger = neonLogger;
            this.JSRuntime = jSRuntime;
            this.HttpContextAccessor = httpContextAccessor;
            this.WebHostEnvironment = webHostEnv;
            this.Analytics = analytics;

            if (Dashboards == null || Dashboards.Count == 0)
            {
                var clusterDomain = neonDashboardService.GetEnvironmentVariable("CLUSTER_DOMAIN");
                Dashboards = new List<Dashboard>()
                {
                    //new Dashboard("Kubernetes", $"https://{ClusterDomain.KubernetesDashboard}.{clusterDomain}"),
                    //new Dashboard("Grafana", $"https://{ClusterDomain.Grafana}.{clusterDomain}"),
                    //new Dashboard("Minio", $"https://{ClusterDomain.MinioOperator}.{clusterDomain}"),
                    //new Dashboard("Kiali", $"https://{ClusterDomain.Kiali}.{clusterDomain}"),
                    //new Dashboard("Harbor", $"https://{ClusterDomain.HarborRegistry}.{clusterDomain}")

                    new Dashboard("loooooopie", $"https://loopielaundry.com/"),
                    new Dashboard("uwu", $"https://www.dictionary.com/e/slang/uwu"),
                };

                DashboardFrames = new List<Dashboard>(); 
            }
        }

        public event Action OnDashboardChange;
        public void NotifyDashboardChanged() => OnDashboardChange?.Invoke();

        public event Action OnChange;
        private void NotifyStateChanged() => OnChange?.Invoke();

        public bool ShowSidebar { get; private set; } = false;

        public event Action OnSidebarChange;
        private void NotifySidebarChanged() => OnSidebarChange?.Invoke();

        public void ToggleSidebar()
        {
            ShowSidebar = !ShowSidebar;
            NotifySidebarChanged();
        }

        /// <summary>
        /// Track Exceptions in Google Analytics.
        /// </summary>
        /// <param name="method"></param>
        /// <param name="exception"></param>
        /// <param name="isFatal"></param>
        /// <returns>The tracking <see cref="Task"/>.</returns>
        public async Task TrackExceptionAsync(MethodBase method, Exception exception, bool? isFatal = false)
        {
            Logger.LogError(exception);

            await Analytics.TrackEvent(method.Name, new 
            { 
                Category = "Exception", 
                Labels = new Dictionary<string, string>()
                {
                    { "Exception", $"{method.Name}::{exception.GetType().Name}" },
                    { "IsFatal", $"{isFatal}" }
                },
                Message = exception.Message
            });
        }

        public void LogException(Exception e)
        {
            Logger.LogError(e);
        }
    }
}