﻿//-----------------------------------------------------------------------------
// FILE:	    Startup.cs
// CONTRIBUTOR: Marcus Bowyer, Jeff Lill
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
using System.Net.Http;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

using Neon.Kube;
using Neon.Kube.Operator;

using k8s;
using KubeOps.Operator;

namespace NeonClusterOperator
{
    /// <summary>
    /// Configures the operator's service controllers.
    /// </summary>
    public class Startup
    {
        /// <summary>
        /// Configures depdendency injection.
        /// </summary>
        /// <param name="services">The service collection.</param>
        public void ConfigureServices(IServiceCollection services)
        {
            // $hack(jefflill):
            //
            // This will be called when CRDs and other assets are being generated by KubeOps
            // and also when the service is actually being started.  We need to load the
            // [WATCHER_MAX_RETRY_INTERVAL] environment variable.  Unfortunately, [Program.Service]
            // won't be set when generating CRDs, so we'll just set a default value.

            var watcherTimeout = TimeSpan.FromMinutes(2);
            var watcherRetry   = TimeSpan.FromSeconds(15);

            if (Program.Service != null)
            {
                watcherTimeout = Program.Service.Environment.Get("WATCHER_TIMEOUT_INTERVAL", watcherTimeout);
                watcherRetry   = Program.Service.Environment.Get("WATCHER_MAX_RETRY_INTERVAL", watcherRetry);
            }

            var watcherTimeoutSeconds = Math.Max(1, Math.Max(ushort.MaxValue, (int)Math.Ceiling(watcherTimeout.TotalSeconds)));
            var watcherRetrySeconds   = Math.Max(1, (int)Math.Ceiling(watcherTimeout.TotalSeconds));

            var _services = services;

            if (!OperatorHelper.GeneratingCRDs)
            {
                _services = _services.AddSingleton<IKubernetes>(new KubernetesWithRetry(KubernetesClientConfiguration.BuildDefaultConfig()));
            }

            var operatorBuilder = _services
                .AddKubernetesOperator(
                    settings =>
                    {
                        settings.EnableAssemblyScanning = true;
                        settings.EnableLeaderElection   = false;    // ResourceManager is managing leader election
                        settings.WatcherHttpTimeout     = (ushort)watcherTimeoutSeconds;
                        settings.WatcherMaxRetrySeconds = watcherRetrySeconds;
                    });

            Program.AddResourceAssemblies(operatorBuilder);
        }

        /// <summary>
        /// Configures the operator service controllers.
        /// </summary>
        /// <param name="app">Specifies the application builder.</param>
        public void Configure(IApplicationBuilder app)
        {
            app.UseKubernetesOperator();
        }
    }
}
