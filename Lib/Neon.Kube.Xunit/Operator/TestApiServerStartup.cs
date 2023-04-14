﻿// FILE:	    TestApiServerStartup.cs
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
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

using k8s;
using k8s.KubeConfigModels;
using System.Text.Json.Serialization;
using System.Diagnostics.Contracts;
using Microsoft.AspNetCore.Mvc.Formatters;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using System.Linq;

namespace Neon.Kube.Xunit.Operator
{
    /// <summary>
    /// Startup class for the test API server.
    /// </summary>
    public class TestApiServerStartup
    {
        /// <summary>
        /// Configures depdendency injection.
        /// </summary>
        /// <param name="services">The service collection.</param>
        public void ConfigureServices(IServiceCollection services)
        {
            Covenant.Requires<ArgumentNullException>(services != null, nameof(services));

            services.AddControllers(options =>
            {
                options.InputFormatters.Insert(0, JPIF.GetJsonPatchInputFormatter());
            })
                .AddJsonOptions(
                    options => 
                    {
                        options.JsonSerializerOptions.DictionaryKeyPolicy  = JsonNamingPolicy.CamelCase;
                        options.JsonSerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
                    });

            services.AddSingleton<ITestApiServer, TestApiServer>();

            var serializeOptions = new JsonSerializerOptions()
            {
                PropertyNamingPolicy        = JsonNamingPolicy.CamelCase,
                IncludeFields               = true,
                PropertyNameCaseInsensitive = true,
            };

            serializeOptions.Converters.Add(new JsonStringEnumMemberConverter());

            services.AddSingleton(serializeOptions);
        }

        /// <summary>
        /// Configures operator service controllers.
        /// </summary>
        /// <param name="app">Specifies the application builder.</param>
        /// <param name="apiServer">Specifies the test API server.</param>
        public void Configure(IApplicationBuilder app, ITestApiServer apiServer)
        {
            Covenant.Requires<ArgumentNullException>(app != null, nameof(app));
            Covenant.Requires<ArgumentNullException>(apiServer != null, nameof(apiServer));

            app.Use(next => async context =>
            {
                // This is a no-op, but very convenient for setting a breakpoint to see per-request details.
                await next(context);
            });

            app.UseRouting();
            app.UseEndpoints(endpoints => endpoints.MapControllers());
            app.Run(apiServer.UnhandledRequest);
        }
    }
    public static class JPIF
    {
        public static NewtonsoftJsonPatchInputFormatter GetJsonPatchInputFormatter()
        {
            var builder = new ServiceCollection()
            .AddLogging()
            .AddMvc()
            .AddNewtonsoftJson()
            .Services.BuildServiceProvider();

            return builder
                .GetRequiredService<IOptions<MvcOptions>>()
                .Value
                .InputFormatters
                .OfType<NewtonsoftJsonPatchInputFormatter>()
                .First();
        }
    }
}
