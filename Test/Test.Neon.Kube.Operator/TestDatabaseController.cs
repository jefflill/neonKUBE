//-----------------------------------------------------------------------------
// FILE:	    TestDatabaseController.cs
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
using System.Diagnostics;
using System.Diagnostics.Contracts;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;

using Neon.Common;
using Neon.Diagnostics;
using Neon.IO;
using Neon.Kube;
using Neon.Kube.Operator.Attributes;
using Neon.Kube.Operator.ResourceManager;
using Neon.Kube.Operator.Controller;
using Neon.Kube.Operator.Util;
using Neon.Kube.Resources;
using Neon.Kube.Resources.Cluster;
using Neon.Retry;
using Neon.Tasks;
using Neon.Time;

using k8s;
using k8s.Models;

using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Prometheus;
using System.Xml.Linq;
using Amazon.Runtime.Internal.Transform;

namespace Test.Neon.Kube.Operator
{
    public class TestDatabaseController : IOperatorController<V1TestDatabase>
    {
        //---------------------------------------------------------------------
        // Instance members

        private readonly IKubernetes k8s;

        /// <summary>
        /// Constructor.
        /// </summary>
        public TestDatabaseController(IKubernetes k8s)
        {
            Covenant.Requires(k8s != null, nameof(k8s));
            this.k8s = k8s;
        }

        /// <inheritdoc/>
        public async Task<ResourceControllerResult> ReconcileAsync(V1TestDatabase resource)
        {
            var statefulSet = new V1StatefulSet()
            {
                Metadata = new V1ObjectMeta()
                {
                    Name = resource.Name(),
                    NamespaceProperty = resource.Namespace()
                },
                Spec = new V1StatefulSetSpec()
                {
                    Replicas = resource.Spec.Servers,
                    Template = new V1PodTemplateSpec()
                    {
                        Spec = new V1PodSpec()
                        {
                            Containers = new List<V1Container>()
                            {
                                new V1Container()
                                {
                                    Image = resource.Spec.Image,
                                }
                            }
                        }
                    },
                    VolumeClaimTemplates = new List<V1PersistentVolumeClaim>()
                    {
                        new V1PersistentVolumeClaim()
                        {
                            Spec = new V1PersistentVolumeClaimSpec()
                            {
                                Resources = new V1ResourceRequirements()
                                {
                                    Requests = new Dictionary<string, ResourceQuantity>()
                                    {
                                        { "storage", new ResourceQuantity(resource.Spec.VolumeSize)}
                                    }
                                }
                            }
                        }
                    }
                }
            };

            await k8s.AppsV1.CreateNamespacedStatefulSetAsync(statefulSet, statefulSet.Namespace());

            var service = new V1Service()
            {
                Metadata = new V1ObjectMeta()
                {
                    Name = resource.Name(),
                    NamespaceProperty = resource.Namespace()
                },
                Spec = new V1ServiceSpec()
                {
                    Selector = new Dictionary<string, string>()
                    {
                        {"app.kubernetes.io/name", resource.Name() }
                    },
                    Ports = new List<V1ServicePort>()
                    {
                        new V1ServicePort(80)
                    }
                }
            };

            await k8s.CoreV1.CreateNamespacedServiceAsync(service, service.Namespace());

            return await Task.FromResult<ResourceControllerResult>(null);
        }
    }
}