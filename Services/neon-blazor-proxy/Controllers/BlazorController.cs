//-----------------------------------------------------------------------------
// FILE:	    BlazorController.cs
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

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using System.Web;

using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Distributed;

using Neon.Common;
using Neon.Cryptography;
using Neon.Service;
using Neon.Tasks;
using Neon.Web;

using DnsClient;

using Newtonsoft.Json;

using Yarp;
using Yarp.ReverseProxy;
using Yarp.ReverseProxy.Forwarder;

namespace NeonBlazorProxy.Controllers
{
    /// <summary>
    /// Implements Blazor proxy service methods.
    /// </summary>
    [ApiController]
    public class BlazorController : NeonControllerBase
    {
        /// <summary>
        /// Lock used for updating the load balancer status.
        /// </summary>
        public static readonly object server_lock = new object();
        
        /// <summary>
        /// The host name of the last server to be sent a request.
        /// </summary>
        public static string LastServer { get; private set; }

        private static ForwarderRequestConfig forwarderRequestConfig;

        private Service            blazorProxyService;
        private ProxyConfig        config;
        private HttpMessageInvoker httpClient;
        private IHttpForwarder     forwarder;
        private SessionTransformer transformer;
        private IDistributedCache  cache;
        private AesCipher          cipher;
        private LookupClient       dnsClient;

        /// <summary>
        /// Static Constructor.
        /// </summary>
        static BlazorController()
        {
            forwarderRequestConfig = new ForwarderRequestConfig()
            {
                ActivityTimeout = TimeSpan.FromSeconds(100)
            };
        }

        /// <summary>
        /// Constructor.
        /// </summary>
        /// <param name="blazorProxyService">The <see cref="Service"/></param>
        /// <param name="config">The <see cref="ProxyConfig"/></param>
        /// <param name="httpClient">HttpClient for forwarding requests.</param>
        /// <param name="forwarder">The YARP forwarder.</param>
        /// <param name="cache">The cache used for storing session information.</param>
        /// <param name="aesCipher">The <see cref="AesCipher"/> used for cookie encryption.</param>
        /// <param name="dnsClient">The <see cref="LookupClient"/> for service discovery.</param>
        /// <param name="sessionTransformer">The <see cref="SessionTransformer"/>.</param>
        public BlazorController(
            Service                      blazorProxyService,
            ProxyConfig                  config,
            HttpMessageInvoker           httpClient,
            IHttpForwarder               forwarder,
            IDistributedCache            cache,
            AesCipher                    aesCipher,
            LookupClient                 dnsClient,
            SessionTransformer           sessionTransformer)
        {
            this.blazorProxyService = blazorProxyService;
            this.config             = config;
            this.httpClient         = httpClient;
            this.forwarder          = forwarder;
            this.cache              = cache;
            this.cipher             = aesCipher;
            this.transformer        = sessionTransformer;
            this.dnsClient          = dnsClient;
        }

        /// <summary>
        /// <para>
        /// A catch-all method used to proxy all non-websocket requests to the Blazor backend. It inspects the backend DNS entry for SRV records on the service
        /// port, and load balances requests using round-robin to available backends.
        /// </para>
        /// <note>No health checking is performed. It is assumed that backends listed in DNS are available.</note>
        /// </summary>
        /// <returns></returns>
        [Route("{**catchAll}")]
        public async Task CatchAllAsync()
        {
            await SyncContext.Clear;

            var host = await GetHostAsync();

            var error = await forwarder.SendAsync(HttpContext, $"{config.Backend.Scheme}://{host}:{config.Backend.Port}", httpClient, forwarderRequestConfig, transformer);

            if (error != ForwarderError.None)
            {
                var errorFeature = HttpContext.GetForwarderErrorFeature();
                var exception    = errorFeature.Exception;

                LogError("CatchAll", exception);
            }
        }

        /// <summary>
        /// <para>
        /// Proxies the Blazor websocket request to the correct Blazor backend server. This is implemented by inspecting the Session Cookie which contains a 
        /// reference to the Session ID. The Session Backend is retreived from the <see cref="cache"/> using the Session ID as the key. Once the correct 
        /// Blazor backend server is identified, the request is proxied upstream using the <see cref="forwarder"/>.
        /// </para>
        /// </summary>
        /// <returns></returns>
        [Route("/_blazor")]
        [Route("/_blazor/{**catchAll}")]
        public async Task BlazorAsync()
        {
            await SyncContext.Clear;

            var cookie    = HttpContext.Request.Cookies.Where(c => c.Key == Service.SessionCookieName).First();
            var sessionId = cipher.DecryptStringFrom(cookie.Value);
            var session   = NeonHelper.JsonDeserialize<Session>(await cache.GetAsync(sessionId));

            session.ConnectionId = HttpContext.Connection.Id;

            await cache.SetAsync(session.Id, NeonHelper.JsonSerializeToBytes(session));
            
            WebsocketMetrics.CurrentConnections.Inc();
            WebsocketMetrics.ConnectionsEstablished.Inc();
            blazorProxyService.CurrentConnections.Add(session.ConnectionId);

            LogDebug($"Fwd [{session.Id}] to [{session.UpstreamHost}].");

            var error = await forwarder.SendAsync(HttpContext, $"{config.Backend.Scheme}://{session.UpstreamHost}", httpClient, forwarderRequestConfig, transformer);

            LogDebug($"Session [{session.Id}] closed.");

            if (error != ForwarderError.None)
            {
                var errorFeature = HttpContext.GetForwarderErrorFeature();
                var exception = errorFeature.Exception;

                if (exception.GetType() != typeof(TaskCanceledException)
                    && exception.GetType() != typeof(OperationCanceledException))
                {
                    LogError("_blazor", exception);
                }
            }
        }

        /// <summary>
        /// Gets the next server using round-robin load balancing over Blazor backends.
        /// </summary>
        /// <returns></returns>
        private async Task<string> GetHostAsync()
        {
            await SyncContext.Clear;

            var host = config.Backend.Host;

            var dns = await dnsClient.QueryAsync(config.Backend.Host, QueryType.SRV);

            if (dns.HasError 
                || dns.Answers.IsEmpty())
            {
                return host;
            }

            var srv = dns.Answers.SrvRecords().Where(r => r.Port == config.Backend.Port).ToList();

            lock (server_lock)
            {
                DnsMetrics.DnsLookupsRequested += 1;

                var index = srv.FindIndex(r => r.Target.Value == LastServer) + 1;
                if (index >= srv.Count()
                    || index < 0)
                {
                    index = 0;
                }

                LastServer = srv.ElementAt(index).Target.Value;
                host = LastServer.Trim('.');
            }

            return host;
        }
    }
}