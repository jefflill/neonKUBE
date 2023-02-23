﻿//-----------------------------------------------------------------------------
// FILE:	    PortListener.cs
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
using System.Net;
using System.Net.Sockets;
using System.Threading;

using Microsoft.Extensions.Logging;

namespace Neon.Kube
{
    internal class PortListener : IPortListener
    {
        private readonly ILogger<PortListener> logger;
        private int localPort;
        private CancellationTokenRegistration ctr = default(CancellationTokenRegistration);
        private bool disposed = false;

        public TcpListener Listener { get; private set; }

        public PortListener(
            int               localPort, 
            ILoggerFactory    loggerFactory,
            CancellationToken cancellationToken)
        {
            this.localPort = localPort;
            this.logger = loggerFactory?.CreateLogger<PortListener>();

            Listener = new TcpListener(IPAddress.Loopback, localPort);
            logger?.LogDebug($"PortListener created on {localPort}");

            ctr = cancellationToken.Register(() => this.Dispose());

            Listener.Start(512);
            logger?.LogDebug($"PortListener started on {localPort}");
        }

        public void Dispose()
        {
            if (disposed)
            {
                return;
            }
            disposed = true;
            logger?.LogDebug($"PortListener stopped on {localPort}");
            Listener.Stop();
            ctr.Dispose();
        }
    }
}
