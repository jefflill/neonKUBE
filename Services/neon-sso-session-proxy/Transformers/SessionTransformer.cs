//-----------------------------------------------------------------------------
// FILE:        SessionTransformer.cs
// CONTRIBUTOR: Marcus Bowyer
// COPYRIGHT:   Copyright © 2005-2023 by NEONFORGE LLC.  All rights reserved.
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

using System.Text;
using System.Web;

using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.StackExchangeRedis;

using Neon.Common;
using Neon.Cryptography;
using Neon.Diagnostics;

using Yarp.ReverseProxy.Forwarder;
using Yarp.ReverseProxy.Transforms;

namespace NeonSsoSessionProxy
{
    /// <summary>
    /// 
    /// </summary>
    public class SessionTransformer : HttpTransformer
    {
        private IDistributedCache            cache;
        private AesCipher                    cipher;
        private DexClient                    dexClient;
        private string                       dexHost;
        private ILogger                      logger;
        private DistributedCacheEntryOptions cacheOptions;

        /// <summary>
        /// Constructor.
        /// </summary>
        /// <param name="cache"></param>
        /// <param name="aesCipher"></param>
        /// <param name="dexClient"></param>
        /// <param name="logger"></param>
        /// <param name="cacheOptions"></param>
        public SessionTransformer(
            IDistributedCache               cache,
            AesCipher                       aesCipher,
            DexClient                       dexClient,
            ILogger                         logger,
            DistributedCacheEntryOptions    cacheOptions)
        { 
            this.cache        = cache;
            this.cipher       = aesCipher;
            this.dexClient    = dexClient;
            this.dexHost      = dexClient.BaseAddress.Host;
            this.logger       = logger;
            this.cacheOptions = cacheOptions;
        }

        /// <summary>
        /// Transforms the request before sending it upstream.
        /// </summary>
        /// <param name="httpContext">Specifies the request context.</param>
        /// <param name="proxyRequest">Specifies the proxy request.</param>
        /// <param name="destinationPrefix">Specifies the desitnation prefix.</param>
        /// <param name="cancellationToken"></param>
        /// <returns>Returns the tracking <see cref="ValueTask"/>.</returns>
        public override async ValueTask TransformRequestAsync(
            HttpContext httpContext,
            HttpRequestMessage proxyRequest,
            string destinationPrefix,
            CancellationToken cancellationToken = default)
        {
            logger.LogDebugEx(() => $"Transform request");

            await base.TransformRequestAsync(httpContext, proxyRequest, destinationPrefix, cancellationToken);
        }

        /// <summary>
        /// <para>
        /// Transforms the response before returning it to the client. 
        /// </para>
        /// <para>
        /// This method will add a <see cref="Cookie"/> to each response containing relevant information
        /// about the current authentication flow. It also intercepts redirects from Dex and saves any relevant
        /// tokens to a cache for reuse.
        /// </para>
        /// </summary>
        /// <param name="httpContext">Specifies the request context.</param>
        /// <param name="proxyResponse">Specifies the proxy response.</param>
        /// <param name="cancellationToken"></param>
        /// <returns>
        /// A bool indicating if the response should be proxied to the client or not. A derived
        ///  implementation that returns false may send an alternate response inline or return
        ///  control to the caller for it to retry, respond, etc.
        /// </returns>
        public override async ValueTask<bool> TransformResponseAsync(
            HttpContext httpContext,
            HttpResponseMessage proxyResponse,
            CancellationToken cancellationToken = default)
        {
            logger.LogDebugEx(() => $"Transform response");

            await base.TransformResponseAsync(httpContext, proxyResponse, cancellationToken);

            Cookie cookie = null;

            if (httpContext.Request.Cookies.TryGetValue(Service.SessionCookieName, out var requestCookieBase64))
            {
                try
                {
                    logger.LogDebugEx(() => $"Decrypting existing cookie.");

                    cookie = NeonHelper.JsonDeserialize<Cookie>(cipher.DecryptBytesFrom(requestCookieBase64));
                }
                catch (Exception e)
                {
                    logger.LogErrorEx(e);

                    cookie = new Cookie();
                }
            }
            else
            {
                logger.LogDebugEx("Cookie not present.");

                cookie = new Cookie();
            }

            // If we're being redirected, intercept request and save token to cookie.

            if (httpContext.Response.Headers.Location.Count > 0 && Uri.IsWellFormedUriString(httpContext.Response.Headers.Location.Single(), UriKind.Absolute))
            {
                var location = new Uri(httpContext.Response.Headers.Location.Single());
                var code     = HttpUtility.ParseQueryString(location.Query).Get("code");

                logger.LogDebugEx(() => $"Location: [{location}] code: [{code}].");

                if (!string.IsNullOrEmpty(code))
                {
                    logger.LogDebugEx(() => $"Code present.");

                    if (cookie != null)
                    {
                        logger.LogDebugEx(() => $"cookie.ClientId: [{cookie.ClientId}].");

                        var redirect = cookie.RedirectUri;
                        var token    = await dexClient.GetTokenAsync(cookie.ClientId, code, redirect, "authorization_code", cookie.CodeVerifier);

                        logger.LogDebugEx(() => $"Redirect: [{redirect}] token: [{token}].");

                        await cache.SetAsync(code, cipher.EncryptToBytes(NeonHelper.JsonSerializeToBytes(token)), cacheOptions);
                        logger.LogDebugEx(() => NeonHelper.JsonSerialize(token));

                        cookie.TokenResponse = token;

                        httpContext.Response.Cookies.Append(
                            Service.SessionCookieName,
                            cipher.EncryptToBase64(NeonHelper.JsonSerialize(cookie)),
                            new CookieOptions()
                            {
                                Path     = "/",
                                Expires  = DateTime.UtcNow.AddSeconds(token.ExpiresIn.Value).AddMinutes(-60),
                                Secure   = true,
                                SameSite = SameSiteMode.Strict
                            });

                        return true;
                    }
                }
            }

            // Add query parameters to the cookie.

            if (httpContext.Request.Query.TryGetValue("client_id", out var clientId))
            {
                logger.LogDebugEx(() => $"Client ID: [{clientId}]");

                cookie.ClientId = clientId;
            }

            if (httpContext.Request.Query.TryGetValue("state", out var state))
            {
                logger.LogDebugEx(() => $"State: [{state}]");

                cookie.State = state;
            }

            if (httpContext.Request.Query.TryGetValue("redirect_uri", out var redirectUri))
            {
                logger.LogDebugEx(() => $"Redirect Uri: [{redirectUri}]");

                cookie.RedirectUri = redirectUri;
            }

            if (httpContext.Request.Query.TryGetValue("scope", out var scope))
            {
                logger.LogDebugEx(() => $"Scope: [{scope}]");
                cookie.Scope = scope;
            }

            if (httpContext.Request.Query.TryGetValue("response_type", out var responseType))
            {
                logger.LogDebugEx(() => $"Response Type: [{responseType}]");

                cookie.ResponseType = responseType;
            }

            if (httpContext.Request.Query.TryGetValue("code_challenge", out var codeChallenge))
            {
                logger.LogDebugEx(() => $"Code Challenge: [{codeChallenge}]");

                cookie.CodeChallenge = codeChallenge;
            }

            if (httpContext.Request.Query.TryGetValue("code_challenge_method", out var codeChallengeMethod))
            {
                logger.LogDebugEx(() => $"Code Challenge Method: [{codeChallengeMethod}]");

                cookie.CodeChallengeMethod = codeChallengeMethod;
            }

            if (httpContext.Request.Query.TryGetValue("code_verifier", out var codeVerifier))
            {
                logger.LogDebugEx(() => $"Code verifier: [{codeVerifier}]");

                cookie.CodeVerifier = codeVerifier;
            }

            httpContext.Response.Cookies.Append(
                Service.SessionCookieName,
                cipher.EncryptToBase64(NeonHelper.JsonSerialize(cookie)),
                new CookieOptions()
                {
                    Path     = "/",
                    Expires  = DateTime.UtcNow.AddHours(4),
                    Secure   = true,
                    SameSite = SameSiteMode.Strict
                });

            return true;
        }
    }
}
