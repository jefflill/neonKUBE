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

using Microsoft.AspNetCore;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;

using Neon.Service;
using Neon.Common;
using Neon.Kube;

using Prometheus;
using Prometheus.DotNetRuntime;

namespace NeonSsoSessionProxy
{
    /// <summary>
    /// Implements the <b>neon-sso-session-proxy</b> service.
    /// </summary>
    public class Service : NeonService
    {
        // class fields
        private IWebHost webHost;

        /// <summary>
        /// Session cookie name.
        /// </summary>
        public const string SessionCookieName = ".NeonKUBE.SsoProxy.Session.Cookie";

        /// <summary>
        /// The Dex configuration.
        /// </summary>
        public DexConfig Config { get; private set; }

        /// <summary>
        /// Constructor.
        /// </summary>
        /// <param name="name">The service name.</param>
        public Service(string name)
             : base(name, version: KubeVersions.NeonKube, metricsPrefix: "neonssosessionproxy")
        {
            
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

            var configFile = GetConfigFilePath("/etc/neonkube/neon-sso-session-proxy/config.yaml");
            using (StreamReader reader = new StreamReader(new FileStream(configFile, FileMode.Open, FileAccess.Read)))
            {
                Config = NeonHelper.YamlDeserializeViaJson<DexConfig>(await reader.ReadToEndAsync());
            }

            // Start the web service.

            webHost = new WebHostBuilder()
                .ConfigureAppConfiguration(
                    (hostingcontext, config) =>
                    {
                        config.Sources.Clear();
                    })
                .UseStartup<Startup>()
                .UseKestrel(options => options.Listen(IPAddress.Any, 80))
                .ConfigureServices(services => services.AddSingleton(typeof(Service), this))
                .UseStaticWebAssets()
                .Build();

            _ = webHost.RunAsync();

            Log.LogInfo($"Listening on {IPAddress.Any}:80");

            // Indicate that the service is ready for business.

            await SetStatusAsync(NeonServiceStatus.Running);

            // Handle termination gracefully.

            await Terminator.StopEvent.WaitAsync();
            Terminator.ReadyToExit();

            return 0;
        }
    }
}