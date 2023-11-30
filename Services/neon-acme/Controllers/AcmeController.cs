//-----------------------------------------------------------------------------
// FILE:        AcmeController.cs
// CONTRIBUTOR: Marcus Bowyer
// COPYRIGHT:   Copyright � 2005-2023 by NEONFORGE LLC.  All rights reserved.
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
using Microsoft.Extensions.Logging;

using Neon.Common;
using Neon.Cryptography;
using Neon.Diagnostics;
using Neon.Service;
using Neon.Kube;
using Neon.Kube.Resources.CertManager;
using Neon.Tasks;
using Neon.Net;
using Neon.Web;

using k8s;
using k8s.Models;
using Neon.Kube.Resources;
using Neon.Collections;

namespace NeonAcme.Controllers
{
    /// <summary>
    /// Implements the neon-acme service.
    /// </summary>
    [ApiController]
    [Route("apis/acme.neoncloud.io/v1alpha1")]
    public class AcmeController : NeonControllerBase
    {
        private Service             service;
        private JsonClient          headendClient => service.HeadendClient;
        private IKubernetes         k8s           => service.Kubernetes;

        /// <summary>
        /// Constructor
        /// </summary>
        /// <param name="service">The parent service.</param>
        public AcmeController(
            Service    service)
        {
            this.service    = service;
        }

        /// <summary>
        /// <para>
        /// This method is used by Kubernetes for service discovery.
        /// </para>
        /// </summary>
        /// <returns>The <see cref="V1APIResourceList"/> detailing the resources available.</returns>
        [HttpGet("")]
        [Produces("application/json")]
        public async Task<ActionResult> DiscoveryAsync()
        {
            await SyncContext.Clear;

            Logger.LogDebugEx(() => $"Headers: {NeonHelper.JsonSerialize(HttpContext.Request.Headers)}");

            return new JsonResult(service.Resources);
        }

        /// <summary>
        /// Handles challenge presentations from CertManager.
        /// </summary>
        /// <param name="challenge">Specifies the challenge.</param>
        /// <returns>The challenge response.</returns>
        [HttpPost("neoncluster_io")]
        [Produces("application/json")]
        public async Task<ActionResult> PresentNeonClusterChallengeAsync([FromBody] ChallengePayload challenge)
        {
            Logger.LogInformationEx(() => $"Challenge request [{challenge.Request.Action}] [{challenge.Request.DnsName}]");
            Logger.LogDebugEx(() => $"Headers: {NeonHelper.JsonSerialize(HttpContext.Request.Headers)}");
            Logger.LogDebugEx(() => NeonHelper.JsonSerialize(challenge));
            
            var args = new ArgDictionary()
            {
                { "api-version", "2023-04-06" },
            };
            
            var response = await headendClient.PostAsync<ChallengePayload>(uri: "acme/challenge", document: challenge, args: args);

            challenge.Response = response.Response;

            return new JsonResult(challenge);
        }
    }
}
