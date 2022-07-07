﻿//-----------------------------------------------------------------------------
// FILE:	    HttpConnection.cs
// CONTRIBUTOR: Jeff Lill
// COPYRIGHT:	Copyright (c) 2005-2022 by neonFORGE LLC.  All rights reserved.
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
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Neon.Tasks;

namespace Microsoft.Net.Http.Client
{
    internal class HttpConnection : IDisposable
    {
        //---------------------------------------------------------------------
        // Static members

        private static readonly char[] spaceArray = new char[] { ' ' };

        //---------------------------------------------------------------------
        // Instance members

        public HttpConnection(BufferedReadStream transport)
        {
            Transport = transport;
        }

        public BufferedReadStream Transport { get; private set; }

        public async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            await SyncContext.Clear;

            try
            {
                // Serialize headers & send
                string rawRequest = SerializeRequest(request);
                byte[] requestBytes = Encoding.ASCII.GetBytes(rawRequest);
                await Transport.WriteAsync(requestBytes, 0, requestBytes.Length, cancellationToken);

                if (request.Content != null)
                {
                    if (request.Content.Headers.ContentLength.HasValue)
                    {
                        await request.Content.CopyToAsync(Transport);
                    }
                    else
                    {
                        // The length of the data is unknown. Send it in chunked mode.
                        using (var chunkedStream = new ChunkedWriteStream(Transport))
                        {
                            await request.Content.CopyToAsync(chunkedStream);
                            await chunkedStream.EndContentAsync(cancellationToken);
                        }
                    }
                }

                // Receive headers
                List<string> responseLines = await ReadResponseLinesAsync(cancellationToken);
                // Determine response type (Chunked, Content-Length, opaque, none...)
                // Receive body
                return CreateResponseMessage(responseLines);
            }
            catch (Exception e)
            {
                Dispose(); // Any errors at this layer abort the connection.
                throw new HttpRequestException("The requested failed, see inner exception for details.", e);
            }
        }

        private string SerializeRequest(HttpRequestMessage request)
        {
            StringBuilder builder = new StringBuilder();
            builder.Append(request.Method);
            builder.Append(' ');
            builder.Append(request.GetAddressLineProperty());
            builder.Append(" HTTP/");

            if (request.GetHostProperty() == "unix.sock")
            {
                // $hack(jefflill): Hardcoded to HTTP/1.0 for unix sockets

                builder.Append("1.1");
            }
            else
            {
                builder.Append(request.Version.ToString(2));
            }

            builder.Append("\r\n");
            builder.Append(request.Headers.ToString());

            if (request.Content != null)
            {
                // Force the content to compute its content length if it has not already.
                var contentLength = request.Content.Headers.ContentLength;
                if (contentLength.HasValue)
                {
                    request.Content.Headers.ContentLength = contentLength.Value;
                }

                builder.Append(request.Content.Headers.ToString());
                if (!contentLength.HasValue)
                {
                    // Add header for chunked mode.
                    builder.Append("Transfer-Encoding: chunked\r\n");
                }
            }
            // Headers end with an empty line
            builder.Append("\r\n");
            return builder.ToString();
        }

        private async Task<List<string>> ReadResponseLinesAsync(CancellationToken cancellationToken)
        {
            var lines = new List<string>();
            var line  = await Transport.ReadLineAsync(cancellationToken);

            while (line.Length > 0)
            {
                lines.Add(line);
                line = await Transport.ReadLineAsync(cancellationToken);
            }

            return lines;
        }

        private HttpResponseMessage CreateResponseMessage(List<string> responseLines)
        {
            var responseLine      = responseLines.First();
            var responseLineParts = responseLine.Split(spaceArray, 3);
            if (responseLineParts.Length < 2)
            {
                throw new HttpRequestException("Invalid response line: " + responseLine);
            }

            var statusCode = 0;
            if (int.TryParse(responseLineParts[1], NumberStyles.None, CultureInfo.InvariantCulture, out statusCode))
            {
                // TODO: Validate range
            }
            else
            {
                throw new HttpRequestException("Invalid status code: " + responseLineParts[1]);
            }

            var response = new HttpResponseMessage((HttpStatusCode)statusCode);

            if (responseLineParts.Length >= 3)
            {
                response.ReasonPhrase = responseLineParts[2];
            }

            var content = new HttpConnectionResponseContent(this);

            response.Content = content;

            foreach (var rawHeader in responseLines.Skip(1))
            {
                var colonOffset = rawHeader.IndexOf(':');

                if (colonOffset <= 0)
                {
                    throw new HttpRequestException("The given header line format is invalid: " + rawHeader);
                }

                var headerName  = rawHeader.Substring(0, colonOffset);
                var headerValue = rawHeader.Substring(colonOffset + 2);

                if (!response.Headers.TryAddWithoutValidation(headerName, headerValue))
                {
                    bool success = response.Content.Headers.TryAddWithoutValidation(headerName, headerValue);
                    System.Diagnostics.Debug.Assert(success, "Failed to add response header: " + rawHeader);
                }
            }

            // After headers have been set
            content.ResolveResponseStream(chunked: response.Headers.TransferEncodingChunked.HasValue && response.Headers.TransferEncodingChunked.Value);

            return response;
        }

        public void Dispose()
        {
            Dispose(true);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (disposing)
            {
                Transport.Dispose();
            }
        }
    }
}
