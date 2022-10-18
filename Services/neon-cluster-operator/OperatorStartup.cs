﻿//-----------------------------------------------------------------------------
// FILE:	    OperatorStartup.cs
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
using System.Net.Http;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

using Neon.Diagnostics;
using Neon.Kube;
using Neon.Kube.Operator;

using k8s;

using KubeOps.Operator;

using OpenTelemetry;
using OpenTelemetry.Trace;
using OpenTelemetry.Resources;

using OpenTelemetry.Instrumentation;
using Quartz.Logging;
using NeonClusterOperator.Webhooks;
using k8s.Models;
using Microsoft.OpenApi.Writers;
using Microsoft.Extensions.Logging;
using System.Reflection;
using System.Linq;
using Microsoft.Extensions.Configuration;
using Neon.Common;

namespace NeonClusterOperator
{
    /// <summary>
    /// Configures the operator's service controllers.
    /// </summary>
    public class OperatorStartup
    {
        /// <summary>
        /// The <see cref="IConfiguration"/>.
        /// </summary>
        public IConfiguration Configuration { get; }

        /// <summary>
        /// The <see cref="Service"/>.
        /// </summary>
        public Service Service;

        /// <summary>
        /// Constructor.
        /// </summary>
        /// <param name="configuration">Specifies the service configuration.</param>
        /// <param name="service">Specifies the service.</param>
        public OperatorStartup(IConfiguration configuration, Service service)
        {
            this.Configuration = configuration;
            this.Service = service;
        }

        /// <summary>
        /// Configures depdendency injection.
        /// </summary>
        /// <param name="services">The service collection.</param>
        public void ConfigureServices(IServiceCollection services)
        {
            services.AddSingleton<ILogger>(Program.Service.Logger)
                .AddSingleton(Service.K8s)
                .AddScoped<PodWebhook>()
                .AddRouting();
        }

        /// <summary>
        /// Configures the operator service controllers.
        /// </summary>
        /// <param name="app">Specifies the application builder.</param>
        public void Configure(IApplicationBuilder app)
        {
            if (NeonHelper.IsDevWorkstation)
            {
                app.UseDeveloperExceptionPage();
            }

            app.UseRouting();
            app.UseEndpoints(
            endpoints =>
            {
                var k8s = (IKubernetes)app.ApplicationServices.GetRequiredService(typeof(IKubernetes));

                var scope = app.ApplicationServices.CreateScope();

                var mutatorType = typeof(PodWebhook);
                var entityType = typeof(V1Pod);

                var mutator = (PodWebhook)scope.ServiceProvider.GetRequiredService(mutatorType);

                var registerMethod = typeof(IAdmissionWebhook<,>)
                    .MakeGenericType(entityType, typeof(MutationResult))
                    .GetMethods(BindingFlags.Instance | BindingFlags.NonPublic)
                    .First(m => m.Name == "Register");

                registerMethod.Invoke(mutator, new object[] { endpoints });

                var createMethod = typeof(IMutationWebhook<>)
                    .MakeGenericType(entityType)
                    .GetMethods(BindingFlags.Instance | BindingFlags.NonPublic)
                    .First(m => m.Name == "Create");

                createMethod.Invoke(mutator, new object[] { k8s });
            });
            
        }
    }
}
