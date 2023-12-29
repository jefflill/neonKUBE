//-----------------------------------------------------------------------------
// FILE:        IHostingManagerFactory.cs
// CONTRIBUTOR: Jeff Lill
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

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Diagnostics.Contracts;
using System.Dynamic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Neon.Common;
using Neon.Cryptography;
using Neon.IO;
using Neon.Kube.ClusterDef;
using Neon.Kube.Proxy;
using Neon.Net;
using Neon.Time;

namespace Neon.Kube.Hosting
{
    /// <summary>
    /// Describes the implementation for mapping a hosting environment into
    /// a concrete <see cref="IHostingManager"/> implementation.
    /// </summary>
    public interface IHostingManagerFactory
    {
        /// <summary>
        /// Returns the <see cref="HostingManager"/> for an environment that can be used 
        /// for validating the hosting related options.
        /// </summary>
        /// <param name="environment">The target hosting environment.</param>
        /// <returns>
        /// The <see cref="HostingManager"/> or <c>null</c> if no hosting manager
        /// could be located for the specified cluster environment.
        /// </returns>
        HostingManager GetManager(HostingEnvironment environment);

        /// <summary>
        /// Returns the <see cref="HostingManager"/> for provisioning a cluster using
        /// the default node image URI for the cluster environment.
        /// </summary>
        /// <param name="cluster">The cluster being managed.</param>
        /// <param name="cloudMarketplace">
        /// <para>
        /// For cloud environments, this specifies whether the cluster should be provisioned
        /// using a VM image from the public cloud marketplace when <c>true</c> or from the
        /// private NEONFORGE image gallery for testing when <c>false</c>.  This is ignored
        /// for on-premise environments.
        /// </para>
        /// <note>
        /// Only NEONFORGE maintainers will have permission to use the private image.
        /// </note>
        /// </param>
        /// <param name="logFolder">
        /// <para>
        /// The folder where log files are to be written, otherwise or <c>null</c> or 
        /// empty if logging is disabled.
        /// </para>
        /// <note>
        /// Specific hosting managers may choose to ignore this when it doesn't make sense.
        /// </note>
        /// </param>
        /// <returns>
        /// The <see cref="HostingManager"/> or <c>null</c> if no hosting manager
        /// could be located for the specified cluster environment.
        /// </returns>
        /// <exception cref="NeonKubeException">Thrown if the multiple managers implement support for the same hosting environment.</exception>
        HostingManager GetManager(ClusterProxy cluster, bool cloudMarketplace, string logFolder = null);

        /// <summary>
        /// Returns the <see cref="HostingManager"/> for provisioning a cluster using
        /// <b>debug mode</b>.
        /// </summary>
        /// <param name="cluster">The cluster being managed.</param>
        /// <param name="cloudMarketplace">
        /// <para>
        /// For cloud environments, this specifies whether the cluster should be provisioned
        /// using a VM image from the public cloud marketplace when <c>true</c> or from the
        /// private NEONFORGE image gallery for testing when <c>false</c>.  This is ignored
        /// for on-premise environments.
        /// </para>
        /// <note>
        /// Only NEONFORGE maintainers will have permission to use the private image.
        /// </note>
        /// </param>
        /// <param name="logFolder">
        /// <para>
        /// The folder where log files are to be written, otherwise or <c>null</c> or 
        /// empty if logging is disabled.
        /// </para>
        /// <note>
        /// Specific hosting managers may choose to ignore this when it doesn't make sense.
        /// </note>
        /// </param>
        /// <returns>
        /// The <see cref="HostingManager"/> or <c>null</c> if no hosting manager
        /// could be located for the specified cluster environment.
        /// </returns>
        /// <exception cref="NeonKubeException">Thrown if the multiple managers implement support for the same hosting environment.</exception>
        HostingManager GetManagerForDebugMode(ClusterProxy cluster, bool cloudMarketplace, string logFolder = null);

        /// <summary>
        /// Returns the <see cref="HostingManager"/> for provisioning a cluster using
        /// a node image specified by HTTP/HTTPS URI.
        /// </summary>
        /// <param name="cluster">The cluster being managed.</param>
        /// <param name="cloudMarketplace">
        /// <para>
        /// For cloud environments, this specifies whether the cluster should be provisioned
        /// using a VM image from the public cloud marketplace when <c>true</c> or from the
        /// private NEONFORGE image gallery for testing when <c>false</c>.  This is ignored
        /// for on-premise environments.
        /// </para>
        /// <note>
        /// Only NEONFORGE maintainers will have permission to use the private image.
        /// </note>
        /// </param>
        /// <param name="nodeImageUri">The node image URI.</param>
        /// <param name="logFolder">
        /// <para>
        /// The folder where log files are to be written, otherwise or <c>null</c> or 
        /// empty if logging is disabled.
        /// </para>
        /// <note>
        /// Specific hosting managers may choose to ignore this when it doesn't make sense.
        /// </note>
        /// </param>
        /// <returns>
        /// The <see cref="HostingManager"/> or <c>null</c> if no hosting manager
        /// could be located for the specified cluster environment.
        /// </returns>
        /// <exception cref="NeonKubeException">Thrown if the multiple managers implement support for the same hosting environment.</exception>
        HostingManager GetManagerWithNodeImageUri(ClusterProxy cluster, bool cloudMarketplace, string nodeImageUri, string logFolder = null);

        /// <summary>
        /// Returns the <see cref="HostingManager"/> for provisioning a cluster from
        /// an already downloaded image file already downloaded.
        /// </summary>
        /// <param name="cluster">The cluster being managed.</param>
        /// <param name="nodeImagePath">Specifies the path to the local node image file.</param>
        /// <param name="logFolder">
        /// The folder where log files are to be written, otherwise or <c>null</c> or 
        /// empty if logging is disabled.
        /// </param>
        /// <returns>
        /// The <see cref="HostingManager"/> or <c>null</c> if no hosting manager
        /// could be located for the specified cluster environment.
        /// </returns>
        /// <exception cref="NeonKubeException">Thrown if the multiple managers implement support for the same hosting environment.</exception>
        HostingManager GetManagerWithNodeImageFile(ClusterProxy cluster, string nodeImagePath = null, string logFolder = null);

        /// <summary>
        /// Determines whether a hosting environment is hosted in the cloud.
        /// </summary>
        /// <param name="environment">The target hosting environment.</param>
        /// <returns><c>true</c> for cloud environments.</returns>
        bool IsCloudEnvironment(HostingEnvironment environment);
    }
}
