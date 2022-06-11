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
using System.Text;
using System.Threading.Tasks;

using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.JSInterop;
using Microsoft.AspNetCore.Hosting;

using Neon.Common;
using Neon.Diagnostics;
using Neon.Kube;
using Neon.Net;
using Neon.Tasks;

using NeonDashboard.Shared;
using NeonDashboard.Shared.Components;

using Blazor.Analytics;
using Blazor.Analytics.Components;

using Blazored.LocalStorage;

using k8s;
using k8s.Models;

using Prometheus;
using System.Globalization;
using Neon.Collections;

namespace NeonDashboard
{
    public partial class AppState
    {
        /// <summary>
        /// Metrics related state.
        /// </summary>
        public class __Metrics : AppStateBase
        {
            /// <summary>
            /// Event action for updates to Kube properties.
            /// </summary>
            public event Action OnChange;
            private void NotifyStateChanged() => OnChange?.Invoke();

            private PrometheusClient PrometheusClient => NeonDashboardService.PrometheusClient;

            /// <summary>
            /// Prometheis result containing the total memory usage for the cluster.
            /// </summary>
            public PrometheusResponse<PrometheusMatrixResult> MemoryUsageBytes;
            
            /// <summary>
            /// The total amount of memory available to the cluster.
            /// </summary>
            public decimal MemoryTotalBytes;

            /// <summary>
            /// Prometheus result containing the CPU use percentage for the cluster.
            /// </summary>
            public PrometheusResponse<PrometheusMatrixResult> CPUUsagePercent;
            
            /// <summary>
            /// The total number of CPU cores available to the cluster.
            /// </summary>
            public decimal CPUTotal;

            /// <summary>
            /// Prometheus result containing the total disk usage for the cluster.
            /// </summary>
            public PrometheusResponse<PrometheusMatrixResult> DiskUsageBytes;
            
            /// <summary>
            /// The total amount of disk space available to the cluster.
            /// </summary>
            public decimal DiskTotalBytes;

            /// <summary>
            /// Constructor.
            /// </summary>
            /// <param name="state"></param>
            public __Metrics(AppState state)
                : base(state)
            {
                
            }

            /// <summary>
            /// Get the total memory usage for the cluster.
            /// </summary>
            /// <param name="start"></param>
            /// <param name="end"></param>
            /// <param name="stepSize"></param>
            /// <returns></returns>
            public async Task<PrometheusResponse<PrometheusMatrixResult>> GetMemoryUsageAsync(DateTime start, DateTime end, string stepSize = "15s")
            {
                await SyncContext.Clear;

                var query = $@"sum(node_memory_MemTotal_bytes{{cluster=~""{NeonDashboardService.ClusterInfo.Name}""}}) - sum(node_memory_MemFree_bytes{{cluster=~""{NeonDashboardService.ClusterInfo.Name}""}})";
                MemoryUsageBytes = await QueryRangeAsync(query, start, end, stepSize);

                NotifyStateChanged();

                return MemoryUsageBytes;
            }

            /// <summary>
            /// Gets the total amount of memory available to the cluster.
            /// </summary>
            /// <returns></returns>
            public async Task<decimal> GetMemoryTotalAsync()
            {
                await SyncContext.Clear;

                var query = $@"sum(node_memory_MemTotal_bytes{{cluster=~""{NeonDashboardService.ClusterInfo.Name}""}})";
                MemoryTotalBytes = decimal.Parse((await QueryAsync(query)).Data.Result.First().Value.Value);

                NotifyStateChanged();

                return MemoryTotalBytes;
            }

            /// <summary>
            /// Gets the CPU usage from the cluster.
            /// </summary>
            /// <param name="start"></param>
            /// <param name="end"></param>
            /// <param name="stepSize"></param>
            /// <returns></returns>
            public async Task<PrometheusResponse<PrometheusMatrixResult>> GetCpuUsageAsync(DateTime start, DateTime end, string stepSize = "15s")
            {
                await SyncContext.Clear;

                var query = $@"(avg(irate(node_cpu_seconds_total{{mode = ""idle"", cluster=~""{NeonDashboardService.ClusterInfo.Name}""}}[5m])))";
                CPUUsagePercent = await QueryRangeAsync(query, start, end, stepSize);

                NotifyStateChanged();

                return CPUUsagePercent;
            }

            /// <summary>
            /// Gets the total number of CPUs available to the cluster.
            /// </summary>
            /// <returns></returns>
            public async Task<decimal> GetCpuTotalAsync()
            {
                await SyncContext.Clear;

                var query = $@"sum(count without(cpu, mode) (node_cpu_seconds_total{{mode = ""idle"", cluster=~""{NeonDashboardService.ClusterInfo.Name}""}}))";
                CPUTotal = decimal.Parse((await QueryAsync(query)).Data.Result.First().Value.Value);

                NotifyStateChanged();

                return CPUTotal;
            }

            /// <summary>
            /// Gets the total disk usage for the cluster.
            /// </summary>
            /// <param name="start"></param>
            /// <param name="end"></param>
            /// <param name="stepSize"></param>
            /// <returns></returns>
            public async Task<PrometheusResponse<PrometheusMatrixResult>> GetDiskUsageAsync(DateTime start, DateTime end, string stepSize = "15s")
            {
                await SyncContext.Clear;

                var query = $@"sum(node_filesystem_size_bytes{{cluster=~""{NeonDashboardService.ClusterInfo.Name}"", mountpoint=""/"",fstype!=""rootfs""}}) - sum(node_filesystem_avail_bytes{{cluster=~""{NeonDashboardService.ClusterInfo.Name}"", mountpoint=""/"",fstype!=""rootfs""}})";
                DiskUsageBytes = await QueryRangeAsync(query, start, end, stepSize);

                NotifyStateChanged();

                return DiskUsageBytes;
            }

            /// <summary>
            /// Gets the total amount of disk space available to the cluster.
            /// </summary>
            /// <returns></returns>
            public async Task<decimal> GetDiskTotalAsync()
            {
                await SyncContext.Clear;

                var query = $@"sum(node_filesystem_avail_bytes{{cluster=~""{NeonDashboardService.ClusterInfo.Name}"", mountpoint=""/"",fstype!=""rootfs""}})";
                DiskTotalBytes = decimal.Parse((await QueryAsync(query)).Data.Result.First().Value.Value);

                NotifyStateChanged();

                return DiskTotalBytes;
            }

            /// <summary>
            /// Executes a range query.
            /// </summary>
            /// <param name="query">The query to be executed</param>
            /// <param name="start">The start time.</param>
            /// <param name="end">The end time.</param>
            /// <param name="stepSize">The optional step size</param>
            /// <param name="cacheInterval">The cache interval</param>
            /// <returns></returns>
            public async Task<PrometheusResponse<PrometheusMatrixResult>> QueryRangeAsync(string query, DateTime start, DateTime end, string stepSize = "15s", int cacheInterval = 1)
            {
                await SyncContext.Clear;

                // round intervals so that they cache better.
                start = start.RoundDown(TimeSpan.FromMinutes(cacheInterval));
                end   = end.RoundDown(TimeSpan.FromMinutes(cacheInterval));

                Logger.LogDebug($"[Metrics] Executing range query. Query: [{query}], Start [{start}], End: [{end}], StepSize: [{stepSize}], CacheInterval: [{cacheInterval}]");

                var key = $"neon-dashboard_{Neon.Cryptography.CryptoHelper.ComputeMD5String(query)}";

                try
                {
                    var value = await Cache.GetAsync<PrometheusResponse<PrometheusMatrixResult>>(key);
                    if (value != null)
                    {
                        Logger.LogDebug($"[Metrics] Returning from Cache. Query: [{query}], Start [{start}], End: [{end}], StepSize: [{stepSize}], CacheInterval: [{cacheInterval}]");

                        return value;
                    }
                }
                catch (Exception e)
                {
                    Logger.LogError(e);
                }

                try
                {
                    var result = await PrometheusClient.QueryRangeAsync(query, start, end, stepSize);

                    _ = Cache.SetAsync(key, result);

                    return result;
                }
                catch (Exception e)
                {
                    Logger.LogError(e);
                    throw;
                }
            }

            private async Task<PrometheusResponse<PrometheusVectorResult>> QueryAsync(string query)
            {
                await SyncContext.Clear;

                Logger.LogDebug($"[Metrics] Executing query. Query: [{query}]");

                var key = Neon.Cryptography.CryptoHelper.ComputeMD5String(query);

                try
                {
                    var value = await Cache.GetAsync<PrometheusResponse<PrometheusVectorResult>>(key);
                    if (value != null)
                    {
                        Logger.LogDebug($"[Metrics] Returning from Cache. Query: [{query}]");

                        return value;
                    }
                }
                catch (Exception e)
                {
                    Logger.LogError(e);
                }

                try
                {
                    var result = await PrometheusClient.QueryAsync(query);

                    _ = Cache.SetAsync(key, result);

                    return result;
                }
                catch (Exception e)
                {
                    Logger.LogError(e);
                    throw;
                }
            }

            /// <summary>
            /// Converts unix timestamp to <see cref="DateTime"/>.
            /// </summary>
            /// <param name="unixTimeStamp"></param>
            /// <returns></returns>
            public DateTime UnixTimeStampToDateTime(double unixTimeStamp)
            {
                // Unix timestamp is seconds past epoch
                DateTime dateTime = new DateTime(1970, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc);
                dateTime = dateTime.AddSeconds(unixTimeStamp).ToLocalTime();
                return dateTime;
            }
        }
    }
}