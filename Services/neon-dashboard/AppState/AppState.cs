﻿//-----------------------------------------------------------------------------
// FILE:	    AppState.cs
// CONTRIBUTOR: Marcus Bowyer
// COPYRIGHT:   Copyright (c) 2005-2022 by neonFORGE LLC.  All rights reserved.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.IO;
using System.Reflection;
using System.Security.Claims;
using System.Security.Cryptography.X509Certificates;
using System.Threading.Tasks;

using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.JSInterop;
using Microsoft.AspNetCore.Hosting;

using Neon.Common;
using Neon.Diagnostics;
using Neon.Kube;
using Neon.Tasks;

using NeonDashboard.Shared;
using NeonDashboard.Shared.Components;

using Blazor.Analytics;
using Blazor.Analytics.Components;

using Blazored.LocalStorage;

namespace NeonDashboard
{
    /// <summary>
    /// App state scoped to the user session.
    /// </summary>
    public partial class AppState
    {
        /// <summary>
        /// The Neon Dashboard Service.
        /// </summary>
        public Service NeonDashboardService;

        /// <summary>
        /// Cluster Info
        /// </summary>
        public ClusterInfo ClusterInfo => NeonDashboardService.ClusterInfo;

        /// <summary>
        /// Kubernetes related state.
        /// </summary>
        public __Kube Kube;

        /// <summary>
        /// Metrics related state.
        /// </summary>
        public __Metrics Metrics;

        /// <summary>
        /// Metrics related state.
        /// </summary>
        public __Cache Cache;

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
        /// The HttpContext for the initial request.
        /// </summary>
        public HttpContext HttpContext;

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
        /// Browser Local Storage.
        /// </summary>
        public ILocalStorageService LocalStorage;

        /// <summary>
        /// Redis Cache.
        /// </summary>
        public IDistributedCache DistributedCache;

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

        /// <summary>
        /// Cluster ID.
        /// </summary>
        public string ClusterId;

        /// <summary>
        /// User ID.
        /// </summary>
        public string UserId = null;

        public AppState(
            Service                 neonDashboardService,
            IHttpContextAccessor    httpContextAccessor,
            INeonLogger             neonLogger,
            IJSRuntime              jSRuntime,
            NavigationManager       navigationManager,
            IWebHostEnvironment     webHostEnv,
            IAnalytics              analytics,
            ILocalStorageService    localStorage,
            IDistributedCache       cache)
        {
            this.NeonDashboardService = neonDashboardService;
            this.NavigationManager    = navigationManager;
            this.Logger               = neonLogger;
            this.JSRuntime            = jSRuntime;
            this.HttpContextAccessor  = httpContextAccessor;
            this.WebHostEnvironment   = webHostEnv;
            this.Analytics            = analytics;
            this.LocalStorage         = localStorage;
            this.DistributedCache     = cache;
            this.Kube                 = new __Kube(this);
            this.Metrics              = new __Metrics(this);
            this.Cache                = new __Cache(this);

            if (NeonDashboardService.DoNotTrack)
            {
                Analytics.Disable();
            }

            if (Dashboards == null || Dashboards.Count == 0)
            {
                ClusterId  = neonDashboardService.ClusterInfo.Domain;
                Dashboards = new List<Dashboard>();

                Dashboards.Add(new Dashboard("neonkube", "neonKUBE"));

                using (var sr = new StreamReader(neonDashboardService.GetConfigFilePath("/etc/neon-dashboard/dashboards.yaml")))
                {
                    var dashboards = NeonHelper.YamlDeserialize<List<Dashboard>>(sr.ReadToEnd());

                    Dashboards = Dashboards.Concat(dashboards).ToList();

                    Logger.LogDebug(NeonHelper.JsonSerialize(Dashboards));
                }

                DashboardFrames = new List<Dashboard>(); 

                if (string.IsNullOrEmpty(UserId))
                {
                    if (HttpContextAccessor.HttpContext.User.HasClaim(c => c.Type == ClaimTypes.NameIdentifier))
                    {
                        UserId = HttpContextAccessor.HttpContext.User.Claims.Where(c => c.Type == ClaimTypes.NameIdentifier).First().Value;

                        var traits = new Dictionary<string, object>()
                        {
                            { "Name", UserId },
                            { "Email", HttpContextAccessor.HttpContext.User.Claims.Where(c => c.Type == ClaimTypes.Email).First().Value }
                        };

                        if (!HttpContextAccessor.HttpContext.WebSockets.IsWebSocketRequest)
                        {
                            Segment.Analytics.Client.Identify(UserId, traits);
                        }
                    }
                }
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
            await SyncContext.Clear;

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

        public Dashboard GetCurrentDashboard(string dashboardId)
        {
            return Dashboards.Where(d => d.Id == dashboardId).FirstOrDefault();
        }
    }
}