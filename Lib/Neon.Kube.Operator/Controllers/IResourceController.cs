//-----------------------------------------------------------------------------
// FILE:        IResourceController.cs
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
using System.Diagnostics.Contracts;
using System.IO;
using System.Net;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

using Neon.Common;
using Neon.Diagnostics;
using Neon.IO;
using Neon.Kube.Operator.Attributes;
using Neon.Kube.Operator.Builder;
using Neon.Kube.Operator.ResourceManager;
using Neon.Tasks;

using k8s;
using k8s.Autorest;
using k8s.Models;

using Prometheus;

namespace Neon.Kube.Operator.Controller
{
    /// <summary>
    /// Describes the interface used to implement Neon based operator controllers.
    /// </summary>
    public interface IResourceController
    {
        /// <summary>
        /// Another way to specify the Field selector.
        /// </summary>
        string FieldSelector => null;

        /// <summary>
        /// Another way to specify the Label Selector.
        /// </summary>
        string LabelSelector => null;

        /// <summary>
        /// Starts the controller.
        /// </summary>
        /// <param name="serviceProvider">The <see cref="IServiceProvider"/>.</param>
        /// <returns>The tracking <see cref="Task"/>.</returns>
        public Task StartAsync(IServiceProvider serviceProvider)
        {
            return Task.CompletedTask;
        }
    }
}
