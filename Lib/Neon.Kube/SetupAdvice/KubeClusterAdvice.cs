﻿//-----------------------------------------------------------------------------
// FILE:	    KubeSetupAdvice.cs
// CONTRIBUTOR: Jeff Lill
// COPYRIGHT:	Copyright (c) 2005-2021 by neonFORGE LLC.  All rights reserved.
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
using System.Diagnostics.Contracts;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

using ICSharpCode.SharpZipLib.Zip;
using k8s;
using k8s.Models;
using Microsoft.Rest;

using Neon.Collections;
using Neon.Common;
using Neon.Cryptography;
using Neon.IO;
using Neon.Retry;
using Neon.SSH;
using Neon.Tasks;
using Newtonsoft.Json.Linq;

namespace Neon.Kube
{
    /// <summary>
    /// Holds cluster configuration advice initialized early during cluster setup.  This
    /// is used to centralize the decisions about things like resource limitations and 
    /// node taints/affinity based on the overall resources available to the cluster.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="KubeClusterAdvice"/> maintains a dictionary of <see cref="KubeServiceAdvice"/> 
    /// instances keyed by the service identity (one of the service identify constants defined
    /// here).  The constructor initializes empty advice instances for each of the known
    /// neonKUBE services.
    /// </para>
    /// <para>
    /// The basic idea here is that an early setup step will be executed that constructs a
    /// <see cref="KubeClusterAdvice"/> instance, determines resource and other limitations
    /// holistically based on the cluster hosting environment (e.g. WSL2) as well as the
    /// total resources available to the cluster, potentially priortizing resource assignments
    /// to some services over others.  The step will persist the <see cref="KubeClusterAdvice"/>
    /// to the setup controller state as the <see cref="KubeSetupProperty.ClusterAdvice"/>
    /// peoperty so this information will be available to all other deployment steps.
    /// </para>
    /// <para>
    /// <see cref="KubeServiceAdvice"/> inherits from <see cref="ObjectDictionary"/> and can
    /// hold arbitrary key/values.  The idea is to make it easy to add custom values to the
    /// advice for a service that can be picked up in subsequent deployment steps and used
    /// for things like initializing Helm chart values.
    /// </para>
    /// <para>
    /// Although <see cref="KubeServiceAdvice"/> can hold arbitrary key/values, we've
    /// defined class properties to manage the common service properties:
    /// </para>
    /// <list type="table">
    /// <item>
    ///     <term><see cref="KubeServiceAdvice.PodCpuLimit"/></term>
    ///     <description>
    ///     <see cref="double"/>: Identifies the property specifying the maximum
    ///     CPU to assign to each service pod.
    ///     </description>
    /// </item>
    /// <item>
    ///     <term><see cref="KubeServiceAdvice.PodCpuRequest"/></term>
    ///     <description>
    ///     <see cref="double"/>: Identifies the property specifying the CPU to 
    ///     reserve for each service pod.
    ///     </description>
    /// </item>
    /// <item>
    ///     <term><see cref="KubeServiceAdvice.PodMemoryLimit"/></term>
    ///     <description>
    ///     <see cref="decimal"/>: Identifies the property specifying the maxumum
    ///  bytes RAM that can be consumed by each service pod.
    ///     </description>
    /// </item>
    /// <item>
    ///     <term><see cref="KubeServiceAdvice.PodMemoryRequest"/></term>
    ///     <description>
    ///     <see cref="decimal"/>: Identifies the property specifying the bytes of
    ///     RAM to be reserved for each service pod.
    ///     </description>
    /// </item>
    /// <item>
    ///     <term><see cref="KubeServiceAdvice.PodCount"/></term>
    ///     <description>
    ///     <see cref="int"/>: Identifies the property specifying how many pods
    ///     should be deployed for the service.
    ///     </description>
    /// </item>
    /// </list>
    /// </remarks>
    public class KubeClusterAdvice
    {
        //---------------------------------------------------------------------
        // Static members

        /// <summary>
        /// Identifies the neonKUBE cluster's <b>Calico</b> service.
        /// </summary>
        public static string Calico = "calico";

        /// <summary>
        /// Identifies the neonKUBE cluster's <b>Citus Postres</b> service.
        /// </summary>
        public static string CitusPostgresSql = "citus-postgressel";

        /// <summary>
        /// Identifies the neonKUBE cluster's <b>Cortex</b> service.
        /// </summary>
        public static string Cortex = "cortex";

        /// <summary>
        /// Identifies the neonKUBE cluster's <b>Etc nodes</b> service.
        /// </summary>
        public static string EtcdCluster = "etcd-cluster";

        /// <summary>
        /// Identifies the neonKUBE cluster's <b>Etcd Operatoir</b> service.
        /// </summary>
        public static string EtcdOperator = "etcd-operator";

        /// <summary>
        /// Identifies the neonKUBE cluster's <b>FluentBit</b> service.
        /// </summary>
        public static string FluentBit = "fluentbit";

        /// <summary>
        /// Identifies the neonKUBE cluster's <b>Grafana</b> service.
        /// </summary>
        public static string Grafana = "grafana";

        /// <summary>
        /// Identifies the neonKUBE cluster's <b>Harbor</b> service.
        /// </summary>
        public static string Harbor = "harbor";

        /// <summary>
        /// Identifies the neonKUBE cluster's <b>istio-prometheus</b> service.
        /// </summary>
        public static string IstioPrometheus = "istio-prometheus";

        /// <summary>
        /// Identifies the neonKUBE cluster's <b>Jaegar</b> service.
        /// </summary>
        public static string Jaegar = "jaegar";

        /// <summary>
        /// Identifies the neonKUBE cluster's <b>Kaili</b> service.
        /// </summary>
        public static string Kiali = "kaili";

        /// <summary>
        /// Identifies the neonKUBE cluster's <b>Loki</b> service.
        /// </summary>
        public static string Loki = "loki";

        /// <summary>
        /// Identifies the neonKUBE cluster's <b>m3db-cluster</b> service.
        /// </summary>
        public static string M3DBCluster = "m3db-cluster";

        /// <summary>
        /// Identifies the neonKUBE cluster's <b>m3db-operator</b> service.
        /// </summary>
        public static string M3DBOperator = "m3db-operator";

        /// <summary>
        /// Identifies the neonKUBE cluster's <b>Metrics-Server</b> service.
        /// </summary>
        public static string MetricsServer = "metrics-server";

        /// <summary>
        /// Identifies the neonKUBE cluster's <b>Minio</b> service.
        /// </summary>
        public static string Minio = "minio";

        /// <summary>
        /// Identifies the neonKUBE cluster's <b>neon-cluster-manager</b> service.
        /// </summary>
        public static string NeonClusterManager = "neon-cluster-manager";

        /// <summary>
        /// Identifies the neonKUBE cluster's <b>NFS</b> service.
        /// </summary>
        public static string Nfs = "nfs";

        /// <summary>
        /// Identifies the neonKUBE cluster's <b>OpenEBS</b> service.
        /// </summary>
        public static string OpenEbs = "openebs";

        /// <summary>
        /// Identifies the neonKUBE cluster's <b>OpenEBS cStore Operator</b> service.
        /// </summary>
        public static string OpenEbsCstoreOperator = "openebs-cstor-operator";

        /// <summary>
        /// Identifies the neonKUBE cluster's <b>Prometheus Operator</b> service.
        /// </summary>
        public static string PrometheusOperator = "prometheus-operator";

        /// <summary>
        /// Identifies the neonKUBE cluster's <b>PromTail</b> service.
        /// </summary>
        public static string Promtail = "promtail";

        /// <summary>
        /// Identifies the neonKUBE cluster's <b>Redis HA</b> service.
        /// </summary>
        public static string RedisHA = "redis-ha";

        //---------------------------------------------------------------------
        // Instance members

        private Dictionary<string, KubeServiceAdvice>   services   = new Dictionary<string, KubeServiceAdvice>(StringComparer.CurrentCultureIgnoreCase);
        private bool                                    isReadOnly = false;

        /// <summary>
        /// Constructs an instance by initialize empty <see cref="KubeServiceAdvice"/> instances
        /// for each cluster service defined above.
        /// </summary>
        public KubeClusterAdvice()
        {
            var serviceIdentities = new string[]
            {
                Calico,
                CitusPostgresSql,
                Cortex,
                EtcdCluster,
                EtcdOperator,
                FluentBit,
                Grafana,
                Harbor,
                IstioPrometheus,
                Jaegar,
                Kiali,
                Loki,
                M3DBCluster,
                M3DBOperator,
                MetricsServer,
                Minio ,
                NeonClusterManager,
                Nfs,
                OpenEbs,
                PrometheusOperator,
                Promtail,
                RedisHA
            };

            foreach (var identity in serviceIdentities)
            {
                services.Add(identity, new KubeServiceAdvice(identity));
            }
        }

        /// <summary>
        /// <para>
        /// Cluster advice is designed to be configured once during cluster setup and then be
        /// considered to be <b>read-only</b> thereafter.  This property should be set to 
        /// <c>true</c> after the advice is intialized to prevent it from being modified
        /// again.
        /// </para>
        /// <note>
        /// This is necessary because setup is performed on multiple threads and this class
        /// is not inheritly thread-safe and this also fits with the idea that the logic behind
        /// this advice is to be 
        /// </note>
        /// </summary>
        /// <exception cref="InvalidOperationException">Thrown when attempting to make the instance read/write aftyer being set to read-only.</exception>
        public bool IsReadOnly
        {
            get => isReadOnly;

            set
            {
                if (value && !isReadOnly)
                {
                    throw new InvalidOperationException($"[{nameof(KubeClusterAdvice)}] cannot be made read/write after being set to read-only.");
                }

                isReadOnly = value;

                foreach (var serviceAdvice in services.Values)
                {
                    serviceAdvice.IsReadOnly = value;
                }
            }
        }

        /// <summary>
        /// Returns the <see cref="KubeServiceAdvice"/> for the specified service.
        /// </summary>
        /// <param name="identity">Identifies the service (one of the constants defined by this class).</param>
        /// <returns>The <see cref="KubeServiceAdvice"/> instance for the service.</returns>
        /// <exception cref="KeyNotFoundException">Thrown when there's no advice for the service.</exception>
        public KubeServiceAdvice GetServiceAdvise(string identity)
        {
            Covenant.Requires<ArgumentNullException>(!string.IsNullOrEmpty(identity));

            return services[identity];
        }
    }
}
