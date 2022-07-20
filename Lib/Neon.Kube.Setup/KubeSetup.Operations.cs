﻿//-----------------------------------------------------------------------------
// FILE:	    KubeSetup.Operations.cs
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
using System.Diagnostics.Contracts;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

using k8s;
using k8s.Models;
using Newtonsoft.Json;

using Neon.Common;
using Neon.Cryptography;
using Neon.IO;
using Neon.Kube.Resources;
using Neon.Net;
using Neon.SSH;
using Neon.Tasks;
using Neon.Kube.Operator;
using System.Dynamic;
using Newtonsoft.Json.Linq;

namespace Neon.Kube
{
    public static partial class KubeSetup
    {
        /// <summary>
        /// Configures a local HAProxy container that makes the Kubernetes etcd
        /// cluster highly available.
        /// </summary>
        /// <param name="controller">The setup controller.</param>
        /// <param name="node">The node where the operation will be performed.</param>
        public static void SetupEtcdHaProxy(ISetupController controller, NodeSshProxy<NodeDefinition> node)
        {
            Covenant.Requires<ArgumentException>(controller != null, nameof(controller));

            var cluster = controller.Get<ClusterProxy>(KubeSetupProperty.ClusterProxy);

            controller.LogProgress(node, verb: "configure", message: "etcd HA");

            var sbHaProxyConfig = new StringBuilder();

            sbHaProxyConfig.Append(
$@"global
    daemon
    log stdout  format raw  local0  info
    maxconn 32000

defaults
    balance                 roundrobin
    retries                 2
    http-reuse              safe
    timeout connect         5000
    timeout client          50000
    timeout server          50000
    timeout check           5000
    timeout http-keep-alive 500

frontend kubernetes_masters
    bind                    *:6442
    mode                    tcp
    log                     global
    option                  tcplog
    default_backend         kubernetes_masters_backend

frontend harbor_http
    bind                    *:80
    mode                    http
    log                     global
    option                  httplog
    default_backend         harbor_backend_http

frontend harbor
    bind                    *:443
    mode                    tcp
    log                     global
    option                  tcplog
    default_backend         harbor_backend

backend kubernetes_masters_backend
    mode                    tcp
    balance                 roundrobin");

            foreach (var master in cluster.Masters)
            {
                sbHaProxyConfig.Append(
$@"
    server {master.Name}         {master.Metadata.Address}:{KubeNodePort.KubeApiServer}");
            }

            sbHaProxyConfig.Append(
$@"
backend harbor_backend_http
    mode                    http
    balance                 roundrobin");

            foreach (var n in cluster.Nodes.Where(n => n.Metadata.Labels.Istio))
            {
                sbHaProxyConfig.Append(
$@"
    server                  {n.Name} {n.Metadata.Address}:{KubeNodePort.IstioIngressHttp}");
            }

            sbHaProxyConfig.Append(
$@"
backend harbor_backend
    mode                    tcp
    balance                 roundrobin");

            foreach (var n in cluster.Nodes.Where(n => n.Metadata.Labels.Istio))
            {
                sbHaProxyConfig.Append(
$@"
    server                  {n.Name} {n.Metadata.Address}:{KubeNodePort.IstioIngressHttps}");
            }

            node.UploadText("/etc/neonkube/neon-etcd-proxy.cfg", sbHaProxyConfig);

            var sbHaProxyPod = new StringBuilder();

            sbHaProxyPod.Append(
$@"
apiVersion: v1
kind: Pod
metadata:
  name: neon-etcd-proxy
  namespace: kube-system
  labels:
    app: neon-etcd-proxy
    role: neon-etcd-proxy
    release: neon-etcd-proxy
spec:
  volumes:
   - name: neon-etcd-proxy-config
     hostPath:
       path: /etc/neonkube/neon-etcd-proxy.cfg
       type: File
  hostNetwork: true
  priorityClassName: { PriorityClass.SystemNodeCritical.Name }
  containers:
    - name: web
      image: {KubeConst.LocalClusterRegistry}/haproxy:{KubeVersions.Haproxy}
      volumeMounts:
        - name: neon-etcd-proxy-config
          mountPath: /etc/haproxy/haproxy.cfg
      ports:
        - name: k8s-masters
          containerPort: 6442
          protocol: TCP
");
            node.UploadText("/etc/kubernetes/manifests/neon-etcd-proxy.yaml", sbHaProxyPod, permissions: "600", owner: "root:root");
        }

        /// <summary>
        /// Adds the Kubernetes node labels.
        /// </summary>
        /// <param name="controller">The setup controller.</param>
        /// <param name="master">The first master node where the operation will be performed.</param>
        /// <returns>The tracking <see cref="Task"/>.</returns>
        public static async Task LabelNodesAsync(ISetupController controller, NodeSshProxy<NodeDefinition> master)
        {
            await SyncContext.Clear;

            Covenant.Requires<ArgumentNullException>(controller != null, nameof(controller));
            Covenant.Requires<ArgumentNullException>(master != null, nameof(master));

            var cluster = controller.Get<ClusterProxy>(KubeSetupProperty.ClusterProxy);
            var k8s     = GetK8sClient(controller);

            controller.ThrowIfCancelled();
            await master.InvokeIdempotentAsync("setup/label-nodes",
                async () =>
                {
                    controller.LogProgress(master, verb: "label", message: "nodes");

                    try
                    {
                        var k8sNodes = (await k8s.ListNodeAsync()).Items;

                        foreach (var node in cluster.Nodes)
                        {
                            controller.ThrowIfCancelled();

                            var k8sNode = k8sNodes.Where(n => n.Metadata.Name == node.Name).FirstOrDefault();

                            var patch = new V1Node()
                            {
                                Metadata = new V1ObjectMeta()
                                {
                                    Labels = k8sNode.Labels()
                                }
                            };

                            if (node.Metadata.IsWorker)
                            {
                                // Kubernetes doesn't set the role for worker nodes so we'll do that here.

                                patch.Metadata.Labels.Add("kubernetes.io/role", "worker");
                            }

                            patch.Metadata.Labels.Add(NodeLabels.LabelDatacenter, cluster.Definition.Datacenter.ToLowerInvariant());
                            patch.Metadata.Labels.Add(NodeLabels.LabelEnvironment, cluster.Definition.Environment.ToString().ToLowerInvariant());

                            foreach (var label in node.Metadata.Labels.All)
                            {
                                if (label.Value != null)
                                {
                                    patch.Metadata.Labels.Add(label.Key, label.Value.ToString());
                                }
                            }

                            await k8s.PatchNodeAsync(new V1Patch(patch, V1Patch.PatchType.StrategicMergePatch), k8sNode.Metadata.Name);
                        }
                    }
                    finally
                    {
                        master.Status = string.Empty;
                    }

                    await Task.CompletedTask;
                });
        }

        /// <summary>
        /// Initializes the cluster on the first manager, joins the remaining
        /// masters and workers to the cluster and then performs the rest of
        /// cluster setup.
        /// </summary>
        /// <param name="controller">The setup controller.</param>
        /// <param name="maxParallel">
        /// The maximum number of operations on separate nodes to be performed in parallel.
        /// This defaults to <see cref="defaultMaxParallelNodes"/>.
        /// </param>
        /// <returns>The tracking <see cref="Task"/>.</returns>
        public static async Task SetupClusterAsync(ISetupController controller, int maxParallel = defaultMaxParallelNodes)
        {
            await SyncContext.Clear;

            Covenant.Requires<ArgumentNullException>(controller != null, nameof(controller));
            Covenant.Requires<ArgumentException>(maxParallel > 0, nameof(maxParallel));

            var cluster   = controller.Get<ClusterProxy>(KubeSetupProperty.ClusterProxy);
            var master    = cluster.FirstMaster;
            var debugMode = controller.Get<bool>(KubeSetupProperty.DebugMode);

            cluster.ClearNodeStatus();

            controller.ThrowIfCancelled();
            ConfigureKubernetes(controller, master);

            controller.ThrowIfCancelled();
            ConfigureKubelet(controller, cluster.Masters);

            controller.ThrowIfCancelled();
            ConfigureWorkstation(controller, master);

            controller.ThrowIfCancelled();
            ConnectCluster(controller);

            controller.ThrowIfCancelled();
            await ConfigureMasterTaintsAsync(controller, master);

            controller.ThrowIfCancelled();
            await TaintNodesAsync(controller);

            controller.ThrowIfCancelled();
            await LabelNodesAsync(controller, master);

            controller.ThrowIfCancelled();
            await CreateNamespacesAsync(controller, master);

            controller.ThrowIfCancelled();
            await InstallCrdsAsync(controller, master);

            controller.ThrowIfCancelled();
            await CreateRootUserAsync(controller, master);

            controller.ThrowIfCancelled();
            await ConfigurePriorityClassesAsync(controller, master);

            controller.ThrowIfCancelled();
            await InstallCalicoCniAsync(controller, master);

            controller.ThrowIfCancelled();
            await InstallMetricsServerAsync(controller, master);

            controller.ThrowIfCancelled();
            await InstallIstioAsync(controller, master);

            controller.ThrowIfCancelled();
            await InstallPrometheusAsync(controller, master);

            controller.ThrowIfCancelled();
            await InstallCertManagerAsync(controller, master);

            controller.ThrowIfCancelled();
            await InstallKubeDashboardAsync(controller, master);

            controller.ThrowIfCancelled();
            if (cluster.Definition.Features.NodeProblemDetector) 
            { 
                await InstallNodeProblemDetectorAsync(controller, master);
            }

            controller.ThrowIfCancelled();
            await InstallOpenEbsAsync(controller, master);

            controller.ThrowIfCancelled();
            await InstallReloaderAsync(controller, master);

            controller.ThrowIfCancelled();
            await InstallSystemDbAsync(controller, master);

            controller.ThrowIfCancelled();
            await InstallRedisAsync(controller, master);

            controller.ThrowIfCancelled();
            await InstallSsoAsync(controller, master);

            controller.ThrowIfCancelled();
            if (cluster.Definition.Features.Kiali)
            {
                await InstallKialiAsync(controller, master);
            }

            controller.ThrowIfCancelled();
            await InstallMinioAsync(controller, master);

            controller.ThrowIfCancelled();
            await InstallHarborAsync(controller, master);

            controller.ThrowIfCancelled();
            await WriteClusterInfoAsync(controller, master);

            controller.ThrowIfCancelled();
            await InstallMonitoringAsync(controller);

            // Install the cluster operators and any required custom resources.
            //
            // NOTE: The neonKUBE CRDs are installed with [neon-cluster-operator]
            //       so we need to install that first.

            controller.ThrowIfCancelled();
            await InstallClusterOperatorAsync(controller, master);

            controller.ThrowIfCancelled();
            await InstallNeonDashboardAsync(controller, master);

            controller.ThrowIfCancelled();
            await InstallNodeAgentAsync(controller, master);

            controller.ThrowIfCancelled();
            await InstallContainerRegistryResourcesAsync(controller, master);

            // IMPORTANT!
            //
            // This must be the last cluster setup steps.

            controller.ThrowIfCancelled();
            await WriteClusterConfigMapsAsync(controller, master);
        }

        /// <summary>
        /// Method to generate Kubernetes cluster configuration.
        /// </summary>
        /// <param name="controller">The setup controller.</param>
        /// <param name="master">The master node where the operation will be performed.</param>
        /// <returns>The tracking <see cref="Task"/>.</returns>
        public static string GenerateKubernetesClusterConfig(ISetupController controller, NodeSshProxy<NodeDefinition> master)
        {
            Covenant.Requires<ArgumentNullException>(controller != null, nameof(controller));
            Covenant.Requires<ArgumentNullException>(master != null, nameof(master));

            var hostingEnvironment   = controller.Get<HostingEnvironment>(KubeSetupProperty.HostingEnvironment);
            var cluster              = controller.Get<ClusterProxy>(KubeSetupProperty.ClusterProxy);
            var controlPlaneEndpoint = $"kubernetes-masters:6442";
            var sbCertSANs           = new StringBuilder();
            var clusterAdvice        = controller.Get<KubeClusterAdvice>(KubeSetupProperty.ClusterAdvice);

            sbCertSANs.AppendLine($"  - \"kubernetes-masters\"");

            if (cluster.Definition.Domain != null)
            {
                sbCertSANs.AppendLine($"  - \"{cluster.Definition.Domain}\"");
            }

            foreach (var address in cluster.Definition.PublicAddresses)
            {
                sbCertSANs.AppendLine($"  - \"{address}\"");
            }

            foreach (var node in cluster.Masters)
            {
                sbCertSANs.AppendLine($"  - \"{node.Metadata.Address}\"");
                sbCertSANs.AppendLine($"  - \"{node.Name}\"");
            }

            if (cluster.Definition.IsDesktopBuiltIn)
            {
                sbCertSANs.AppendLine($"  - \"{Dns.GetHostName()}\"");
                sbCertSANs.AppendLine($"  - \"{cluster.Definition.Name}\"");
            }

            var kubeletFailSwapOnLine = string.Empty;
            var clusterConfig         = new StringBuilder();

            clusterConfig.AppendLine(
$@"
apiVersion: kubeadm.k8s.io/v1beta3
kind: InitConfiguration
nodeRegistration:
  criSocket: {KubeConst.CrioSocket}
  imagePullPolicy: IfNotPresent
---
apiVersion: kubeadm.k8s.io/v1beta2
kind: ClusterConfiguration
clusterName: {cluster.Name}
kubernetesVersion: ""v{KubeVersions.Kubernetes}""
imageRepository: ""{KubeConst.LocalClusterRegistry}""
apiServer:
  extraArgs:
    bind-address: 0.0.0.0
    advertise-address: 0.0.0.0
    logging-format: json
    default-not-ready-toleration-seconds: ""30"" # default 300
    default-unreachable-toleration-seconds: ""30"" #default  300
    allow-privileged: ""true""
    api-audiences: api
    service-account-issuer: https://kubernetes.default.svc
    service-account-key-file: /etc/kubernetes/pki/sa.key
    service-account-signing-key-file: /etc/kubernetes/pki/sa.key
    oidc-issuer-url: https://{ClusterDomain.Sso}.{cluster.Definition.Domain}
    oidc-client-id: kubernetes
    oidc-username-claim: email
    oidc-groups-claim: groups
    oidc-username-prefix: ""-""
    oidc-groups-prefix: """"
    default-watch-cache-size: ""{clusterAdvice.KubeApiServerWatchCacheSize}""
  certSANs:
{sbCertSANs}
controlPlaneEndpoint: ""{controlPlaneEndpoint}""
networking:
  podSubnet: ""{cluster.Definition.Network.PodSubnet}""
  serviceSubnet: ""{cluster.Definition.Network.ServiceSubnet}""
controllerManager:
  extraArgs:
    logging-format: json
    node-monitor-grace-period: 15s #default 40s
    node-monitor-period: 5s #default 5s
    pod-eviction-timeout: 30s #default 5m0s
scheduler:
  extraArgs:
    logging-format: json");

            clusterConfig.AppendLine($@"
---
apiVersion: kubelet.config.k8s.io/v1beta1
kind: KubeletConfiguration
logging:
  format: json
nodeStatusReportFrequency: 4s
volumePluginDir: /var/lib/kubelet/volume-plugins
cgroupDriver: systemd
runtimeRequestTimeout: 5m
{kubeletFailSwapOnLine}
maxPods: {cluster.Definition.Kubernetes.MaxPodsPerNode}
rotateCertificates: true
");

            var kubeProxyMode = "ipvs";

            clusterConfig.AppendLine($@"
---
apiVersion: kubeproxy.config.k8s.io/v1alpha1
kind: KubeProxyConfiguration
mode: {kubeProxyMode}");

            return clusterConfig.ToString();
        }

        /// <summary>
        /// Basic Kubernetes cluster initialization.
        /// </summary>
        /// <param name="controller">The setup controller.</param>
        /// <param name="master">The master node where the operation will be performed.</param>
        public static void ConfigureKubernetes(ISetupController controller, NodeSshProxy<NodeDefinition> master)
        {
            Covenant.Requires<ArgumentNullException>(controller != null, nameof(controller));
            Covenant.Requires<ArgumentNullException>(master != null, nameof(master));

            var hostingEnvironment = controller.Get<HostingEnvironment>(KubeSetupProperty.HostingEnvironment);
            var cluster            = controller.Get<ClusterProxy>(KubeSetupProperty.ClusterProxy);
            var clusterLogin       = controller.Get<ClusterLogin>(KubeSetupProperty.ClusterLogin);

            controller.ThrowIfCancelled();
            master.InvokeIdempotent("setup/cluster-init",
                () =>
                {
                    //---------------------------------------------------------
                    // Initialize the cluster on the first master:

                    controller.LogProgress(master, verb: "create", message: "cluster");

                    // Initialize Kubernetes:

                    controller.ThrowIfCancelled();
                    master.InvokeIdempotent("setup/kubernetes-init",
                        () =>
                        {
                            controller.LogProgress(master, verb: "reset", message: "kubernetes");

                            // It's possible that a previous cluster initialization operation
                            // was interrupted.  This command resets the state.

                            master.SudoCommand("kubeadm reset --force");

                            SetupEtcdHaProxy(controller, master);

                            // CRI-O needs to be running and listening on its unix domain socket so that
                            // Kubelet can start and the cluster can be initialized via [kubeadm].  CRI-O
                            // takes perhaps 20-30 seconds to start and we've run into occassional trouble
                            // with cluster setup failures because CRI-O hadn't started listening on its
                            // socket in time.
                            //
                            // We're going to wait for the presence of the CRI-O socket here.

                            controller.LogProgress(master, verb: "wait", message: "for cri-o");

                            NeonHelper.WaitFor(
                                () =>
                                {
                                    controller.ThrowIfCancelled();

                                    var socketResponse = master.SudoCommand("cat", new object[] { "/proc/net/unix" });

                                    return socketResponse.Success && socketResponse.OutputText.Contains(KubeConst.CrioSocket);
                                },
                                pollInterval: TimeSpan.FromSeconds(0.5),
                                timeout:      TimeSpan.FromSeconds(60));

                            // $note(jefflill):
                            //
                            // We've seen this fail occasionally with this message in the command response:
                            //
                            //      [wait-control-plane] Waiting for the kubelet to boot up the control plane as static Pods from directory "/etc/kubernetes/manifests". This can take up to 4m0s
                            //      [kubelet-check] Initial timeout of 40s passed.
                            //
                            // After some investigation, it looks like the second line is really just
                            // a warning and that kubeadm does continue waiting for the full 4 minutes,
                            // but sometimes this is not long enough.
                            //
                            // We're going to mitigate this by retrying 2 additional times.

                            var clusterConfig  = GenerateKubernetesClusterConfig(controller, master);
                            var kubeInitScript =
$@"
if ! systemctl enable kubelet.service; then
    echo 'FAILED: systemctl enable kubelet.service' >&2
    exit 1
fi

# The first call doesn't specify [--ignore-preflight-errors=all]

if kubeadm init --config cluster.yaml --ignore-preflight-errors=DirAvailable; then
    exit 0
fi

# The additional two calls specify [--ignore-preflight-errors=all] to avoid detecting
# bogus conflicts with itself.

for count in {{1..2}}
do
    if kubeadm init --config cluster.yaml --ignore-preflight-errors=all; then
        exit 0
    fi
done

echo 'FAILED: kubeadm init...' >&2
exit 1
";
                            controller.LogProgress(master, verb: "initialize", message: "kubernetes");

                            var response = master.SudoCommand(CommandBundle.FromScript(kubeInitScript).AddFile("cluster.yaml", clusterConfig.ToString()));

                            // Extract the cluster join command from the response.  We'll need this to join
                            // other nodes to the cluster.

                            var output = response.OutputText;
                            var pStart = output.IndexOf(joinCommandMarker, output.IndexOf(joinCommandMarker) + 1);

                            if (pStart == -1)
                            {
                                master.LogLine("START: [kubeadm init ...] response ============================================");

                                using (var reader = new StringReader(response.AllText))
                                {
                                    foreach (var line in reader.Lines())
                                    {
                                        master.LogLine(line);
                                    }
                                }

                                master.LogLine("END: [kubeadm init ...] response ==============================================");

                                throw new NeonKubeException("Cannot locate the [kubeadm join ...] command in the [kubeadm init ...] response.");
                            }

                            var pEnd = output.Length;

                            if (pEnd == -1)
                            {
                                clusterLogin.SetupDetails.ClusterJoinCommand = Regex.Replace(output.Substring(pStart).Trim(), @"\t|\n|\r|\\", "");
                            }
                            else
                            {
                                clusterLogin.SetupDetails.ClusterJoinCommand = Regex.Replace(output.Substring(pStart, pEnd - pStart).Trim(), @"\t|\n|\r|\\", "");
                            }

                            clusterLogin.Save();

                            controller.LogProgress(master, verb: "created", message: "cluster");
                        });

                    controller.ThrowIfCancelled();
                    master.InvokeIdempotent("setup/kubectl",
                        () =>
                        {
                            controller.LogProgress(master, verb: "configure", message: "kubectl");

                            // Edit the Kubernetes configuration file to rename the context:
                            //
                            //       CLUSTERNAME-admin@kubernetes --> root@CLUSTERNAME
                            //
                            // rename the user:
                            //
                            //      CLUSTERNAME-admin --> CLUSTERNAME-root 

                            var adminConfig = master.DownloadText("/etc/kubernetes/admin.conf");

                            adminConfig = adminConfig.Replace($"kubernetes-admin@{cluster.Definition.Name}", $"root@{cluster.Definition.Name}");
                            adminConfig = adminConfig.Replace("kubernetes-admin", $"root@{cluster.Definition.Name}");

                            master.UploadText("/etc/kubernetes/admin.conf", adminConfig, permissions: "600", owner: "root:root");
                        });

                    // Download the boot master files that will need to be provisioned on
                    // the remaining masters and may also be needed for other purposes
                    // (if we haven't already downloaded these).

                    if (clusterLogin.SetupDetails.MasterFiles != null)
                    {
                        clusterLogin.SetupDetails.MasterFiles = new Dictionary<string, KubeFileDetails>();
                    }

                    if (clusterLogin.SetupDetails.MasterFiles.Count == 0)
                    {
                        // I'm hardcoding the permissions and owner here.  It would be nice to
                        // scrape this from the source files in the future but it's not worth
                        // the bother at this point.

                        var files = new RemoteFile[]
                        {
                            new RemoteFile("/etc/kubernetes/admin.conf", "600", "root:root"),
                            new RemoteFile("/etc/kubernetes/pki/ca.crt", "600", "root:root"),
                            new RemoteFile("/etc/kubernetes/pki/ca.key", "600", "root:root"),
                            new RemoteFile("/etc/kubernetes/pki/sa.pub", "600", "root:root"),
                            new RemoteFile("/etc/kubernetes/pki/sa.key", "644", "root:root"),
                            new RemoteFile("/etc/kubernetes/pki/front-proxy-ca.crt", "644", "root:root"),
                            new RemoteFile("/etc/kubernetes/pki/front-proxy-ca.key", "600", "root:root"),
                            new RemoteFile("/etc/kubernetes/pki/etcd/ca.crt", "644", "root:root"),
                            new RemoteFile("/etc/kubernetes/pki/etcd/ca.key", "600", "root:root"),
                        };

                        foreach (var file in files)
                        {
                            var text = master.DownloadText(file.Path);

                            controller.ThrowIfCancelled();
                            clusterLogin.SetupDetails.MasterFiles[file.Path] = new KubeFileDetails(text, permissions: file.Permissions, owner: file.Owner);
                        }
                    }

                    // Persist the cluster join command and downloaded master files.

                    clusterLogin.Save();

                    //---------------------------------------------------------
                    // Join the remaining masters to the cluster:

                    foreach (var master in cluster.Masters.Where(node => node != master))
                    {
                        try
                        {
                            controller.ThrowIfCancelled();
                            master.InvokeIdempotent("setup/kubectl",
                                () =>
                                {
                                    controller.LogProgress(master, verb: "setup", message: "kubectl");

                                    // It's possible that a previous cluster join operation
                                    // was interrupted.  This command resets the state.

                                    master.SudoCommand("kubeadm reset --force");

                                    // The other (non-boot) masters need files downloaded from the boot master.

                                    controller.LogProgress(master, verb: "upload", message: "master files");

                                    foreach (var file in clusterLogin.SetupDetails.MasterFiles)
                                    {
                                        master.UploadText(file.Key, file.Value.Text, permissions: file.Value.Permissions, owner: file.Value.Owner);
                                    }

                                    // Join the cluster:

                                    controller.ThrowIfCancelled();
                                    master.InvokeIdempotent("setup/master-join",
                                        () =>
                                        {
                                            controller.LogProgress(master, verb: "join", message: "master to cluster");

                                            SetupEtcdHaProxy(controller, master);

                                            var joined = false;

                                            controller.LogProgress(master, verb: "join", message: "as master");

                                            master.SudoCommand("podman run",
                                                   "--name=neon-etcd-proxy",
                                                   "--detach",
                                                   "--restart=always",
                                                   "-v=/etc/neonkube/neon-etcd-proxy.cfg:/etc/haproxy/haproxy.cfg",
                                                   "--network=host",
                                                   "--log-driver=k8s-file",
                                                   $"{KubeConst.LocalClusterRegistry}/haproxy:{KubeVersions.Haproxy}"
                                               );

                                            for (int attempt = 0; attempt < maxJoinAttempts; attempt++)
                                            {
                                                controller.ThrowIfCancelled();

                                                var response = master.SudoCommand(clusterLogin.SetupDetails.ClusterJoinCommand + " --control-plane --ignore-preflight-errors=DirAvailable--etc-kubernetes-manifests", RunOptions.Defaults & ~RunOptions.FaultOnError);

                                                if (response.Success)
                                                {
                                                    joined = true;
                                                    break;
                                                }

                                                Thread.Sleep(joinRetryDelay);
                                            }

                                            if (!joined)
                                            {
                                                throw new Exception($"Unable to join node [{master.Name}] to the after [{maxJoinAttempts}] attempts.");
                                            }

                                            controller.ThrowIfCancelled();
                                            master.SudoCommand("docker kill neon-etcd-proxy");
                                            master.SudoCommand("docker rm neon-etcd-proxy");
                                        });
                                });
                        }
                        catch (Exception e)
                        {
                            master.Fault(NeonHelper.ExceptionError(e));
                            master.LogException(e);
                        }

                        controller.LogProgress(master, verb: "joined", message: "to cluster");
                    }

                    cluster.ClearNodeStatus();

                    // Configure [kube-apiserver] on all the masters

                    foreach (var master in cluster.Masters)
                    {
                        try
                        {
                            controller.ThrowIfCancelled();
                            master.InvokeIdempotent("setup/kubernetes-apiserver",
                                () =>
                                {
                                    controller.LogProgress(master, verb: "configure", message: "api-server");

                                    master.SudoCommand(CommandBundle.FromScript(
@"#!/bin/bash

sed -i 's/.*--enable-admission-plugins=.*/    - --enable-admission-plugins=NamespaceLifecycle,LimitRanger,ServiceAccount,DefaultStorageClass,DefaultTolerationSeconds,MutatingAdmissionWebhook,ValidatingAdmissionWebhook,Priority,ResourceQuota/' /etc/kubernetes/manifests/kube-apiserver.yaml
"));
                                });
                        }
                        catch (Exception e)
                        {
                            master.Fault(NeonHelper.ExceptionError(e));
                            master.LogException(e);
                        }

                        master.Status = string.Empty;
                    }

                    //---------------------------------------------------------
                    // Join the remaining workers to the cluster:

                    var parallelOptions = new ParallelOptions()
                    {
                        MaxDegreeOfParallelism = defaultMaxParallelNodes
                    };

                    Parallel.ForEach(cluster.Workers, parallelOptions,
                        worker =>
                        {
                            try
                            {
                                controller.ThrowIfCancelled();
                                worker.InvokeIdempotent("setup/worker-join",
                                    () =>
                                    {
                                        controller.LogProgress(worker, verb: "join", message: "worker to cluster");

                                        SetupEtcdHaProxy(controller, worker);

                                        var joined = false;

                                        controller.LogProgress(worker, verb: "join", message: "as worker");

                                        controller.ThrowIfCancelled();
                                        worker.SudoCommand("podman run",
                                            "--name=neon-etcd-proxy",
                                            "--detach",
                                            "--restart=always",
                                            "-v=/etc/neonkube/neon-etcd-proxy.cfg:/etc/haproxy/haproxy.cfg",
                                            "--network=host",
                                            "--log-driver=k8s-file",
                                            $"{KubeConst.LocalClusterRegistry}/haproxy:{KubeVersions.Haproxy}",
                                            RunOptions.FaultOnError);

                                        for (int attempt = 0; attempt < maxJoinAttempts; attempt++)
                                        {
                                            controller.ThrowIfCancelled();

                                            var response = worker.SudoCommand(clusterLogin.SetupDetails.ClusterJoinCommand + " --ignore-preflight-errors=DirAvailable--etc-kubernetes-manifests", RunOptions.Defaults & ~RunOptions.FaultOnError);

                                            if (response.Success)
                                            {
                                                joined = true;
                                                break;
                                            }

                                            Thread.Sleep(joinRetryDelay);
                                        }

                                        if (!joined)
                                        {
                                            throw new Exception($"Unable to join node [{worker.Name}] to the cluster after [{maxJoinAttempts}] attempts.");
                                        }

                                        controller.ThrowIfCancelled();
                                        worker.SudoCommand("docker kill neon-etcd-proxy");
                                        worker.SudoCommand("docker rm neon-etcd-proxy");
                                    });
                            }
                            catch (Exception e)
                            {
                                worker.Fault(NeonHelper.ExceptionError(e));
                                worker.LogException(e);
                            }

                            controller.LogProgress(worker, verb: "joined", message: "to cluster");
                        });
                });

            cluster.ClearNodeStatus();
        }

        /// <summary>
        /// Configures the Kubernetes feature gates specified by the <see cref="ClusterDefinition.FeatureGates"/> dictionary.
        /// It does this by editing the API server's static pod manifest located at <b>/etc/kubernetes/manifests/kube-apiserver.yaml</b>
        /// on the master nodes as required.  This also tweaks the <b>--service-account-issuer</b> option.
        /// </summary>
        /// <param name="controller">The setup controller.</param>
        /// <param name="masterNodes">The target master nodes.</param>
        public static void ConfigureKubelet(ISetupController controller, IEnumerable<NodeSshProxy<NodeDefinition>> masterNodes)
        {
            Covenant.Requires<ArgumentNullException>(controller != null, nameof(controller));
            Covenant.Requires<ArgumentNullException>(masterNodes != null, nameof(masterNodes));
            Covenant.Requires<ArgumentException>(masterNodes.Count() > 0, nameof(masterNodes));

            var cluster = controller.Get<ClusterProxy>(KubeSetupProperty.ClusterProxy);
            var clusterDefinition = cluster.Definition;

            // We need to generate a "--feature-gates=..." command line option and add it to the end
            // of the command arguments in the API server static pod manifest at: 
            //
            //      /etc/kubernetes/manifests/kube-apiserver.yaml
            //
            // and while we're at it, we need to modify the [--service-account-issuer] option to
            // pass the Kubernetes compliance tests.
            //
            //      https://github.com/nforgeio/neonKUBE/issues/1385
            //
            // Here's what the static pod manifest looks like:
            //
            //  apiVersion: v1
            //  kind: Pod
            //  metadata:
            //  annotations:
            //      kubeadm.kubernetes.io/kube-apiserver.advertise-address.endpoint: 100.64.0.2:6443
            //    creationTimestamp: null
            //    labels:
            //      component: kube-apiserver
            //      tier: control-plane
            //    name: kube-apiserver
            //    namespace: kube-system
            //  spec:
            //    containers:
            //    - command:
            //      - kube-apiserver
            //      - --advertise-address=0.0.0.0
            //      - --allow-privileged=true
            //      - --api-audiences=api
            //      - --authorization-mode=Node,RBAC
            //      - --bind-address=0.0.0.0
            //      - --client-ca-file=/etc/kubernetes/pki/ca.crt
            //      - --default-not-ready-toleration-seconds=30
            //      - --default-unreachable-toleration-seconds=30
            //      - --enable-admission-plugins=NamespaceLifecycle,LimitRanger,ServiceAccount,DefaultStorageClass,DefaultTolerationSeconds,MutatingAdmissionWebhook,ValidatingAdmissionWebhook,Priority,ResourceQuota
            //      - --enable-bootstrap-token-auth=true
            //      - --etcd-cafile=/etc/kubernetes/pki/etcd/ca.crt
            //      - --etcd-certfile=/etc/kubernetes/pki/apiserver-etcd-client.crt
            //      - --etcd-keyfile=/etc/kubernetes/pki/apiserver-etcd-client.key
            //      - --etcd-servers=https://127.0.0.1:2379
            //      - --insecure-port=0
            //      - --kubelet-client-certificate=/etc/kubernetes/pki/apiserver-kubelet-client.crt
            //      - --kubelet-client-key=/etc/kubernetes/pki/apiserver-kubelet-client.key
            //      - --kubelet-preferred-address-types=InternalIP,ExternalIP,Hostname
            //      - --logging-format=json
            //      - --oidc-client-id=kubernetes
            //      - --oidc-groups-claim=groups
            //      - --oidc-groups-prefix=
            //      - --oidc-issuer-url=https://neon-sso.f4ef74204ee34bbb888e823b3f0c8e3b.neoncluster.io
            //      - --oidc-username-claim=email
            //      - --oidc-username-prefix=-
            //      - --proxy-client-cert-file=/etc/kubernetes/pki/front-proxy-client.crt
            //      - --proxy-client-key-file=/etc/kubernetes/pki/front-proxy-client.key
            //      - --requestheader-allowed-names=front-proxy-client
            //      - --requestheader-client-ca-file=/etc/kubernetes/pki/front-proxy-ca.crt
            //      - --requestheader-extra-headers-prefix=X-Remote-Extra-
            //      - --requestheader-group-headers=X-Remote-Group
            //      - --requestheader-username-headers=X-Remote-User
            //      - --secure-port=6443
            //      - --service-account-issuer=https://kubernetes.default.svc                   <--- WE NEED TO REPLACE THE ORIGINAL SETTING WITH THIS TO PASS KUBERNETES COMPLIANCE TESTS
            //      - --service-account-key-file=/etc/kubernetes/pki/sa.key
            //      - --service-account-signing-key-file=/etc/kubernetes/pki/sa.key
            //      - --service-cluster-ip-range=10.253.0.0/16
            //      - --tls-cert-file=/etc/kubernetes/pki/apiserver.crt
            //      - --tls-private-key-file=/etc/kubernetes/pki/apiserver.key
            //      - --feature-gates=EphemeralContainers=true,...                              <--- WE'RE INSERTING SOMETHING LIKE THIS!
            //      image: neon-registry.node.local/kube-apiserver:v1.21.4
            //      imagePullPolicy: IfNotPresent
            //      livenessProbe:
            //        failureThreshold: 8
            //        httpGet:
            //          host: 100.64.0.2
            //          path: /livez
            //          port: 6443
            //          scheme: HTTPS
            //        initialDelaySeconds: 10
            //        periodSeconds: 10
            //        timeoutSeconds: 15
            //      name: kube-apiserver
            //      ...
            //
            // Note that Kublet will automatically restart the API server's static pod when it
            // notices that that static pod manifest has been modified.

            const string manifestPath = "/etc/kubernetes/manifests/kube-apiserver.yaml";

            foreach (var master in masterNodes)
            {
                controller.ThrowIfCancelled();
                master.InvokeIdempotent("setup/feature-gates",
                    () =>
                    {
                        controller.LogProgress(master, verb: "configure", message: "feature-gates");

                        var manifestText = master.DownloadText(manifestPath);
                        var manifest = NeonHelper.YamlDeserialize<dynamic>(manifestText);
                        var spec = manifest["spec"];
                        var containers = spec["containers"];
                        var container = containers[0];
                        var command = (List<object>)container["command"];
                        var sbFeatures = new StringBuilder();

                        foreach (var featureGate in clusterDefinition.FeatureGates)
                        {
                            sbFeatures.AppendWithSeparator($"{featureGate.Key}={NeonHelper.ToBoolString(featureGate.Value)}", ",");
                        }

                        // Search for a [--feature-gates] command line argument.  If one is present,
                        // we'll replace it, otherwise we'll append a new one.

                        var featureGateOption = $"--feature-gates={sbFeatures}";
                        var existingArgIndex = -1;

                        for (int i = 0; i < command.Count; i++)
                        {
                            var arg = (string)command[i];

                            if (arg.StartsWith("--feature-gates="))
                            {
                                existingArgIndex = i;
                                break;
                            }
                        }

                        if (existingArgIndex >= 0)
                        {
                            command[existingArgIndex] = featureGateOption;
                        }
                        else
                        {
                            command.Add(featureGateOption);
                        }

                        // Update the [---service-account-issuer] command option as well.

                        for (int i = 0; i < command.Count; i++)
                        {
                            var arg = (string)command[i];

                            if (arg.StartsWith("--service-account-issuer="))
                            {
                                command[i] = "--service-account-issuer=https://kubernetes.default.svc";
                                break;
                            }
                        }

                        // Set GOGC so that GC happens more frequently, reducing memory usage.

                        // This is a bit of a 2 part hack because the environment variable needs to be
                        // a string, and YamlDotNet doesn't serialise it as such.

                        var env = new List<Dictionary<string, string>>();

                        env.Add(new Dictionary<string, string>() { 
                            { "name", "GOGC"},
                        });

                        container["env"] = env;

                        manifestText = NeonHelper.YamlSerialize(manifest);

                        var sb = new StringBuilder();
                        using (var reader = new StringReader(manifestText))
                        {
                            foreach (var line in reader.Lines())
                            {
                                sb.AppendLine(line);
                                if (line.Contains("- name: GOGC"))
                                {
                                    sb.AppendLine(line.Replace("- name: GOGC", @"  value: ""25"""));
                                }
                            }
                        }

                        manifestText = sb.ToString();

                        master.UploadText(manifestPath, manifestText, permissions: "600", owner: "root");
                    });
            }
        }

        /// <summary>
        /// Configures the local workstation.
        /// </summary>
        /// <param name="controller">The setup controller.</param>
        /// <param name="firstMaster">The master node where the operation will be performed.</param>
        public static void ConfigureWorkstation(ISetupController controller, NodeSshProxy<NodeDefinition> firstMaster)
        {
            Covenant.Requires<ArgumentNullException>(controller != null, nameof(controller));
            Covenant.Requires<ArgumentNullException>(firstMaster != null, nameof(firstMaster));

            firstMaster.InvokeIdempotent("setup/workstation",
                (Action)(() =>
                {
                    controller.LogProgress(firstMaster, verb: "configure", message: "workstation");

                    var cluster        = controller.Get<ClusterProxy>(KubeSetupProperty.ClusterProxy);
                    var clusterLogin   = controller.Get<ClusterLogin>(KubeSetupProperty.ClusterLogin);
                    var kubeConfigPath = KubeHelper.KubeConfigPath;

                    // Update kubeconfig.

                    var configText = clusterLogin.SetupDetails.MasterFiles["/etc/kubernetes/admin.conf"].Text;
                    var port       = NetworkPorts.KubernetesApiServer;

                    configText = configText.Replace("https://kubernetes-masters:6442", $"https://{cluster.Definition.Domain}:{port}");

                    if (!File.Exists(kubeConfigPath))
                    {
                        File.WriteAllText(kubeConfigPath, configText);
                    }
                    else
                    {
                        // The user already has an existing kubeconfig, so we need
                        // to merge in the new config.

                        var newConfig      = NeonHelper.YamlDeserialize<KubeConfig>(configText);
                        var existingConfig = KubeHelper.Config;

                        // Remove any existing user, context, and cluster with the same names.
                        // Note that we're assuming that there's only one of each in the config
                        // we downloaded from the cluster.

                        var newCluster      = newConfig.Clusters.Single();
                        var newContext      = newConfig.Contexts.Single();
                        var newUser         = newConfig.Users.Single();
                        var existingCluster = existingConfig.GetCluster(newCluster.Name);
                        var existingContext = existingConfig.GetContext(newContext.Name);
                        var existingUser    = existingConfig.GetUser(newUser.Name);

                        if (existingConfig != null)
                        {
                            existingConfig.Clusters.Remove(existingCluster);
                        }

                        if (existingContext != null)
                        {
                            existingConfig.Contexts.Remove(existingContext);
                        }

                        if (existingUser != null)
                        {
                            existingConfig.Users.Remove(existingUser);
                        }

                        existingConfig.Clusters.Add(newCluster);
                        existingConfig.Contexts.Add(newContext);
                        existingConfig.Users.Add(newUser);

                        existingConfig.CurrentContext = newContext.Name;

                        KubeHelper.SetConfig(existingConfig);
                    }

                    // Make sure that the config cached by [KubeHelper] is up to date.

                    KubeHelper.LoadConfig();
                }));
        }

        /// <summary>
        /// Adds the neonKUBE standard priority classes to the cluster.
        /// </summary>
        /// <param name="controller"></param>
        /// <param name="master"></param>
        /// <returns>The tracking <see cref="Task"/>.</returns>
        public static async Task ConfigurePriorityClassesAsync(ISetupController controller, NodeSshProxy<NodeDefinition> master)
        {
            await SyncContext.Clear;
            Covenant.Requires<ArgumentNullException>(controller != null, nameof(controller));
            Covenant.Requires<ArgumentNullException>(master != null, nameof(master));

            var cluster = master.Cluster;
            var k8s = GetK8sClient(controller);

            controller.ThrowIfCancelled();
            master.InvokeIdempotent("setup/priorityclass",
                () =>
                {
                    controller.LogProgress(master, verb: "configure", message: "priority classes");

                    // I couldn't figure out how to specify the priority class name when create them
                    // via the Kubernetes client, so I'll just use [kubectl] to apply them all at
                    // once on the master.

                    var sbPriorityClasses = new StringBuilder();

                    foreach (var priorityClassDef in PriorityClass.Values.Where(priorityClass => !priorityClass.IsSystem))
                    {
                        if (sbPriorityClasses.Length > 0)
                        {
                            sbPriorityClasses.AppendLine("---");
                        }

                        var definition =
$@"apiVersion: scheduling.k8s.io/v1
kind: PriorityClass
metadata:
  name: {priorityClassDef.Name}
value: {priorityClassDef.Value}
description: ""{priorityClassDef.Description}""
preemptionPolicy: PreemptLowerPriority
globalDefault: {NeonHelper.ToBoolString(priorityClassDef.IsDefault)}
";
                        sbPriorityClasses.Append(definition);
                    }

                    var script =
@"
set -euo pipefail

kubectl apply -f priorityclasses.yaml
";
                    var bundle = CommandBundle.FromScript(script);

                    bundle.AddFile("priorityclasses.yaml", sbPriorityClasses.ToString());

                    master.SudoCommand(bundle, RunOptions.FaultOnError);
                });
        }

        /// <summary>
        /// Installs the Calico CNI.
        /// </summary>
        /// <param name="controller">The setup controller.</param>
        /// <param name="master">The master node where the operation will be performed.</param>
        /// <returns>The tracking <see cref="Task"/>.</returns>
        public static async Task InstallCalicoCniAsync(ISetupController controller, NodeSshProxy<NodeDefinition> master)
        {
            await SyncContext.Clear;

            Covenant.Requires<ArgumentNullException>(controller != null, nameof(controller));
            Covenant.Requires<ArgumentNullException>(master != null, nameof(master));

            var cluster       = master.Cluster;
            var k8s           = GetK8sClient(controller);
            var clusterAdvice = controller.Get<KubeClusterAdvice>(KubeSetupProperty.ClusterAdvice);
            var coreDnsAdvice = clusterAdvice.GetServiceAdvice(KubeClusterAdvice.CoreDns);

            controller.ThrowIfCancelled();
            await master.InvokeIdempotentAsync("setup/dns",
                async () =>
                {
                    var coreDnsDeployment = await k8s.ReadNamespacedDeploymentAsync("coredns", KubeNamespace.KubeSystem);

                    var spec = NeonHelper.JsonSerialize(coreDnsDeployment.Spec);
                    var coreDnsDaemonset = new V1DaemonSet()
                    {
                        Metadata = new V1ObjectMeta()
                        {
                            Name = "coredns",
                            NamespaceProperty = KubeNamespace.KubeSystem,
                            Labels = coreDnsDeployment.Metadata.Labels
                        },
                        Spec = NeonHelper.JsonDeserialize<V1DaemonSetSpec>(spec)
                    };

                    coreDnsDaemonset.Spec.Template.Spec.NodeSelector = new Dictionary<string, string>()
                    {
                        { "neonkube.io/node.role", "master" }
                    };

                    coreDnsDaemonset.Spec.Template.Spec.Containers.First().Resources.Requests["memory"] =
                        new ResourceQuantity(ToSiString(coreDnsAdvice.PodMemoryRequest));

                    coreDnsDaemonset.Spec.Template.Spec.Containers.First().Resources.Limits["memory"] =
                        new ResourceQuantity(ToSiString(coreDnsAdvice.PodMemoryLimit));

                    await k8s.CreateNamespacedDaemonSetAsync(coreDnsDaemonset, KubeNamespace.KubeSystem);
                    await k8s.DeleteNamespacedDeploymentAsync(coreDnsDeployment.Name(), coreDnsDeployment.Namespace());

                });
            
            controller.ThrowIfCancelled();
            await master.InvokeIdempotentAsync("setup/cni",
                async () =>
                {
                    controller.LogProgress(master, verb: "setup", message: "calico");

                    var cluster       = master.Cluster;
                    var k8s           = GetK8sClient(controller);
                    var clusterAdvice = controller.Get<KubeClusterAdvice>(KubeSetupProperty.ClusterAdvice);
                    var calicoAdvice  = clusterAdvice.GetServiceAdvice(KubeClusterAdvice.Calico);

                    var values = new Dictionary<string, object>();

                    values.Add("images.organization", KubeConst.LocalClusterRegistry);
                    values.Add($"serviceMonitor.enabled", calicoAdvice.MetricsEnabled ?? clusterAdvice.MetricsEnabled);
                    
                    if (cluster.Definition.Hosting.Environment == HostingEnvironment.Azure)
                    {
                        values.Add($"vEthMtu", "1410");
                        values.Add($"ipipMode", "Never");
                        values.Add($"vxlanMode", "Always");
                    }

                    controller.ThrowIfCancelled();
                    await master.InstallHelmChartAsync(controller, "calico", releaseName: "calico", @namespace: KubeNamespace.KubeSystem, values: values);

                    // Wait for Calico and CoreDNS pods to report that they're running.

                    controller.ThrowIfCancelled();
                    await k8s.WaitForDaemonsetAsync(KubeNamespace.KubeSystem, "calico-node",
                        timeout:           clusterOpTimeout,
                        pollInterval:      clusterOpPollInterval,
                        cancellationToken: controller.CancellationToken);

                    controller.ThrowIfCancelled();
                    await k8s.WaitForDaemonsetAsync(KubeNamespace.KubeSystem, "coredns",
                        timeout:           clusterOpTimeout,
                        pollInterval:      clusterOpPollInterval,
                        cancellationToken: controller.CancellationToken);

                    // Spin up a [dnsutils] pod and then exec into it to confirm that
                    // CoreDNS is answering DNS queries.

                    controller.ThrowIfCancelled();
                    await master.InvokeIdempotentAsync("setup/dnsutils",
                        async () =>
                        {
                            controller.LogProgress(master, verb: "setup", message: "dnsutils");

                            var pod = await k8s.CreateNamespacedPodAsync(
                                new V1Pod()
                                {
                                    Metadata = new V1ObjectMeta()
                                    {
                                        Name              = "dnsutils",
                                        NamespaceProperty = KubeNamespace.NeonSystem
                                    },
                                    Spec = new V1PodSpec()
                                    {
                                        Containers = new List<V1Container>()
                                        {
                                            new V1Container()
                                            {
                                                Name            = "dnsutils",
                                                Image           = $"{KubeConst.LocalClusterRegistry}/kubernetes-e2e-test-images-dnsutils:{KubeVersions.DnsUtils}",
                                                Command         = new List<string>() {"sleep", "3600" },
                                                ImagePullPolicy = "IfNotPresent"
                                            }
                                        },
                                        RestartPolicy = "Always",
                                        Tolerations = new List<V1Toleration>()
                                        {
                                            { new V1Toleration() { Effect = "NoSchedule", OperatorProperty = "Exists" } },
                                            { new V1Toleration() { Effect = "NoExecute", OperatorProperty = "Exists" } }
                                        }
                                    }
                                },
                                KubeNamespace.NeonSystem);

                            await k8s.WaitForPodAsync(pod.Namespace(), pod.Name(),
                                timeout:           clusterOpTimeout,
                                pollInterval:      clusterOpPollInterval,
                                cancellationToken: controller.CancellationToken);
                        });

                    controller.ThrowIfCancelled();
                    await master.InvokeIdempotentAsync("setup/dns-verify",
                        async () =>
                        {
                            controller.LogProgress(master, verb: "verify", message: "dns");

                            // Verify that [coredns] is actually working.

                            await NeonHelper.WaitForAsync(
                                async () =>
                                {
                                    try
                                    {
                                        master.SudoCommand($"kubectl exec -n {KubeNamespace.NeonSystem} -t dnsutils -- nslookup kubernetes.default", RunOptions.LogOutput).EnsureSuccess();

                                        return true;
                                    }
                                    catch
                                    {
                                        // Restart coredns and try again.

                                        var coredns = await k8s.ReadNamespacedDaemonSetAsync("coredns", KubeNamespace.KubeSystem);

                                        await coredns.RestartAsync(k8s);
                                        await Task.Delay(TimeSpan.FromSeconds(20));

                                        return false;
                                    }
                                },
                                timeout:      TimeSpan.FromSeconds(120),
                                pollInterval: TimeSpan.FromMilliseconds(500));

                            await k8s.DeleteNamespacedPodAsync("dnsutils", KubeNamespace.NeonSystem);
                        });
                });

            controller.ThrowIfCancelled();
            await master.InvokeIdempotentAsync("setup/calico-metrics",
                async () =>
                {
                    await NeonHelper.WaitForAsync(async () =>
                    {
                        var configs = await k8s.ListClusterCustomObjectAsync<FelixConfiguration>();

                        return configs.Items.Count() > 0;
                    },
                    timeout:           clusterOpTimeout,
                    pollInterval:      clusterOpPollInterval,
                    cancellationToken: controller.CancellationToken);

                    var configs = await k8s.ListClusterCustomObjectAsync<FelixConfiguration>();

                    dynamic patchContent = new JObject();

                    patchContent.spec                          = new JObject();
                    patchContent.spec.prometheusMetricsEnabled = true;

                    var patch = new V1Patch(NeonHelper.JsonSerialize(patchContent), V1Patch.PatchType.MergePatch);

                    foreach (var felix in configs.Items)
                    {
                        await k8s.PatchClusterCustomObjectAsync<FelixConfiguration>(patch, felix.Name());
                    }
                });

            if (coreDnsAdvice.MetricsEnabled ?? false)
            {
                controller.ThrowIfCancelled();
                await master.InvokeIdempotentAsync("setup/coredns-metrics",
                    async () =>
                    {
                        var serviceMonitor = new ServiceMonitor()
                        {
                            Metadata = new V1ObjectMeta()
                            {
                                Name = "kube-dns",
                                NamespaceProperty = "kube-system"
                            },
                            Spec = new ServiceMonitorSpec()
                            {
                                Endpoints = new List<Endpoint>()
                                {
                                new Endpoint()
                                {
                                    Interval      = coreDnsAdvice.MetricsInterval ?? clusterAdvice.MetricsInterval,
                                    Path          = "/metrics",
                                    ScrapeTimeout = "10s",
                                    TargetPort    = 9153
                                }
                                },
                                NamespaceSelector = new NamespaceSelector()
                                {
                                    MatchNames = new List<string>() { "kube-system" }
                                },
                                Selector = new V1LabelSelector()
                                {
                                    MatchLabels = new Dictionary<string, string>()
                                    {
                                    { "k8s-app", "kube-dns"}
                                    }
                                }
                            }
                        };

                        await k8s.CreateNamespacedCustomObjectAsync<ServiceMonitor>(serviceMonitor, serviceMonitor.Name(), serviceMonitor.Namespace());
                    });
            }
        }

        /// <summary>
        /// Uploads cluster related metadata to cluster nodes to <b>/etc/neonkube/metadata</b>
        /// </summary>
        /// <param name="controller">The setup controller.</param>
        /// <param name="node">The target cluster node.</param>
        /// <returns>The tracking <see cref="Task"/>.</returns>
        public static async Task ConfigureMetadataAsync(ISetupController controller, NodeSshProxy<NodeDefinition> node)
        {
            await SyncContext.Clear;
            Covenant.Requires<ArgumentNullException>(controller != null, nameof(controller));
            Covenant.Requires<ArgumentNullException>(node != null, nameof(node));

            controller.ThrowIfCancelled();
            node.InvokeIdempotent("cluster-metadata",
                () =>
                {
                    node.UploadText(LinuxPath.Combine(KubeNodeFolder.Config, "metadata", "cluster-manifest.json"), NeonHelper.JsonSerialize(ClusterManifest, Formatting.Indented));
                });

            await Task.CompletedTask;
        }

        /// <summary>
        /// Configures pods to be schedule on masters when enabled.
        /// </summary>
        /// <param name="controller">The setup controller.</param>
        /// <param name="master">The master node where the operation will be performed.</param>
        /// <returns>The tracking <see cref="Task"/>.</returns>
        public static async Task ConfigureMasterTaintsAsync(ISetupController controller, NodeSshProxy<NodeDefinition> master)
        {
            await SyncContext.Clear;
            Covenant.Requires<ArgumentNullException>(controller != null, nameof(controller));
            Covenant.Requires<ArgumentNullException>(master != null, nameof(master));

            var cluster = master.Cluster;
            var k8s = GetK8sClient(controller);

            controller.ThrowIfCancelled();
            await master.InvokeIdempotentAsync("setup/kubernetes-master-taints",
                async () =>
                {
                    controller.LogProgress(master, verb: "configure", message: "master taints");

                    // The [kubectl taint] command looks like it can return a non-zero exit code.
                    // We'll ignore this.

                    if (cluster.Definition.Kubernetes.AllowPodsOnMasters.GetValueOrDefault())
                    {
                        var nodes = new V1NodeList();

                        await NeonHelper.WaitForAsync(
                           async () =>
                           {
                               nodes = await k8s.ListNodeAsync(labelSelector: "node-role.kubernetes.io/control-plane=");

                               return nodes.Items.All(n => n.Status.Conditions.Any(c => c.Type == "Ready" && c.Status == "True"));
                           },
                           timeout:           TimeSpan.FromMinutes(5),
                           pollInterval:      TimeSpan.FromSeconds(5),
                           cancellationToken: controller.CancellationToken);

                        foreach (var master in nodes.Items)
                        {
                            controller.ThrowIfCancelled();

                            if (master.Spec.Taints == null)
                            {
                                continue;
                            }

                            var patch = new V1Node()
                            {
                                Spec = new V1NodeSpec()
                                {
                                    Taints = master.Spec.Taints.Where(t => t.Key != "node-role.kubernetes.io/master"
                                                                        && t.Key != "node-role.kubernetes.io/control-plane").ToList()
                                }
                            };

                            await k8s.PatchNodeAsync(new V1Patch(patch, V1Patch.PatchType.StrategicMergePatch), master.Metadata.Name);
                        }
                    }
                });
        }

        /// <summary>
        /// Installs the Kubernetes Metrics Server service.
        /// </summary>
        /// <param name="controller">The setup controller.</param>
        /// <param name="master">The master node where the operation will be performed.</param>
        /// <returns>The tracking <see cref="Task"/>.</returns>
        public static async Task InstallMetricsServerAsync(ISetupController controller, NodeSshProxy<NodeDefinition> master)
        {
            await SyncContext.Clear;

            Covenant.Requires<ArgumentNullException>(controller != null, nameof(controller));
            Covenant.Requires<ArgumentNullException>(master != null, nameof(master));

            var cluster       = master.Cluster;
            var k8s           = GetK8sClient(controller);
            var clusterAdvice = controller.Get<KubeClusterAdvice>(KubeSetupProperty.ClusterAdvice);
            var serviceAdvice = clusterAdvice.GetServiceAdvice(KubeClusterAdvice.MetricsServer);

            controller.ThrowIfCancelled();
            await master.InvokeIdempotentAsync("setup/kubernetes-metrics-server",
                async () =>
                {
                    controller.LogProgress(master, verb: "setup", message: "metrics-server");

                    var values = new Dictionary<string, object>();

                    values.Add("image.organization", KubeConst.LocalClusterRegistry);
                    values.Add("serviceMonitor.enabled", serviceAdvice.MetricsEnabled ?? clusterAdvice.MetricsEnabled);
                    values.Add("serviceMonitor.interval", serviceAdvice.MetricsInterval ?? clusterAdvice.MetricsInterval);

                    int i = 0;

                    foreach (var taint in await GetTaintsAsync(controller, NodeLabels.LabelMetricsInternal, "true"))
                    {
                        values.Add($"tolerations[{i}].key", $"{taint.Key.Split("=")[0]}");
                        values.Add($"tolerations[{i}].effect", taint.Effect);
                        values.Add($"tolerations[{i}].operator", "Exists");
                        i++;
                    }

                    await master.InstallHelmChartAsync(controller, "metrics-server", releaseName: "metrics-server", @namespace: KubeNamespace.KubeSystem, values: values);
                });

            controller.ThrowIfCancelled();
            await master.InvokeIdempotentAsync("setup/kubernetes-metrics-server-ready",
                async () =>
                {
                    controller.LogProgress(master, verb: "wait for", message: "metrics-server");

                    await k8s.WaitForDeploymentAsync("kube-system", "metrics-server", timeout: clusterOpTimeout, pollInterval: clusterOpPollInterval, cancellationToken: controller.CancellationToken);
                });
        }

        /// <summary>
        /// Installs Istio.
        /// </summary>
        /// <param name="controller">The setup controller.</param>
        /// <param name="master">The master node where the operation will be performed.</param>
        /// <returns>The tracking <see cref="Task"/>.</returns>
        public static async Task InstallIstioAsync(ISetupController controller, NodeSshProxy<NodeDefinition> master)
        {
            await SyncContext.Clear;

            Covenant.Requires<ArgumentNullException>(controller != null, nameof(controller));
            Covenant.Requires<ArgumentNullException>(master != null, nameof(master));

            var cluster       = controller.Get<ClusterProxy>(KubeSetupProperty.ClusterProxy);
            var clusterLogin  = controller.Get<ClusterLogin>(KubeSetupProperty.ClusterLogin);
            var k8s           = GetK8sClient(controller);
            var clusterAdvice = controller.Get<KubeClusterAdvice>(KubeSetupProperty.ClusterAdvice);
            var ingressAdvice = clusterAdvice.GetServiceAdvice(KubeClusterAdvice.IstioIngressGateway);
            var proxyAdvice   = clusterAdvice.GetServiceAdvice(KubeClusterAdvice.IstioProxy);
            var pilotAdvice   = clusterAdvice.GetServiceAdvice(KubeClusterAdvice.IstioPilot);

            controller.ThrowIfCancelled();
            await CreateNamespaceAsync(controller, master, KubeNamespace.NeonIngress, false);

            controller.ThrowIfCancelled();
            await master.InvokeIdempotentAsync("setup/ingress",
                async () =>
                {
                    controller.LogProgress(master, verb: "setup", message: "ingress");

                    var values = new Dictionary<string, object>();

                    values.Add("cluster.name", clusterLogin.ClusterDefinition.Name);
                    values.Add("cluster.domain", clusterLogin.ClusterDefinition.Domain);

                    var i = 0;
                    foreach (var rule in master.Cluster.Definition.Network.IngressRules
                        .Where(rule => rule.TargetPort != 0))   // [TargetPort=0] indicates that traffic does not route through ingress gateway
                    {
                        values.Add($"nodePorts[{i}].name", $"{rule.Name}");
                        values.Add($"nodePorts[{i}].protocol", $"{rule.Protocol.ToString().ToUpper()}");
                        values.Add($"nodePorts[{i}].port", rule.ExternalPort);
                        values.Add($"nodePorts[{i}].targetPort", rule.TargetPort);
                        values.Add($"nodePorts[{i}].nodePort", rule.NodePort);
                        i++;
                    }

                    values.Add($"resources.ingress.limits.cpu", $"{ToSiString(ingressAdvice.PodCpuLimit)}");
                    values.Add($"resources.ingress.limits.memory", $"{ToSiString(ingressAdvice.PodMemoryLimit)}");
                    values.Add($"resources.ingress.requests.cpu", $"{ToSiString(ingressAdvice.PodCpuRequest)}");
                    values.Add($"resources.ingress.requests.memory", $"{ToSiString(ingressAdvice.PodMemoryRequest)}");

                    values.Add($"resources.proxy.limits.cpu", $"{ToSiString(proxyAdvice.PodCpuLimit)}");
                    values.Add($"resources.proxy.limits.memory", $"{ToSiString(proxyAdvice.PodMemoryLimit)}");
                    values.Add($"resources.proxy.requests.cpu", $"{ToSiString(proxyAdvice.PodCpuRequest)}");
                    values.Add($"resources.proxy.requests.memory", $"{ToSiString(proxyAdvice.PodMemoryRequest)}");

                    values.Add($"resources.pilot.limits.cpu", $"{ToSiString(proxyAdvice.PodCpuLimit)}");
                    values.Add($"resources.pilot.limits.memory", $"{ToSiString(proxyAdvice.PodMemoryLimit)}");
                    values.Add($"resources.pilot.requests.cpu", $"{ToSiString(proxyAdvice.PodCpuRequest)}");
                    values.Add($"resources.pilot.requests.memory", $"{ToSiString(proxyAdvice.PodMemoryRequest)}");

                    values.Add("serviceMesh.enabled", cluster.Definition.Features.ServiceMesh);
                    
                    await master.InstallHelmChartAsync(controller, "istio",
                        releaseName: "neon-ingress",
                        @namespace: KubeNamespace.NeonIngress,
                        prioritySpec: PriorityClass.SystemClusterCritical.Name,
                        values: values);
                });

            controller.ThrowIfCancelled();
            await master.InvokeIdempotentAsync("setup/ingress-ready",
                async () =>
                {
                    controller.LogProgress(master, verb: "wait for", message: "istio");

                    await NeonHelper.WaitAllAsync(
                        new List<Task>()
                        {
                            k8s.WaitForDeploymentAsync(KubeNamespace.NeonIngress, "istio-operator", timeout: clusterOpTimeout, pollInterval: clusterOpPollInterval, cancellationToken: controller.CancellationToken),
                            k8s.WaitForDeploymentAsync(KubeNamespace.NeonIngress, "istiod", timeout: clusterOpTimeout, pollInterval: clusterOpPollInterval, cancellationToken: controller.CancellationToken),
                            k8s.WaitForDaemonsetAsync(KubeNamespace.NeonIngress, "istio-ingressgateway", timeout: clusterOpTimeout, pollInterval: clusterOpPollInterval, cancellationToken: controller.CancellationToken),
                            k8s.WaitForDaemonsetAsync(KubeNamespace.KubeSystem, "istio-cni-node", timeout: clusterOpTimeout, pollInterval: clusterOpPollInterval, cancellationToken: controller.CancellationToken),
                        },
                        timeoutMessage:    "setup/ingress-ready",
                        cancellationToken: controller.CancellationToken);
                });
        }

        /// <summary>
        /// Installs Cert Manager.
        /// </summary>
        /// <param name="controller">The setup controller.</param>
        /// <param name="master">The master node where the operation will be performed.</param>
        /// <returns>The tracking <see cref="Task"/>.</returns>
        public static async Task InstallCertManagerAsync(ISetupController controller, NodeSshProxy<NodeDefinition> master)
        {
            await SyncContext.Clear;

            Covenant.Requires<ArgumentNullException>(controller != null, nameof(controller));
            Covenant.Requires<ArgumentNullException>(master != null, nameof(master));

            var cluster            = controller.Get<ClusterProxy>(KubeSetupProperty.ClusterProxy);
            var k8s                = GetK8sClient(controller);
            var clusterAdvice      = controller.Get<KubeClusterAdvice>(KubeSetupProperty.ClusterAdvice);
            var serviceAdvice      = clusterAdvice.GetServiceAdvice(KubeClusterAdvice.CertManager);
            var ingressAdvice      = clusterAdvice.GetServiceAdvice(KubeClusterAdvice.IstioIngressGateway);
            var proxyAdvice        = clusterAdvice.GetServiceAdvice(KubeClusterAdvice.IstioProxy);
            var hostingEnvironment = controller.Get<HostingEnvironment>(KubeSetupProperty.HostingEnvironment);

            controller.ThrowIfCancelled();
            await master.InvokeIdempotentAsync("setup/cert-manager",
                async () =>
                {
                    controller.LogProgress(master, verb: "setup", message: "cert-manager");

                    var values = new Dictionary<string, object>();

                    values.Add("image.organization", KubeConst.LocalClusterRegistry);
                    values.Add($"prometheus.servicemonitor.enabled", serviceAdvice.MetricsEnabled ?? clusterAdvice.MetricsEnabled);
                    values.Add($"prometheus.servicemonitor.interval", serviceAdvice.MetricsInterval ?? clusterAdvice.MetricsInterval);

                    int i = 0;

                    foreach (var taint in await GetTaintsAsync(controller, NodeLabels.LabelIngress, "true"))
                    {
                        values.Add($"tolerations[{i}].key", $"{taint.Key.Split("=")[0]}");
                        values.Add($"tolerations[{i}].effect", taint.Effect);
                        values.Add($"tolerations[{i}].operator", "Exists");
                        i++;
                    }

                    await master.InstallHelmChartAsync(controller, "cert-manager",
                        releaseName: "cert-manager",
                        @namespace: KubeNamespace.NeonIngress,
                        prioritySpec: $"global.priorityClassName={PriorityClass.NeonNetwork.Name}",
                        values: values);
                });

            controller.ThrowIfCancelled();
            await master.InvokeIdempotentAsync("setup/cert-manager-ready",
                async () =>
                {
                    controller.LogProgress(master, verb: "wait for", message: "cert-manager");

                    await NeonHelper.WaitAllAsync(
                        new List<Task>()
                        {
                            k8s.WaitForDeploymentAsync(KubeNamespace.NeonIngress, "cert-manager", timeout: clusterOpTimeout, pollInterval: clusterOpPollInterval, cancellationToken: controller.CancellationToken),
                            k8s.WaitForDeploymentAsync(KubeNamespace.NeonIngress, "cert-manager-cainjector", timeout: clusterOpTimeout, pollInterval: clusterOpPollInterval, cancellationToken: controller.CancellationToken),
                            k8s.WaitForDeploymentAsync(KubeNamespace.NeonIngress, "cert-manager-webhook", timeout: clusterOpTimeout, pollInterval: clusterOpPollInterval, cancellationToken: controller.CancellationToken),
                        },
                        timeoutMessage:    "setup/cert-manager-ready",
                        cancellationToken: controller.CancellationToken);
                });

            controller.ThrowIfCancelled();
            await master.InvokeIdempotentAsync("setup/neon-acme",
                async () =>
                {
                    controller.LogProgress(master, verb: "setup", message: "neon-acme");

                    var cluster     = controller.Get<ClusterProxy>(KubeSetupProperty.ClusterProxy);
                    var k8s         = GetK8sClient(controller);
                    var values      = new Dictionary<string, object>();
                    var acmeOptions = cluster.Definition.Network.AcmeOptions;

                    var issuer = new ClusterIssuer()
                    {
                        Metadata = new V1ObjectMeta()
                        {
                            Name              = "neon-acme",
                            NamespaceProperty = KubeNamespace.NeonIngress
                        },
                        Spec = new IssuerSpec()
                        {
                            Acme = acmeOptions.Issuer
                        }
                    };

                    if (issuer.Spec.Acme.ExternalAccountBinding != null)
                    {
                        var secret = new V1Secret()
                        {
                            Metadata = new V1ObjectMeta()
                            {
                                Name = issuer.Spec.Acme.ExternalAccountBinding.KeySecretRef.Name,
                                NamespaceProperty = KubeNamespace.NeonIngress
                            },
                            StringData = new Dictionary<string, string>()
                            {
                                { issuer.Spec.Acme.ExternalAccountBinding.KeySecretRef.Key, issuer.Spec.Acme.ExternalAccountBinding.Key }
                            }
                        };

                        await k8s.UpsertSecretAsync(secret, secret.Namespace());

                        issuer.Spec.Acme.ExternalAccountBinding.Key = null;
                    }

                    if (!string.IsNullOrEmpty(issuer.Spec.Acme.PrivateKey))
                    {
                        var secret = new V1Secret()
                        {
                            Metadata = new V1ObjectMeta()
                            {
                                Name              = issuer.Spec.Acme.PrivateKeySecretRef.Name,
                                NamespaceProperty = KubeNamespace.NeonIngress
                            },
                            StringData = new Dictionary<string, string>()
                            {
                                { issuer.Spec.Acme.PrivateKeySecretRef.Key, issuer.Spec.Acme.PrivateKey }
                            }
                        };

                        await k8s.UpsertSecretAsync(secret, secret.Namespace());

                        issuer.Spec.Acme.PrivateKey                  = null;
                        issuer.Spec.Acme.DisableAccountKeyGeneration = true;
                    }

                    foreach (var solver in issuer.Spec.Acme.Solvers)
                    {
                        if (solver.Dns01.Route53 != null)
                        {
                            var secret = new V1Secret()
                            {
                                Metadata = new V1ObjectMeta()
                                {
                                    Name              = solver.Dns01.Route53.SecretAccessKeySecretRef.Name,
                                    NamespaceProperty = KubeNamespace.NeonIngress
                                },
                                StringData = new Dictionary<string, string>()
                                {
                                    { solver.Dns01.Route53.SecretAccessKeySecretRef.Key, solver.Dns01.Route53.SecretAccessKey }
                                }
                            };

                            await k8s.UpsertSecretAsync(secret, secret.Namespace());

                            solver.Dns01.Route53.SecretAccessKey = null;
                        }
                    }

                    await k8s.UpsertClusterCustomObjectAsync<ClusterIssuer>(issuer, issuer.Name());

                    values.Add("image.organization", KubeConst.LocalClusterRegistry);
                    values.Add("image.tag", KubeVersions.NeonKubeContainerImageTag);
                    values.Add("cluster.name", cluster.Definition.Name);
                    values.Add("cluster.domain", cluster.Definition.Domain);
                    values.Add("certficateDuration", cluster.Definition.Network.AcmeOptions.CertificateDuration);
                    values.Add("certificateRenewBefore", cluster.Definition.Network.AcmeOptions.CertificateRenewBefore);

                    int i = 0;

                    foreach (var taint in await GetTaintsAsync(controller, NodeLabels.LabelIngress, "true"))
                    {
                        values.Add($"tolerations[{i}].key", $"{taint.Key.Split("=")[0]}");
                        values.Add($"tolerations[{i}].effect", taint.Effect);
                        values.Add($"tolerations[{i}].operator", "Exists");
                        i++;
                    }

                    await master.InstallHelmChartAsync(controller, "neon-acme",
                        releaseName:  "neon-acme",
                        @namespace:   KubeNamespace.NeonIngress,
                        prioritySpec: PriorityClass.NeonNetwork.Name,
                        values:       values);
                });
        }

        /// <summary>
        /// Configures the root Kubernetes user.
        /// </summary>
        /// <param name="controller">The setup controller.</param>
        /// <param name="master">The master node where the operation will be performed.</param>
        /// <returns>The tracking <see cref="Task"/>.</returns>
        public static async Task CreateRootUserAsync(ISetupController controller, NodeSshProxy<NodeDefinition> master)
        {
            await SyncContext.Clear;

            Covenant.Requires<ArgumentNullException>(controller != null, nameof(controller));
            Covenant.Requires<ArgumentNullException>(master != null, nameof(master));

            controller.ThrowIfCancelled();
            await master.InvokeIdempotentAsync("setup/root-user",
                async () =>
                {
                    controller.LogProgress(master, verb: "create", message: "root user");

                    var userYaml =
$@"
apiVersion: v1
kind: ServiceAccount
metadata:
  name: {KubeConst.RootUser}-user
  namespace: kube-system
---
apiVersion: rbac.authorization.k8s.io/v1
kind: ClusterRoleBinding
metadata:
  name: {KubeConst.RootUser}-user
roleRef:
  apiGroup: rbac.authorization.k8s.io
  kind: ClusterRole
  name: cluster-admin
subjects:
- kind: ServiceAccount
  name: {KubeConst.RootUser}-user
  namespace: kube-system
- kind: Group
  apiGroup: rbac.authorization.k8s.io
  name: superadmin
";
                    master.KubectlApply(userYaml, RunOptions.FaultOnError);

                    await Task.CompletedTask;
                });
        }

        /// <summary>
        /// Generates a dashboard certificate.
        /// </summary>
        /// <param name="controller">The setup controller.</param>
        /// <returns>The generated certificate.</returns>
        public static TlsCertificate GenerateDashboardCert(ISetupController controller)
        {
            var cluster = controller.Get<ClusterProxy>(KubeSetupProperty.ClusterProxy);

            // We're going to tie the custom certificate to the private IP
            // addresses of the master nodes as well as the cluster domain
            // plus the public ingress address for cloud deployments.
            //
            // This means that only these nodes can accept the traffic and also
            // that we'd need to regenerate the certificate if we add/remove a
            // master node.
            //
            // Here's the tracking task:
            //
            //      https://github.com/nforgeio/neonKUBE/issues/441

            var certHostnames = new List<string>();

            foreach (var master in cluster.Masters)
            {
                certHostnames.Add(master.Metadata.Address);
            }

            certHostnames.Add(cluster.Definition.Domain);

            var clusterAddresses = cluster.HostingManager.GetClusterAddresses();

            foreach (var clusterAddress in clusterAddresses)
            {
                certHostnames.Add(clusterAddress);
            }

            var utcNow     = DateTime.UtcNow;
            var utc10Years = utcNow.AddYears(10);

            var certificate = TlsCertificate.CreateSelfSigned(
                hostnames: certHostnames,
                validDays: (int)(utc10Years - utcNow).TotalDays,
                issuedBy:  "kubernetes-dashboard");

            return certificate;
        }

        /// <summary>
        /// Configures the root Kubernetes user.
        /// </summary>
        /// <param name="controller">The setup controller.</param>
        /// <param name="master">The master node where the operation will be performed.</param>
        /// <returns>The tracking <see cref="Task"/>.</returns>
        public static async Task InstallKubeDashboardAsync(ISetupController controller, NodeSshProxy<NodeDefinition> master)
        {
            await SyncContext.Clear;
            Covenant.Requires<ArgumentNullException>(controller != null, nameof(controller));
            Covenant.Requires<ArgumentNullException>(master != null, nameof(master));

            var cluster       = controller.Get<ClusterProxy>(KubeSetupProperty.ClusterProxy);
            var k8s           = GetK8sClient(controller);
            var clusterAdvice = controller.Get<KubeClusterAdvice>(KubeSetupProperty.ClusterAdvice);
            var serviceAdvice = clusterAdvice.GetServiceAdvice(KubeClusterAdvice.KubernetesDashboard);

            controller.ThrowIfCancelled();
            await master.InvokeIdempotentAsync("setup/kube-dashboard",
                async () =>
                {
                    controller.LogProgress(master, verb: "setup", message: "kubernetes dashboard");

                    var values = new Dictionary<string, object>();

                    values.Add("replicas", serviceAdvice.ReplicaCount);
                    values.Add("cluster.name", cluster.Definition.Name);
                    values.Add("settings.clusterName", cluster.Definition.Name);
                    values.Add("cluster.domain", cluster.Definition.Domain);
                    values.Add("neonkube.clusterDomain.kubernetesDashboard", ClusterDomain.KubernetesDashboard);
                    values.Add($"serviceMonitor.enabled", serviceAdvice.MetricsEnabled ?? clusterAdvice.MetricsEnabled);
                    values.Add($"resources.requests.memory", ToSiString(serviceAdvice.PodMemoryRequest));
                    values.Add($"resources.limits.memory", ToSiString(serviceAdvice.PodMemoryLimit));
                    values.Add("serviceMesh.enabled", cluster.Definition.Features.ServiceMesh);

                    await master.InstallHelmChartAsync(controller, "kubernetes-dashboard",
                        releaseName:     "kubernetes-dashboard",
                        @namespace:      KubeNamespace.NeonSystem,
                        prioritySpec:    PriorityClass.NeonApp.Name,
                        values:          values,
                        progressMessage: "kubernetes-dashboard");

                });
        }

        /// <summary>
        /// Adds the node taints.
        /// </summary>
        /// <param name="controller">The setup controller.</param>
        /// <returns>The tracking <see cref="Task"/>.</returns>
        public static async Task TaintNodesAsync(ISetupController controller)
        {
            await SyncContext.Clear;
            Covenant.Requires<ArgumentNullException>(controller != null, nameof(controller));

            var cluster = controller.Get<ClusterProxy>(KubeSetupProperty.ClusterProxy);
            var master = cluster.FirstMaster;

            controller.ThrowIfCancelled();
            await master.InvokeIdempotentAsync("setup/taint-nodes",
                async () =>
                {
                    controller.LogProgress(master, verb: "taint", message: "nodes");

                    try
                    {
                        // Generate a Bash script we'll submit to the first master
                        // that initializes the taints for all nodes.

                        var sbScript = new StringBuilder();
                        var sbArgs = new StringBuilder();

                        sbScript.AppendLineLinux("#!/bin/bash");

                        foreach (var node in cluster.Nodes)
                        {
                            var taintDefinitions = new List<string>();

                            if (node.Metadata.IsWorker)
                            {
                                // Kubernetes doesn't set the role for worker nodes so we'll do that here.

                                taintDefinitions.Add("kubernetes.io/role=worker");
                            }

                            taintDefinitions.Add($"{NodeLabels.LabelDatacenter}={GetLabelValue(cluster.Definition.Datacenter.ToLowerInvariant())}");
                            taintDefinitions.Add($"{NodeLabels.LabelEnvironment}={GetLabelValue(cluster.Definition.Environment.ToString().ToLowerInvariant())}");

                            if (node.Metadata.Taints != null)
                            {
                                foreach (var taint in node.Metadata.Taints)
                                {
                                    sbScript.AppendLine();
                                    sbScript.AppendLineLinux($"kubectl taint nodes {node.Name} {taint}");
                                }
                            }
                        }

                        master.SudoCommand(CommandBundle.FromScript(sbScript));
                    }
                    finally
                    {
                        master.Status = string.Empty;
                    }

                    await Task.CompletedTask;
                });
        }

        /// <summary>
        /// Installs CRDs used later on in setup by various helm charts.
        /// </summary>
        /// <param name="controller">The setup controller.</param>
        /// <param name="master">The master node where the operation will be performed.</param>
        /// <returns>The tracking <see cref="Task"/>.</returns>
        public static async Task InstallCrdsAsync(ISetupController controller, NodeSshProxy<NodeDefinition> master)
        {
            await SyncContext.Clear;

            Covenant.Requires<ArgumentNullException>(controller != null, nameof(controller));

            var cluster = controller.Get<ClusterProxy>(KubeSetupProperty.ClusterProxy);

            controller.ThrowIfCancelled();
            await master.InvokeIdempotentAsync("setup/install-crds",
                async () =>
                {
                    controller.LogProgress(master, verb: "Install", message: "CRDs");

                    await master.InstallHelmChartAsync(controller, "crd-cluster",
                        releaseName: "crd-cluster",
                        @namespace: KubeNamespace.NeonSystem);
                });
        }

        /// <summary>
        /// Deploy Kiali.
        /// </summary>
        /// <param name="controller">The setup controller.</param>
        /// <param name="master">The master node where the operation will be performed.</param>
        /// <returns>The tracking <see cref="Task"/>.</returns>
        private static async Task InstallKialiAsync(ISetupController controller, NodeSshProxy<NodeDefinition> master)
        {
            await SyncContext.Clear;
            Covenant.Requires<ArgumentNullException>(controller != null, nameof(controller));
            Covenant.Requires<ArgumentNullException>(master != null, nameof(master));

            var cluster       = controller.Get<ClusterProxy>(KubeSetupProperty.ClusterProxy);
            var k8s           = GetK8sClient(controller);
            var clusterAdvice = controller.Get<KubeClusterAdvice>(KubeSetupProperty.ClusterAdvice);
            var serviceAdvice = clusterAdvice.GetServiceAdvice(KubeClusterAdvice.Kiali);

            controller.ThrowIfCancelled();
            await master.InvokeIdempotentAsync("setup/kiali",
                async () =>
                {
                    controller.LogProgress(master, verb: "setup", message: "kiali");

                    var values = new Dictionary<string, object>();
                    var secret = await k8s.ReadNamespacedSecretAsync(KubeConst.DexSecret, KubeNamespace.NeonSystem);

                    values.Add("oidc.secret", Encoding.UTF8.GetString(secret.Data["KUBERNETES_CLIENT_SECRET"]));
                    values.Add("image.operator.organization", KubeConst.LocalClusterRegistry);
                    values.Add("image.operator.repository", "kiali-kiali-operator");
                    values.Add("image.kiali.organization", KubeConst.LocalClusterRegistry);
                    values.Add("image.kiali.repository", "kiali-kiali");
                    values.Add("cluster.name", cluster.Definition.Name);
                    values.Add("cluster.domain", cluster.Definition.Domain);
                    values.Add("neonkube.clusterDomain.sso", ClusterDomain.Sso);
                    values.Add("neonkube.clusterDomain.kiali", ClusterDomain.Kiali);
                    values.Add($"neonkube.clusterDomain.grafana", ClusterDomain.Grafana);
                    values.Add("grafanaPassword", NeonHelper.GetCryptoRandomPassword(cluster.Definition.Security.PasswordLength));
                    values.Add($"metrics.enabled", serviceAdvice.MetricsEnabled ?? clusterAdvice.MetricsEnabled);
                    values.Add($"metrics.serviceMonitor.interval", serviceAdvice.MetricsInterval ?? clusterAdvice.MetricsInterval);

                    int i = 0;
                    foreach (var taint in await GetTaintsAsync(controller, NodeLabels.LabelIstio, "true"))
                    {
                        values.Add($"tolerations[{i}].key", $"{taint.Key.Split("=")[0]}");
                        values.Add($"tolerations[{i}].effect", taint.Effect);
                        values.Add($"tolerations[{i}].operator", "Exists");
                        i++;
                    }

                    await master.InstallHelmChartAsync(controller, "kiali",
                        releaseName:  "kiali-operator",
                        @namespace:   KubeNamespace.NeonSystem,
                        prioritySpec: PriorityClass.NeonApp.Name,
                        values:       values);
                });

            controller.ThrowIfCancelled();
            await master.InvokeIdempotentAsync("setup/kiali-ready",
                async () =>
                {
                    controller.LogProgress(master, verb: "wait for", message: "kiali");

                    await NeonHelper.WaitAllAsync(
                        new List<Task>()
                        {
                            k8s.WaitForDeploymentAsync(KubeNamespace.NeonSystem, "kiali-operator", timeout: clusterOpTimeout, pollInterval: clusterOpPollInterval, cancellationToken: controller.CancellationToken),
                            k8s.WaitForDeploymentAsync(KubeNamespace.NeonSystem, "kiali", timeout: clusterOpTimeout, pollInterval: clusterOpPollInterval, cancellationToken: controller.CancellationToken)
                        },
                        timeoutMessage:    "setup/kiali-ready",
                        cancellationToken: controller.CancellationToken);
                });
        }

        /// <summary>
        /// Some initial kubernetes configuration.
        /// </summary>
        /// <param name="controller">The setup controller.</param>
        /// <param name="master">The master node where the operation will be performed.</param>
        /// <returns>The tracking <see cref="Task"/>.</returns>
        public static async Task KubeSetupAsync(ISetupController controller, NodeSshProxy<NodeDefinition> master)
        {
            await SyncContext.Clear;
            Covenant.Requires<ArgumentNullException>(controller != null, nameof(controller));
            Covenant.Requires<ArgumentNullException>(master != null, nameof(master));

            controller.ThrowIfCancelled();
            await master.InvokeIdempotentAsync("setup/initial-kubernetes", async
                () =>
                {
                    controller.LogProgress(master, verb: "setup", message: "kubernetes");

                    await master.InstallHelmChartAsync(controller, "cluster-setup");
                });
        }

        /// <summary>
        /// Installs the Node Problem Detector.
        /// </summary>
        /// <param name="controller">The setup controller.</param>
        /// <param name="master">The master node where the operation will be performed.</param>
        /// <returns>The tracking <see cref="Task"/>.</returns>
        public static async Task InstallNodeProblemDetectorAsync(ISetupController controller, NodeSshProxy<NodeDefinition> master)
        {
            await SyncContext.Clear;
            Covenant.Requires<ArgumentNullException>(controller != null, nameof(controller));
            Covenant.Requires<ArgumentNullException>(master != null, nameof(master));

            var cluster       = controller.Get<ClusterProxy>(KubeSetupProperty.ClusterProxy);
            var k8s           = GetK8sClient(controller);
            var clusterAdvice = controller.Get<KubeClusterAdvice>(KubeSetupProperty.ClusterAdvice);
            var serviceAdvice = clusterAdvice.GetServiceAdvice(KubeClusterAdvice.NodeProblemDetector);

            var values = new Dictionary<string, object>();

            values.Add("cluster.name", cluster.Definition.Name);
            values.Add("cluster.domain", cluster.Definition.Domain);
            values.Add($"metrics.serviceMonitor.enabled", serviceAdvice.MetricsEnabled ?? clusterAdvice.MetricsEnabled);
            values.Add($"metrics.serviceMonitor.interval", serviceAdvice.MetricsInterval ?? clusterAdvice.MetricsInterval);
            values.Add("serviceMesh.enabled", cluster.Definition.Features.ServiceMesh);

            controller.ThrowIfCancelled();
            await master.InvokeIdempotentAsync("setup/node-problem-detector",
                async () =>
                {
                    await master.InstallHelmChartAsync(controller, "node-problem-detector",
                        releaseName: "node-problem-detector",
                        prioritySpec: PriorityClass.NeonOperator.Name,
                        @namespace:   KubeNamespace.NeonSystem);
                });

            controller.ThrowIfCancelled();
            await master.InvokeIdempotentAsync("setup/node-problem-detector-ready",
                async () =>
                {
                    await k8s.WaitForDaemonsetAsync(KubeNamespace.NeonSystem, "node-problem-detector", timeout: clusterOpTimeout, pollInterval: clusterOpPollInterval, cancellationToken: controller.CancellationToken);
                });
        }

        /// <summary>
        /// Installs OpenEBS.
        /// </summary>
        /// <param name="controller">The setup controller.</param>
        /// <param name="master">The master node where the operation will be performed.</param>
        /// <returns>The tracking <see cref="Task"/>.</returns>
        public static async Task InstallOpenEbsAsync(ISetupController controller, NodeSshProxy<NodeDefinition> master)
        {
            await SyncContext.Clear;
            Covenant.Requires<ArgumentNullException>(controller != null, nameof(controller));
            Covenant.Requires<ArgumentNullException>(master != null, nameof(master));

            var cluster                = controller.Get<ClusterProxy>(KubeSetupProperty.ClusterProxy);
            var clusterAdvice          = controller.Get<KubeClusterAdvice>(KubeSetupProperty.ClusterAdvice);
            var apiServerAdvice        = clusterAdvice.GetServiceAdvice(KubeClusterAdvice.OpenEbsApiServer);
            var cStorAdvice            = clusterAdvice.GetServiceAdvice(KubeClusterAdvice.OpenEbsCstor);
            var provisionerAdvice      = clusterAdvice.GetServiceAdvice(KubeClusterAdvice.OpenEbsProvisioner);
            var jivaAdvice             = clusterAdvice.GetServiceAdvice(KubeClusterAdvice.OpenEbsJiva);
            var localPvAdvice          = clusterAdvice.GetServiceAdvice(KubeClusterAdvice.OpenEbsLocalPvProvisioner);
            var snapshotOperatorAdvice = clusterAdvice.GetServiceAdvice(KubeClusterAdvice.OpenEbsSnapshotOperator);
            var ndmOperatorAdvice      = clusterAdvice.GetServiceAdvice(KubeClusterAdvice.OpenEbsNdmOperator);
            var webhookAdvice          = clusterAdvice.GetServiceAdvice(KubeClusterAdvice.OpenEbsWebhook);

            controller.ThrowIfCancelled();
            await master.InvokeIdempotentAsync("setup/openebs-all",
                async () =>
                {
                    controller.LogProgress(master, verb: "setup", message: "openebs");

                    controller.ThrowIfCancelled();
                    await master.InvokeIdempotentAsync("setup/openebs",
                        async () =>
                        {
                            controller.LogProgress(master, verb: "setup", message: "openebs-base");

                            var values = new Dictionary<string, object>();

                            values.Add("apiserver.image.organization", KubeConst.LocalClusterRegistry);
                            values.Add("helper.image.organization", KubeConst.LocalClusterRegistry);
                            values.Add("localprovisioner.image.organization", KubeConst.LocalClusterRegistry);
                            values.Add("policies.monitoring.image.organization", KubeConst.LocalClusterRegistry);
                            values.Add("snapshotOperator.controller.image.organization", KubeConst.LocalClusterRegistry);
                            values.Add("snapshotOperator.provisioner.image.organization", KubeConst.LocalClusterRegistry);
                            values.Add("provisioner.image.organization", KubeConst.LocalClusterRegistry);
                            values.Add("ndm.image.organization", KubeConst.LocalClusterRegistry);
                            values.Add("ndmOperator.image.organization", KubeConst.LocalClusterRegistry);
                            values.Add("webhook.image.organization", KubeConst.LocalClusterRegistry);
                            values.Add("jiva.image.organization", KubeConst.LocalClusterRegistry);

                            values.Add($"apiserver.replicas", apiServerAdvice.ReplicaCount);
                            values.Add($"provisioner.replicas", provisionerAdvice.ReplicaCount);
                            values.Add($"localprovisioner.replicas", localPvAdvice.ReplicaCount);
                            values.Add($"snapshotOperator.replicas", snapshotOperatorAdvice.ReplicaCount);
                            values.Add($"ndmOperator.replicas", ndmOperatorAdvice.ReplicaCount);
                            values.Add($"webhook.replicas", webhookAdvice.ReplicaCount);
                            values.Add($"serviceMonitor.interval", clusterAdvice.MetricsInterval);

                            values.Add($"openebsMonitoringAddon.cStor.serviceMonitor.enabled", cStorAdvice.MetricsEnabled);
                            values.Add($"openebsMonitoringAddon.jiva.serviceMonitor.enabled", jivaAdvice.MetricsEnabled);
                            values.Add($"openebsMonitoringAddon.lvmLocalPV.serviceMonitor.enabled", localPvAdvice.MetricsEnabled);
                            values.Add($"openebsMonitoringAddon.deviceLocalPV.serviceMonitor.enabled", localPvAdvice.MetricsEnabled);
                            values.Add($"openebsMonitoringAddon.ndm.serviceMonitor.enabled", ndmOperatorAdvice.MetricsEnabled);


                            await master.InstallHelmChartAsync(controller, "openebs",
                                releaseName:  "openebs",
                                @namespace:   KubeNamespace.NeonStorage,
                                prioritySpec: PriorityClass.NeonStorage.Name,
                                values:       values);
                        });

                    switch (cluster.Definition.Storage.OpenEbs.Engine)
                    {
                        case OpenEbsEngine.cStor:

                            await DeployOpenEbsWithcStor(controller, master);
                            break;

                        case OpenEbsEngine.HostPath:
                        case OpenEbsEngine.Jiva:

                            await WaitForOpenEbsReady(controller, master);
                            break;

                        default:
                        case OpenEbsEngine.Default:
                        case OpenEbsEngine.Mayastor:

                            throw new NotImplementedException($"[{cluster.Definition.Storage.OpenEbs.Engine}]");
                    }
                });
        }

        /// <summary>
        /// Deploys OpenEBS using the cStor engine.
        /// </summary>
        /// <param name="controller">The setup controller.</param>
        /// <param name="master">The master node where the operation will be performed.</param>
        /// <returns>The tracking <see cref="Task"/>.</returns>
        private static async Task DeployOpenEbsWithcStor(ISetupController controller, NodeSshProxy<NodeDefinition> master)
        {
            await SyncContext.Clear;

            var cluster            = controller.Get<ClusterProxy>(KubeSetupProperty.ClusterProxy);
            var clusterAdvice      = controller.Get<KubeClusterAdvice>(KubeSetupProperty.ClusterAdvice);
            var k8s                = GetK8sClient(controller);
            var cstorPoolAdvice    = clusterAdvice.GetServiceAdvice(KubeClusterAdvice.OpenEbsCstorPool);
            var cstorPoolAuxAdvice = clusterAdvice.GetServiceAdvice(KubeClusterAdvice.OpenEbsCstorPoolAux);

            controller.ThrowIfCancelled();
            await master.InvokeIdempotentAsync("setup/openebs-cstor",
                async () =>
                {
                    controller.LogProgress(master, verb: "setup", message: "openebs-cstor");

                    var values = new Dictionary<string, object>();

                    values.Add("cspcOperator.image.organization", KubeConst.LocalClusterRegistry);
                    values.Add("cspcOperator.poolManager.image.organization", KubeConst.LocalClusterRegistry);
                    values.Add("cspcOperator.cstorPool.image.organization", KubeConst.LocalClusterRegistry);
                    values.Add("cspcOperator.cstorPoolExporter.image.organization", KubeConst.LocalClusterRegistry);

                    values.Add("cvcOperator.image.organization", KubeConst.LocalClusterRegistry);
                    values.Add("cvcOperator.target.image.organization", KubeConst.LocalClusterRegistry);
                    values.Add("cvcOperator.volumeMgmt.image.organization", KubeConst.LocalClusterRegistry);
                    values.Add("cvcOperator.volumeExporter.image.organization", KubeConst.LocalClusterRegistry);

                    values.Add("csiController.resizer.image.organization", KubeConst.LocalClusterRegistry);
                    values.Add("csiController.snapshotter.image.organization", KubeConst.LocalClusterRegistry);
                    values.Add("csiController.snapshotController.image.organization", KubeConst.LocalClusterRegistry);
                    values.Add("csiController.attacher.image.organization", KubeConst.LocalClusterRegistry);
                    values.Add("csiController.provisioner.image.organization", KubeConst.LocalClusterRegistry);
                    values.Add("csiController.driverRegistrar.image.organization", KubeConst.LocalClusterRegistry);

                    values.Add("cstorCSIPlugin.image.organization", KubeConst.LocalClusterRegistry);

                    values.Add("csiNode.driverRegistrar.image.organization", KubeConst.LocalClusterRegistry);

                    values.Add("admissionServer.image.organization", KubeConst.LocalClusterRegistry);

                    await master.InstallHelmChartAsync(controller, "openebs-cstor-operator", releaseName: "openebs-cstor", values: values, @namespace: KubeNamespace.NeonStorage);
                });

            controller.ThrowIfCancelled();
            await WaitForOpenEbsReady(controller, master);

            controller.LogProgress(master, verb: "setup", message: "openebs-pool");

            controller.ThrowIfCancelled();
            await master.InvokeIdempotentAsync("setup/openebs-pool",
                async () =>
                {
                    var cStorPoolCluster = new V1CStorPoolCluster()
                    {
                        Metadata = new V1ObjectMeta()
                        {
                            Name              = "cspc-stripe",
                            NamespaceProperty = KubeNamespace.NeonStorage
                        },
                        Spec = new V1CStorPoolClusterSpec()
                        {
                            Pools     = new List<V1CStorPoolSpec>(),
                            Resources = new V1ResourceRequirements()
                            {
                                Limits   = new Dictionary<string, ResourceQuantity>() { { "memory", new ResourceQuantity(ToSiString(cstorPoolAdvice.PodMemoryLimit)) } },
                                Requests = new Dictionary<string, ResourceQuantity>() { { "memory", new ResourceQuantity(ToSiString(cstorPoolAdvice.PodMemoryRequest)) } },
                            },
                            AuxResources = new V1ResourceRequirements()
                            {
                                Limits   = new Dictionary<string, ResourceQuantity>() { { "memory", new ResourceQuantity(ToSiString(cstorPoolAuxAdvice.PodMemoryLimit)) } },
                                Requests = new Dictionary<string, ResourceQuantity>() { { "memory", new ResourceQuantity(ToSiString(cstorPoolAuxAdvice.PodMemoryRequest)) } },
                            }
                        }
                    };

                    var blockDevices = await k8s.ListNamespacedCustomObjectAsync<V1CStorBlockDevice>(KubeNamespace.NeonStorage);

                    foreach (var node in cluster.Definition.Nodes)
                    {
                        if (blockDevices.Items.Any(device => device.Spec.NodeAttributes.GetValueOrDefault("nodeName") == node.Name))
                        {
                            var pool = new V1CStorPoolSpec()
                            {
                                NodeSelector = new Dictionary<string, string>()
                                {
                                    { "kubernetes.io/hostname", node.Name }
                                },
                                DataRaidGroups = new List<V1CStorDataRaidGroup>()
                                {
                                    new V1CStorDataRaidGroup()
                                    {
                                        BlockDevices = new List<V1CStorBlockDeviceRef>()
                                    }
                                },
                                PoolConfig = new V1CStorPoolConfig()
                                {
                                    DataRaidGroupType = DataRaidGroupType.Stripe,
                                    Tolerations       = new List<V1Toleration>()
                                    {
                                        { new V1Toleration() { Effect = "NoSchedule", OperatorProperty = "Exists" } },
                                        { new V1Toleration() { Effect = "NoExecute", OperatorProperty = "Exists" } }
                                    }
                                }
                            };

                            foreach (var device in blockDevices.Items.Where(device => device.Spec.NodeAttributes.GetValueOrDefault("nodeName") == node.Name))
                            {
                                pool.DataRaidGroups.FirstOrDefault().BlockDevices.Add(
                                    new V1CStorBlockDeviceRef()
                                    {
                                        BlockDeviceName = device.Metadata.Name
                                    });
                            }

                            cStorPoolCluster.Spec.Pools.Add(pool);
                        }
                    }

                    await k8s.CreateNamespacedCustomObjectAsync(cStorPoolCluster, cStorPoolCluster.Name(), cStorPoolCluster.Namespace());
                });

            controller.ThrowIfCancelled();
            await master.InvokeIdempotentAsync("setup/openebs-cstor-ready",
                async () =>
                {
                    controller.LogProgress(master, verb: "wait for", message: "openebs cstor");

                    await NeonHelper.WaitAllAsync(
                        new List<Task>()
                        {
                            k8s.WaitForDaemonsetAsync(KubeNamespace.NeonStorage, "openebs-cstor-csi-node", timeout: clusterOpTimeout, pollInterval: clusterOpPollInterval, cancellationToken: controller.CancellationToken),
                            k8s.WaitForDeploymentAsync(KubeNamespace.NeonStorage, "openebs-cstor-admission-server", timeout: clusterOpTimeout, pollInterval: clusterOpPollInterval, cancellationToken: controller.CancellationToken),
                            k8s.WaitForDeploymentAsync(KubeNamespace.NeonStorage, "openebs-cstor-cvc-operator", timeout: clusterOpTimeout, pollInterval: clusterOpPollInterval, cancellationToken: controller.CancellationToken),
                            k8s.WaitForDeploymentAsync(KubeNamespace.NeonStorage, "openebs-cstor-cspc-operator", timeout: clusterOpTimeout, pollInterval: clusterOpPollInterval, cancellationToken: controller.CancellationToken)
                        },
                        timeoutMessage:    "setup/openebs-cstor-ready",
                        cancellationToken: controller.CancellationToken);
                });

            var replicas = 3;

            if (cluster.Definition.Nodes.Where(node => node.OpenEbsStorage).Count() < replicas)
            {
                replicas = cluster.Definition.Nodes.Where(node => node.OpenEbsStorage).Count();
            }

            controller.ThrowIfCancelled();
            await CreateCstorStorageClass(controller, master, "openebs-cstor", replicaCount: replicas);
        }

        /// <summary>
        /// Waits for OpenEBS to become ready.
        /// </summary>
        /// <param name="controller">The setup controller.</param>
        /// <param name="master">The master node where the operation will be performed.</param>
        /// <returns>The tracking <see cref="Task"/>.</returns>
        private static async Task WaitForOpenEbsReady(ISetupController controller, NodeSshProxy<NodeDefinition> master)
        {
            await SyncContext.Clear;

            var k8s = GetK8sClient(controller);

            controller.ThrowIfCancelled();
            await master.InvokeIdempotentAsync("setup/openebs-ready",
                async () =>
                {
                    controller.LogProgress(master, verb: "wait for", message: "openebs");

                    await NeonHelper.WaitAllAsync(
                        new List<Task>()
                        {
                            k8s.WaitForDaemonsetAsync(KubeNamespace.NeonStorage, "openebs-ndm", timeout: clusterOpTimeout, pollInterval: clusterOpPollInterval, cancellationToken: controller.CancellationToken),
                            k8s.WaitForDaemonsetAsync(KubeNamespace.NeonStorage, "openebs-ndm-node-exporter", timeout: clusterOpTimeout, pollInterval: clusterOpPollInterval, cancellationToken: controller.CancellationToken),
                            k8s.WaitForDeploymentAsync(KubeNamespace.NeonStorage, "openebs-admission-server", timeout: clusterOpTimeout, pollInterval: clusterOpPollInterval, cancellationToken: controller.CancellationToken),
                            k8s.WaitForDeploymentAsync(KubeNamespace.NeonStorage, "openebs-apiserver", timeout: clusterOpTimeout, pollInterval: clusterOpPollInterval, cancellationToken: controller.CancellationToken),
                            k8s.WaitForDeploymentAsync(KubeNamespace.NeonStorage, "openebs-localpv-provisioner", timeout: clusterOpTimeout, pollInterval: clusterOpPollInterval, cancellationToken: controller.CancellationToken),
                            k8s.WaitForDeploymentAsync(KubeNamespace.NeonStorage, "openebs-ndm-operator", timeout: clusterOpTimeout, pollInterval: clusterOpPollInterval, cancellationToken: controller.CancellationToken),
                            k8s.WaitForDeploymentAsync(KubeNamespace.NeonStorage, "openebs-ndm-cluster-exporter", timeout: clusterOpTimeout, pollInterval: clusterOpPollInterval, cancellationToken: controller.CancellationToken),
                            k8s.WaitForDeploymentAsync(KubeNamespace.NeonStorage, "openebs-provisioner", timeout: clusterOpTimeout, pollInterval: clusterOpPollInterval, cancellationToken: controller.CancellationToken),
                            k8s.WaitForDeploymentAsync(KubeNamespace.NeonStorage, "openebs-snapshot-operator", timeout: clusterOpTimeout, pollInterval: clusterOpPollInterval, cancellationToken: controller.CancellationToken)
                        },
                        timeoutMessage:    "setup/openebs-ready",
                        cancellationToken: controller.CancellationToken);
                });
        }

        /// <summary>
        /// Creates a Kubernetes namespace.
        /// </summary>
        /// <param name="controller">The setup controller.</param>
        /// <param name="master">The master node where the operation will be performed.</param>
        /// <param name="name">The new Namespace name.</param>
        /// <param name="istioInjectionEnabled">Whether Istio sidecar injection should be enabled.</param>
        /// <returns>The tracking <see cref="Task"/>.</returns>
        public static async Task CreateNamespaceAsync(
            ISetupController                controller,
            NodeSshProxy<NodeDefinition>    master,
            string                          name,
            bool                            istioInjectionEnabled = true)
        {
            await SyncContext.Clear;
            Covenant.Requires<ArgumentNullException>(controller != null, nameof(controller));
            Covenant.Requires<ArgumentNullException>(master != null, nameof(master));

            var k8s = GetK8sClient(controller);

            controller.ThrowIfCancelled();
            await master.InvokeIdempotentAsync($"setup/namespace-{name}",
                async () =>
                {
                    await k8s.CreateNamespaceAsync(
                        new V1Namespace()
                        {
                            Metadata = new V1ObjectMeta()
                            {
                                Name = name,
                                Labels = new Dictionary<string, string>()
                                {
                                    { "istio-injection", istioInjectionEnabled ? "enabled" : "disabled" }
                                }
                            }
                        });
                });
        }

        /// <summary>
        /// Creates a Kubernetes Storage Class.
        /// </summary>
        /// <param name="controller">The setup controller.</param>
        /// <param name="master">The master node where the operation will be performed.</param>
        /// <param name="name">The new <see cref="V1StorageClass"/> name.</param>
        /// <param name="replicaCount">Specifies the data replication factor.</param>
        /// <param name="storagePool">Specifies the OpenEBS storage pool.</param>
        /// <returns>The tracking <see cref="Task"/>.</returns>
        public static async Task CreateJivaStorageClass(
            ISetupController                controller,
            NodeSshProxy<NodeDefinition>    master,
            string                          name,
            int                             replicaCount = 3,
            string                          storagePool  = "default")
        {
            await SyncContext.Clear;
            Covenant.Requires<ArgumentNullException>(controller != null, nameof(controller));
            Covenant.Requires<ArgumentNullException>(master != null, nameof(master));
            Covenant.Requires<ArgumentNullException>(!string.IsNullOrEmpty(name), nameof(name));
            Covenant.Requires<ArgumentException>(replicaCount > 0, nameof(replicaCount));

            var k8s = GetK8sClient(controller);

            controller.ThrowIfCancelled();
            await master.InvokeIdempotentAsync($"setup/storage-class-jiva-{name}",
                async () =>
                {

                    if (master.Cluster.Definition.Nodes.Count() < replicaCount)
                    {
                        replicaCount = master.Cluster.Definition.Nodes.Count();
                    }

                    var storageClass = new V1StorageClass()
                    {
                        Metadata = new V1ObjectMeta()
                        {
                            Name        = name,
                            Annotations = new Dictionary<string, string>()
                            {
                                {  "cas.openebs.io/config",
$@"- name: ReplicaCount
  value: ""{replicaCount}""
- name: StoragePool
  value: {storagePool}
" },
                                {"openebs.io/cas-type", "jiva" }
                            },
                        },
                        Provisioner       = "openebs.io/provisioner-iscsi",
                        ReclaimPolicy     = "Delete",
                        VolumeBindingMode = "WaitForFirstConsumer"
                    };

                    await k8s.CreateStorageClassAsync(storageClass);
                });
        }

        /// <summary>
        /// Creates a Kubernetes Storage Class.
        /// </summary>
        /// <param name="controller">The setup controller.</param>
        /// <param name="master">The master node where the operation will be performed.</param>
        /// <param name="name">The new <see cref="V1StorageClass"/> name.</param>
        /// <returns>The tracking <see cref="Task"/>.</returns>
        public static async Task CreateHostPathStorageClass(
            ISetupController                controller,
            NodeSshProxy<NodeDefinition>    master,
            string                          name)
        {
            await SyncContext.Clear;
            Covenant.Requires<ArgumentNullException>(controller != null, nameof(controller));
            Covenant.Requires<ArgumentNullException>(master != null, nameof(master));
            Covenant.Requires<ArgumentNullException>(!string.IsNullOrEmpty(name), nameof(name));

            var k8s = GetK8sClient(controller);

            controller.ThrowIfCancelled();
            await master.InvokeIdempotentAsync($"setup/storage-class-hostpath-{name}",
                async () =>
                {
                    var storageClass = new V1StorageClass()
                    {
                        Metadata = new V1ObjectMeta()
                        {
                            Name        = name,
                            Annotations = new Dictionary<string, string>()
                            {
                                {  "cas.openebs.io/config",
$@"- name: StorageType
  value: ""hostpath""
- name: BasePath
  value: /var/openebs/local
" },
                                {"openebs.io/cas-type", "local" }
                            },
                        },
                        Provisioner       = "openebs.io/local",
                        ReclaimPolicy     = "Delete",
                        VolumeBindingMode = "WaitForFirstConsumer"
                    };

                    await k8s.CreateStorageClassAsync(storageClass);
                });
        }

        /// <summary>
        /// Creates an OpenEBS cStor Kubernetes Storage Class.
        /// </summary>
        /// <param name="controller">The setup controller.</param>
        /// <param name="master">The master node where the operation will be performed.</param>
        /// <param name="name">The new <see cref="V1StorageClass"/> name.</param>
        /// <param name="cstorPoolCluster">Specifies the cStor pool name.</param>
        /// <param name="replicaCount">Specifies the data replication factor.</param>
        /// <returns>The tracking <see cref="Task"/>.</returns>
        public static async Task CreateCstorStorageClass(
            ISetupController                controller,
            NodeSshProxy<NodeDefinition>    master,
            string                          name,
            string                          cstorPoolCluster = "cspc-stripe",
            int                             replicaCount     = 3)
        {
            await SyncContext.Clear;
            Covenant.Requires<ArgumentNullException>(controller != null, nameof(controller));
            Covenant.Requires<ArgumentNullException>(master != null, nameof(master));
            Covenant.Requires<ArgumentNullException>(!string.IsNullOrEmpty(name), nameof(name));
            Covenant.Requires<ArgumentException>(replicaCount > 0, nameof(replicaCount));

            var k8s = GetK8sClient(controller);

            controller.ThrowIfCancelled();
            await master.InvokeIdempotentAsync($"setup/storage-class-cstor-{name}",
                async () =>
                {
                    if (master.Cluster.Definition.Nodes.Where(node => node.OpenEbsStorage).Count() < replicaCount)
                    {
                        replicaCount = master.Cluster.Definition.Nodes.Where(node => node.OpenEbsStorage).Count();
                    }

                    var storageClass = new V1StorageClass()
                    {
                        Metadata = new V1ObjectMeta()
                        {
                            Name = name
                        },
                        Parameters = new Dictionary<string, string>
                        {
                            {  "cas-type", "cstor" },
                            {  "cstorPoolCluster", cstorPoolCluster },
                            {  "replicaCount", $"{replicaCount}" },
                        },
                        AllowVolumeExpansion = true,
                        Provisioner          = "cstor.csi.openebs.io",
                        ReclaimPolicy        = "Delete",
                        VolumeBindingMode    = "Immediate"
                    };

                    await k8s.CreateStorageClassAsync(storageClass);
                });
        }

        /// <summary>
        /// Creates the approperiate OpenEBS Kubernetes Storage Class for the cluster.
        /// </summary>
        /// <param name="controller">The setup controller.</param>
        /// <param name="master">The master node where the operation will be performed.</param>
        /// <param name="name">The new <see cref="V1StorageClass"/> name.</param>
        /// <param name="replicaCount">Specifies the data replication factor.</param>
        /// <returns>The tracking <see cref="Task"/>.</returns>
        public static async Task CreateStorageClass(
            ISetupController                controller,
            NodeSshProxy<NodeDefinition>    master,
            string                          name,
            int                             replicaCount = 3)
        {
            await SyncContext.Clear;
            Covenant.Requires<ArgumentNullException>(controller != null, nameof(controller));
            Covenant.Requires<ArgumentNullException>(master != null, nameof(master));
            Covenant.Requires<ArgumentNullException>(!string.IsNullOrEmpty(name), nameof(name));
            Covenant.Requires<ArgumentException>(replicaCount > 0, nameof(replicaCount));

            var cluster = controller.Get<ClusterProxy>(KubeSetupProperty.ClusterProxy);

            controller.ThrowIfCancelled();

            switch (cluster.Definition.Storage.OpenEbs.Engine)
            {
                case OpenEbsEngine.Default:

                    throw new InvalidOperationException($"[{nameof(OpenEbsEngine.Default)}] is not valid here.  This must be set to one of the other storage engines in [{nameof(OpenEbsOptions)}.Validate()].");

                case OpenEbsEngine.HostPath:

                    await CreateHostPathStorageClass(controller, master, name);
                    break;

                case OpenEbsEngine.cStor:

                    await CreateCstorStorageClass(controller, master, name);
                    break;

                case OpenEbsEngine.Jiva:

                    await CreateJivaStorageClass(controller, master, name);
                    break;

                case OpenEbsEngine.Mayastor:
                default:

                    throw new NotImplementedException($"Support for the [{cluster.Definition.Storage.OpenEbs.Engine}] OpenEBS storage engine is not implemented.");
            };
        }

        /// <summary>
        /// Installs The Grafana Agent to the monitoring namespace.
        /// </summary>
        /// <param name="controller">The setup controller.</param>
        /// <param name="master">The master node where the operation will be performed.</param>
        /// <returns>The tracking <see cref="Task"/>.</returns>
        public static async Task InstallPrometheusAsync(ISetupController controller, NodeSshProxy<NodeDefinition> master)
        {
            await SyncContext.Clear;
            Covenant.Requires<ArgumentNullException>(controller != null, nameof(controller));
            Covenant.Requires<ArgumentNullException>(master != null, nameof(master));

            var cluster         = controller.Get<ClusterProxy>(KubeSetupProperty.ClusterProxy);
            var clusterAdvice   = controller.Get<KubeClusterAdvice>(KubeSetupProperty.ClusterAdvice);
            var agentAdvice     = clusterAdvice.GetServiceAdvice(KubeClusterAdvice.GrafanaAgent);
            var agentNodeAdvice = clusterAdvice.GetServiceAdvice(KubeClusterAdvice.GrafanaAgentNode);
            var blackboxAdvice  = clusterAdvice.GetServiceAdvice(KubeClusterAdvice.BlackboxExporter);
            var istioAdvice     = clusterAdvice.GetServiceAdvice(KubeClusterAdvice.IstioProxy);

            controller.ThrowIfCancelled();
            await master.InvokeIdempotentAsync("setup/monitoring-prometheus",
                async () =>
                {
                    controller.LogProgress(master, verb: "setup", message: "prometheus");

                    var values = new Dictionary<string, object>();
                    var i = 0;

                    values.Add($"cluster.name", cluster.Definition.Name);
                    values.Add($"cluster.domain", cluster.Definition.Domain);
                    values.Add($"cluster.datacenter", cluster.Definition.Datacenter);
                    values.Add($"cluster.version", cluster.Definition.ClusterVersion);
                    values.Add($"cluster.hostingEnvironment", cluster.Definition.Hosting.Environment);

                    values.Add($"metrics.global.enabled", clusterAdvice.MetricsEnabled);
                    values.Add($"metrics.global.scrapeInterval", clusterAdvice.MetricsInterval);
                    values.Add($"metrics.crio.enabled", clusterAdvice.MetricsEnabled);
                    values.Add($"metrics.crio.scrapeInterval", clusterAdvice.MetricsInterval);
                    values.Add($"metrics.istio.enabled", istioAdvice.MetricsEnabled ?? clusterAdvice.MetricsEnabled);
                    values.Add($"metrics.istio.scrapeInterval", istioAdvice.MetricsInterval ?? clusterAdvice.MetricsInterval);
                    values.Add($"metrics.kubelet.enabled", clusterAdvice.MetricsEnabled);
                    values.Add($"metrics.kubelet.scrapeInterval", clusterAdvice.MetricsInterval);
                    values.Add($"metrics.cadvisor.enabled", clusterAdvice.MetricsEnabled);
                    values.Add($"metrics.cadvisor.scrapeInterval", clusterAdvice.MetricsInterval);
                    values.Add($"tracing.enabled", cluster.Definition.Features.Tracing);
                    values.Add("serviceMesh.enabled", cluster.Definition.Features.ServiceMesh);

                    values.Add($"resources.agent.requests.memory", ToSiString(agentAdvice.PodMemoryRequest));
                    values.Add($"resources.agent.limits.memory", ToSiString(agentAdvice.PodMemoryLimit));

                    values.Add($"resources.agentNode.requests.memory", ToSiString(agentNodeAdvice.PodMemoryRequest));
                    values.Add($"resources.agentNode.limits.memory", ToSiString(agentNodeAdvice.PodMemoryLimit));

                    foreach (var taint in await GetTaintsAsync(controller, NodeLabels.LabelMetricsInternal, "true"))
                    {
                        values.Add($"tolerations[{i}].key", $"{taint.Key.Split("=")[0]}");
                        values.Add($"tolerations[{i}].effect", taint.Effect);
                        values.Add($"tolerations[{i}].operator", "Exists");
                        i++;
                    }

                    await master.InstallHelmChartAsync(controller, "grafana-agent",
                        releaseName: "grafana-agent",
                        @namespace: KubeNamespace.NeonMonitor,
                        prioritySpec: PriorityClass.NeonMonitor.Name,
                        values: values);
                });

            controller.ThrowIfCancelled();
            await master.InvokeIdempotentAsync("setup/monitoring-prometheus-blackbox-exporter",
                async () =>
                {
                    controller.LogProgress(master, verb: "setup", message: "prometheus");

                    var values = new Dictionary<string, object>();
                    var i = 0;

                    values.Add($"replicas", blackboxAdvice.ReplicaCount);
                    values.Add($"serviceMesh.enabled", false);
                    values.Add($"resources.requests.memory", ToSiString(blackboxAdvice.PodMemoryRequest));
                    values.Add($"resources.limits.memory", ToSiString(blackboxAdvice.PodMemoryLimit));

                    foreach (var taint in await GetTaintsAsync(controller, NodeLabels.LabelMetricsInternal, "true"))
                    {
                        values.Add($"tolerations[{i}].key", $"{taint.Key.Split("=")[0]}");
                        values.Add($"tolerations[{i}].effect", taint.Effect);
                        values.Add($"tolerations[{i}].operator", "Exists");
                        i++;
                    }

                    await master.InstallHelmChartAsync(controller, "blackbox-exporter",
                        releaseName: "blackbox-exporter",
                        @namespace: KubeNamespace.NeonMonitor,
                        prioritySpec: PriorityClass.NeonMonitor.Name,
                        values: values);
                });
        }

        /// <summary>
        /// Waits for Prometheus to be fully ready.
        /// </summary>
        /// <param name="controller">The setup controller.</param>
        /// <param name="master">The master node where the operation will be performed.</param>
        /// <returns>The tracking <see cref="Task"/>.</returns>
        public static async Task WaitForPrometheusAsync(ISetupController controller, NodeSshProxy<NodeDefinition> master)
        {
            await SyncContext.Clear;
            Covenant.Requires<ArgumentNullException>(controller != null, nameof(controller));
            Covenant.Requires<ArgumentNullException>(master != null, nameof(master));

            var cluster = controller.Get<ClusterProxy>(KubeSetupProperty.ClusterProxy);
            var k8s     = GetK8sClient(controller);

            controller.ThrowIfCancelled();
            await master.InvokeIdempotentAsync("setup/monitoring-grafana-agent-ready",
                async () =>
                {
                    controller.LogProgress(master, verb: "wait for", message: "grafana agent");

                    await NeonHelper.WaitAllAsync(
                        new List<Task>()
                        {
                            k8s.WaitForDeploymentAsync(KubeNamespace.NeonMonitor, "grafana-agent-operator", timeout: clusterOpTimeout, pollInterval: clusterOpPollInterval, cancellationToken: controller.CancellationToken),
                            k8s.WaitForDaemonsetAsync(KubeNamespace.NeonMonitor, "grafana-agent-node", timeout: clusterOpTimeout, pollInterval: clusterOpPollInterval, cancellationToken: controller.CancellationToken),
                            k8s.WaitForStatefulSetAsync(KubeNamespace.NeonMonitor, "grafana-agent", timeout: clusterOpTimeout, pollInterval: clusterOpPollInterval, cancellationToken: controller.CancellationToken),
                        },
                        timeoutMessage:    "setup/monitoring-grafana-agent-ready",
                        cancellationToken: controller.CancellationToken);
                });
        }

        /// <summary>
        /// Installs Memcached to the neon-system namespace.
        /// </summary>
        /// <param name="controller">The setup controller.</param>
        /// <param name="master">The master node where the operation will be performed.</param>
        /// <returns>The tracking <see cref="Task"/>.</returns>
        public static async Task InstallMemcachedAsync(ISetupController controller, NodeSshProxy<NodeDefinition> master)
        {
            await SyncContext.Clear;
            Covenant.Requires<ArgumentNullException>(controller != null, nameof(controller));
            Covenant.Requires<ArgumentNullException>(master != null, nameof(master));

            controller.ThrowIfCancelled();
            var cluster       = controller.Get<ClusterProxy>(KubeSetupProperty.ClusterProxy);
            var k8s           = GetK8sClient(controller);
            var clusterAdvice = controller.Get<KubeClusterAdvice>(KubeSetupProperty.ClusterAdvice);
            var serviceAdvice = clusterAdvice.GetServiceAdvice(KubeClusterAdvice.Memcached);

            var values = new Dictionary<string, object>();

            controller.ThrowIfCancelled();
            await master.InvokeIdempotentAsync("setup/memcached",
                async () =>
                {
                    controller.LogProgress(master, verb: "setup", message: "memcached");

                    values.Add($"replicas", serviceAdvice.ReplicaCount);
                    values.Add($"metrics.serviceMonitor.enabled", serviceAdvice.MetricsEnabled ?? clusterAdvice.MetricsEnabled);
                    values.Add($"metrics.serviceMonitor.interval", serviceAdvice.MetricsInterval ?? clusterAdvice.MetricsInterval);
                    values.Add($"serviceMesh.enabled", cluster.Definition.Features.ServiceMesh);
                    values.Add($"resources.requests.memory", ToSiString(serviceAdvice.PodMemoryRequest));
                    values.Add($"resources.limits.memory", ToSiString(serviceAdvice.PodMemoryLimit));
                    values.Add($"server.memory", Decimal.ToInt32(serviceAdvice.PodMemoryLimit.Value / 1200000));

                    int i = 0;

                    foreach (var taint in await GetTaintsAsync(controller, NodeLabels.LabelRole, "worker"))
                    {
                        values.Add($"tolerations[{i}].key", $"{taint.Key.Split("=")[0]}");
                        values.Add($"tolerations[{i}].effect", taint.Effect);
                        values.Add($"tolerations[{i}].operator", "Exists");
                        i++;
                    }

                    values.Add("image.organization", KubeConst.LocalClusterRegistry);

                    await master.InstallHelmChartAsync(controller, "memcached",
                        releaseName: "neon-memcached",
                        @namespace: KubeNamespace.NeonSystem,
                        prioritySpec: PriorityClass.NeonMonitor.Name,
                        values: values);
                });

            controller.ThrowIfCancelled();
            await master.InvokeIdempotentAsync("setup/memcached-ready",
                async () =>
                {
                    controller.LogProgress(master, verb: "wait for", message: "memcached");

                    await k8s.WaitForStatefulSetAsync(KubeNamespace.NeonSystem, "neon-memcached", timeout: clusterOpTimeout, pollInterval: clusterOpPollInterval, cancellationToken: controller.CancellationToken);
                });
        }

        /// <summary>
        /// Installs Mimir to the monitoring namespace.
        /// </summary>
        /// <param name="controller">The setup controller.</param>
        /// <param name="master">The master node where the operation will be performed.</param>
        /// <returns>The tracking <see cref="Task"/>.</returns>
        public static async Task InstallMimirAsync(ISetupController controller, NodeSshProxy<NodeDefinition> master)
        {
            await SyncContext.Clear;
            Covenant.Requires<ArgumentNullException>(controller != null, nameof(controller));
            Covenant.Requires<ArgumentNullException>(master != null, nameof(master));

            await CreateHostPathStorageClass(controller, master, "neon-internal-mimir");

            controller.ThrowIfCancelled();
            await master.InvokeIdempotentAsync("setup/monitoring-mimir-all",
                async () =>
                {
                    var cluster             = controller.Get<ClusterProxy>(KubeSetupProperty.ClusterProxy);
                    var k8s                 = GetK8sClient(controller);
                    var clusterAdvice       = controller.Get<KubeClusterAdvice>(KubeSetupProperty.ClusterAdvice);
                    var mimirAdvice         = clusterAdvice.GetServiceAdvice(KubeClusterAdvice.Mimir);
                    var alertmanagerAdvice  = clusterAdvice.GetServiceAdvice(KubeClusterAdvice.MimirAlertmanager);
                    var compactorAdvice     = clusterAdvice.GetServiceAdvice(KubeClusterAdvice.MimirCompactor);
                    var distributorAdvice   = clusterAdvice.GetServiceAdvice(KubeClusterAdvice.MimirDistributor);
                    var ingesterAdvice      = clusterAdvice.GetServiceAdvice(KubeClusterAdvice.MimirIngester);
                    var overridesAdvice     = clusterAdvice.GetServiceAdvice(KubeClusterAdvice.MimirOverridesExporter);
                    var querierAdvice       = clusterAdvice.GetServiceAdvice(KubeClusterAdvice.MimirQuerier);
                    var queryFrontendAdvice = clusterAdvice.GetServiceAdvice(KubeClusterAdvice.MimirQueryFrontend);
                    var rulerAdvice         = clusterAdvice.GetServiceAdvice(KubeClusterAdvice.MimirRuler);
                    var storeGatewayAdvice  = clusterAdvice.GetServiceAdvice(KubeClusterAdvice.MimirStoreGateway);
                    
                    var values        = new Dictionary<string, object>();

                    values.Add("cluster.name", cluster.Definition.Name);
                    values.Add("cluster.domain", cluster.Definition.Domain);

                    values.Add($"alertmanager.replicas", alertmanagerAdvice.ReplicaCount);
                    values.Add($"alertmanager.resources.requests.memory", ToSiString(alertmanagerAdvice.PodMemoryRequest));
                    values.Add($"alertmanager.resources.limits.memory", ToSiString(alertmanagerAdvice.PodMemoryLimit));
                    values.Add($"alertmanager.priorityClassName", PriorityClass.NeonMonitor.Name);

                    values.Add($"compactor.replicas", compactorAdvice.ReplicaCount);
                    values.Add($"compactor.resources.requests.memory", ToSiString(compactorAdvice.PodMemoryRequest));
                    values.Add($"compactor.resources.limits.memory", ToSiString(compactorAdvice.PodMemoryLimit));
                    values.Add($"compactor.priorityClassName", PriorityClass.NeonMonitor.Name);

                    values.Add($"distributor.replicas", distributorAdvice.ReplicaCount);
                    values.Add($"distributor.resources.requests.memory", ToSiString(distributorAdvice.PodMemoryRequest));
                    values.Add($"distributor.resources.limits.memory", ToSiString(distributorAdvice.PodMemoryLimit));
                    values.Add($"distributor.priorityClassName", PriorityClass.NeonMonitor.Name);

                    values.Add($"ingester.replicas", ingesterAdvice.ReplicaCount);
                    values.Add($"ingester.resources.requests.memory", ToSiString(ingesterAdvice.PodMemoryRequest));
                    values.Add($"ingester.resources.limits.memory", ToSiString(ingesterAdvice.PodMemoryLimit));
                    values.Add($"ingester.priorityClassName", PriorityClass.NeonMonitor.Name);

                    values.Add($"overrides_exporter.replicas", overridesAdvice.ReplicaCount);
                    values.Add($"overrides_exporter.resources.requests.memory", ToSiString(overridesAdvice.PodMemoryRequest));
                    values.Add($"overrides_exporter.resources.limits.memory", ToSiString(overridesAdvice.PodMemoryLimit));
                    values.Add($"overrides_exporter.priorityClassName", PriorityClass.NeonMonitor.Name);

                    values.Add($"querier.replicas", querierAdvice.ReplicaCount);
                    values.Add($"querier.resources.requests.memory", ToSiString(querierAdvice.PodMemoryRequest));
                    values.Add($"querier.resources.limits.memory", ToSiString(querierAdvice.PodMemoryLimit));
                    values.Add($"querier.priorityClassName", PriorityClass.NeonMonitor.Name);

                    values.Add($"query_frontend.replicas", queryFrontendAdvice.ReplicaCount);
                    values.Add($"query_frontend.resources.requests.memory", ToSiString(queryFrontendAdvice.PodMemoryRequest));
                    values.Add($"query_frontend.resources.limits.memory", ToSiString(queryFrontendAdvice.PodMemoryLimit));
                    values.Add($"query_frontend.priorityClassName", PriorityClass.NeonMonitor.Name);

                    values.Add($"ruler.replicas", rulerAdvice.ReplicaCount);
                    values.Add($"ruler.resources.requests.memory", ToSiString(rulerAdvice.PodMemoryRequest));
                    values.Add($"ruler.resources.limits.memory", ToSiString(rulerAdvice.PodMemoryLimit));
                    values.Add($"ruler.priorityClassName", PriorityClass.NeonMonitor.Name);

                    values.Add($"store_gateway.replicas", storeGatewayAdvice.ReplicaCount);
                    values.Add($"store_gateway.resources.requests.memory", ToSiString(storeGatewayAdvice.PodMemoryRequest));
                    values.Add($"store_gateway.resources.limits.memory", ToSiString(storeGatewayAdvice.PodMemoryLimit));
                    values.Add($"store_gateway.priorityClassName", PriorityClass.NeonMonitor.Name);

                    values.Add($"serviceMonitor.enabled", mimirAdvice.MetricsEnabled ?? clusterAdvice.MetricsEnabled);
                    values.Add($"serviceMonitor.interval", mimirAdvice.MetricsInterval ?? clusterAdvice.MetricsInterval);
                    values.Add($"serviceMesh.enabled", cluster.Definition.Features.ServiceMesh);
                    values.Add($"tracing.enabled", cluster.Definition.Features.Tracing);
                    values.Add($"minio.enabled", true);

                    if (cluster.Definition.Nodes.Where(node => node.Labels.MetricsInternal).Count() == 1)
                    {
                        values.Add($"blocksStorage.tsdb.block_ranges_period[0]", "1h0m0s");
                        values.Add($"blocksStorage.tsdb.retention_period", "2h0m0s");
                        values.Add($"limits.compactor_blocks_retention_period", "12h");
                    }

                    await CreateMinioBucketAsync(controller, master, KubeMinioBucket.Mimir, clusterAdvice.MetricsQuota);
                    await CreateMinioBucketAsync(controller, master, KubeMinioBucket.MimirRuler);

                    controller.ThrowIfCancelled();
                    await master.InvokeIdempotentAsync("setup/monitoring-mimir-secret",
                        async () =>
                        {

                            var dbSecret = await k8s.ReadNamespacedSecretAsync(KubeConst.NeonSystemDbServiceSecret, KubeNamespace.NeonSystem);

                            var citusSecret = new V1Secret()
                            {
                                Metadata = new V1ObjectMeta()
                                {
                                    Name              = KubeConst.CitusSecretKey,
                                    NamespaceProperty = KubeNamespace.NeonMonitor
                                },
                                Data       = new Dictionary<string, byte[]>(),
                                StringData = new Dictionary<string, string>()
                            };

                            citusSecret.Data["username"] = dbSecret.Data["username"];
                            citusSecret.Data["password"] = dbSecret.Data["password"];

                            await k8s.UpsertSecretAsync(citusSecret, KubeNamespace.NeonMonitor);
                        }
                        );

                    int i = 0;

                    foreach (var taint in await GetTaintsAsync(controller, NodeLabels.LabelMetricsInternal, "true"))
                    {
                        foreach (var component in new string[]
                        {
                            "alertmanager", "distributor", "ingester", "overrides_exporter", "ruler", "querier", "query_frontend", "store_gateway", "compactor"
                        })
                        {
                            values.Add($"{component}.tolerations[{i}].key", $"{taint.Key.Split("=")[0]}");
                            values.Add($"{component}.tolerations[{i}].effect", taint.Effect);
                            values.Add($"{component}.tolerations[{i}].operator", "Exists");
                        }
                        i++;
                    }

                    values.Add("image.organization", KubeConst.LocalClusterRegistry);

                    await master.InstallHelmChartAsync(controller, "mimir",
                        releaseName: "mimir",
                        @namespace:   KubeNamespace.NeonMonitor,
                        values:       values);

                    controller.ThrowIfCancelled();
                    await master.InvokeIdempotentAsync("setup/monitoring-mimir-ready",
                        async () =>
                        {
                            controller.LogProgress(master, verb: "wait for", message: "mimir");

                            await k8s.WaitForStatefulSetAsync(KubeNamespace.NeonMonitor, "mimir-alertmanager", timeout: clusterOpTimeout, pollInterval: clusterOpPollInterval, cancellationToken: controller.CancellationToken);
                            await k8s.WaitForStatefulSetAsync(KubeNamespace.NeonMonitor, "mimir-compactor", timeout: clusterOpTimeout, pollInterval: clusterOpPollInterval, cancellationToken: controller.CancellationToken);
                            await k8s.WaitForDeploymentAsync(KubeNamespace.NeonMonitor, "mimir-distributor", timeout: clusterOpTimeout, pollInterval: clusterOpPollInterval, cancellationToken: controller.CancellationToken);
                            await k8s.WaitForStatefulSetAsync(KubeNamespace.NeonMonitor, "mimir-ingester", timeout: clusterOpTimeout, pollInterval: clusterOpPollInterval, cancellationToken: controller.CancellationToken);
                            await k8s.WaitForDeploymentAsync(KubeNamespace.NeonMonitor, "mimir-overrides-exporter", timeout: clusterOpTimeout, pollInterval: clusterOpPollInterval, cancellationToken: controller.CancellationToken);
                            await k8s.WaitForDeploymentAsync(KubeNamespace.NeonMonitor, "mimir-querier", timeout: clusterOpTimeout, pollInterval: clusterOpPollInterval, cancellationToken: controller.CancellationToken);
                            await k8s.WaitForDeploymentAsync(KubeNamespace.NeonMonitor, "mimir-query-frontend", timeout: clusterOpTimeout, pollInterval: clusterOpPollInterval, cancellationToken: controller.CancellationToken);
                            await k8s.WaitForDeploymentAsync(KubeNamespace.NeonMonitor, "mimir-ruler", timeout: clusterOpTimeout, pollInterval: clusterOpPollInterval, cancellationToken: controller.CancellationToken);
                            await k8s.WaitForStatefulSetAsync(KubeNamespace.NeonMonitor, "mimir-store-gateway", timeout: clusterOpTimeout, pollInterval: clusterOpPollInterval, cancellationToken: controller.CancellationToken);
                        });
                });
        }

        /// <summary>
        /// Installs Loki to the monitoring namespace.
        /// </summary>
        /// <param name="controller">The setup controller.</param>
        /// <param name="master">The master node where the operation will be performed.</param>
        /// <returns>The tracking <see cref="Task"/>.</returns>
        public static async Task InstallLokiAsync(ISetupController controller, NodeSshProxy<NodeDefinition> master)
        {
            await SyncContext.Clear;
            Covenant.Requires<ArgumentNullException>(controller != null, nameof(controller));
            Covenant.Requires<ArgumentNullException>(master != null, nameof(master));

            var cluster             = controller.Get<ClusterProxy>(KubeSetupProperty.ClusterProxy);
            var k8s                 = GetK8sClient(controller);
            var clusterAdvice       = controller.Get<KubeClusterAdvice>(KubeSetupProperty.ClusterAdvice);
            var lokiAdvice          = clusterAdvice.GetServiceAdvice(KubeClusterAdvice.Loki);
            var compactorAdvice     = clusterAdvice.GetServiceAdvice(KubeClusterAdvice.LokiCompactor);
            var distributorAdvice   = clusterAdvice.GetServiceAdvice(KubeClusterAdvice.LokiDistributor);
            var ingesterAdvice      = clusterAdvice.GetServiceAdvice(KubeClusterAdvice.LokiIngester);
            var indexGatewayAdvice  = clusterAdvice.GetServiceAdvice(KubeClusterAdvice.LokiIndexGateway);
            var querierAdvice       = clusterAdvice.GetServiceAdvice(KubeClusterAdvice.LokiQuerier);
            var queryFrontendAdvice = clusterAdvice.GetServiceAdvice(KubeClusterAdvice.LokiQueryFrontend);
            var rulerAdvice         = clusterAdvice.GetServiceAdvice(KubeClusterAdvice.LokiRuler);
            var tableManagerAdvice  = clusterAdvice.GetServiceAdvice(KubeClusterAdvice.LokiTableManager);

            await CreateHostPathStorageClass(controller, master, "neon-internal-loki");

            controller.ThrowIfCancelled();
            await master.InvokeIdempotentAsync("setup/monitoring-loki",
                async () =>
                {
                    controller.LogProgress(master, verb: "setup", message: "loki");

                    var values = new Dictionary<string, object>();

                    values.Add("cluster.name", cluster.Definition.Name);
                    values.Add("cluster.domain", cluster.Definition.Domain);

                    values.Add($"compactor.replicas", compactorAdvice.ReplicaCount);
                    values.Add($"compactor.resources.requests.memory", ToSiString(compactorAdvice.PodMemoryRequest));
                    values.Add($"compactor.resources.limits.memory", ToSiString(compactorAdvice.PodMemoryLimit));
                    values.Add($"compactor.priorityClassName", PriorityClass.NeonMonitor.Name);

                    values.Add($"distributor.replicas", distributorAdvice.ReplicaCount);
                    values.Add($"distributor.resources.requests.memory", ToSiString(distributorAdvice.PodMemoryRequest));
                    values.Add($"distributor.resources.limits.memory", ToSiString(distributorAdvice.PodMemoryLimit));
                    values.Add($"distributor.priorityClassName", PriorityClass.NeonMonitor.Name);

                    values.Add($"indexGateway.replicas", indexGatewayAdvice.ReplicaCount);
                    values.Add($"indexGateway.resources.requests.memory", ToSiString(indexGatewayAdvice.PodMemoryRequest));
                    values.Add($"indexGateway.resources.limits.memory", ToSiString(indexGatewayAdvice.PodMemoryLimit));
                    values.Add($"indexGateway.priorityClassName", PriorityClass.NeonMonitor.Name);

                    values.Add($"ingester.replicas", ingesterAdvice.ReplicaCount);
                    values.Add($"ingester.resources.requests.memory", ToSiString(ingesterAdvice.PodMemoryRequest));
                    values.Add($"ingester.resources.limits.memory", ToSiString(ingesterAdvice.PodMemoryLimit));
                    values.Add($"ingester.priorityClassName", PriorityClass.NeonMonitor.Name);

                    values.Add($"querier.replicas", querierAdvice.ReplicaCount);
                    values.Add($"querier.resources.requests.memory", ToSiString(querierAdvice.PodMemoryRequest));
                    values.Add($"querier.resources.limits.memory", ToSiString(querierAdvice.PodMemoryLimit));
                    values.Add($"querier.priorityClassName", PriorityClass.NeonMonitor.Name);

                    values.Add($"queryFrontend.replicas", queryFrontendAdvice.ReplicaCount);
                    values.Add($"queryFrontend.resources.requests.memory", ToSiString(queryFrontendAdvice.PodMemoryRequest));
                    values.Add($"queryFrontend.resources.limits.memory", ToSiString(queryFrontendAdvice.PodMemoryLimit));
                    values.Add($"queryFrontend.priorityClassName", PriorityClass.NeonMonitor.Name);

                    values.Add($"ruler.replicas", rulerAdvice.ReplicaCount);
                    values.Add($"ruler.resources.requests.memory", ToSiString(rulerAdvice.PodMemoryRequest));
                    values.Add($"ruler.resources.limits.memory", ToSiString(rulerAdvice.PodMemoryLimit));
                    values.Add($"ruler.priorityClassName", PriorityClass.NeonMonitor.Name);

                    values.Add($"tableManager.replicas", tableManagerAdvice.ReplicaCount);
                    values.Add($"tableManager.resources.requests.memory", ToSiString(tableManagerAdvice.PodMemoryRequest));
                    values.Add($"tableManager.resources.limits.memory", ToSiString(tableManagerAdvice.PodMemoryLimit));
                    values.Add($"tableManager.priorityClassName", PriorityClass.NeonMonitor.Name);

                    values.Add($"serviceMonitor.enabled", lokiAdvice.MetricsEnabled ?? clusterAdvice.MetricsEnabled);
                    values.Add($"serviceMonitor.interval", lokiAdvice.MetricsInterval ?? clusterAdvice.MetricsInterval);
                    values.Add($"serviceMesh.enabled", cluster.Definition.Features.ServiceMesh);
                    values.Add($"tracing.enabled", cluster.Definition.Features.Tracing);

                    values.Add($"minio.enabled", true);

                    if (cluster.Definition.Nodes.Where(node => node.Labels.LogsInternal).Count() >= 3)
                    {
                        values.Add($"config.replication_factor", 3);
                    }

                    await CreateMinioBucketAsync(controller, master, KubeMinioBucket.Loki, clusterAdvice.LogsQuota);
                    values.Add($"loki.schemaConfig.configs[0].object_store", "aws");
                    values.Add($"loki.storageConfig.boltdb_shipper.shared_store", "s3");

                    if (cluster.Definition.IsDesktopBuiltIn || cluster.Definition.Nodes.Count() == 1)
                    {
                        values.Add($"loki.storageConfig.boltdb_shipper.cache_ttl", "24h");
                        values.Add($"limits_config.retention_period", "24h");
                        values.Add($"limits_config.reject_old_samples_max_age", "6h");
                        values.Add($"table_manager.retention_period", "24h");
                    }

                    int i = 0;

                    foreach (var taint in await GetTaintsAsync(controller, NodeLabels.LabelLogsInternal, "true"))
                    {
                        foreach (var component in new string[]
                        {
                            "ingester", "distributor", "querier", "queryFrontend", "tableManager", "compactor", "ruler", "indexGateway"
                        })
                        {
                            values.Add($"{component}.tolerations[{i}].key", $"{taint.Key.Split("=")[0]}");
                            values.Add($"{component}.tolerations[{i}].effect", taint.Effect);
                            values.Add($"{component}.tolerations[{i}].operator", "Exists");
                        }
                        i++;
                    }

                    await master.InstallHelmChartAsync(controller, "loki",
                        releaseName:  "loki",
                        @namespace:   KubeNamespace.NeonMonitor,
                        prioritySpec: PriorityClass.NeonMonitor.Name,
                        values:       values);
                });

            controller.ThrowIfCancelled();
            await master.InvokeIdempotentAsync("setup/monitoring-loki-ready",
                async () =>
                {
                    controller.LogProgress(master, verb: "wait for", message: "loki");

                    await k8s.WaitForDeploymentAsync(KubeNamespace.NeonMonitor, "loki-compactor", timeout: clusterOpTimeout, pollInterval: clusterOpPollInterval, cancellationToken: controller.CancellationToken);
                    await k8s.WaitForDeploymentAsync(KubeNamespace.NeonMonitor, "loki-distributor", timeout: clusterOpTimeout, pollInterval: clusterOpPollInterval, cancellationToken: controller.CancellationToken);
                    await k8s.WaitForStatefulSetAsync(KubeNamespace.NeonMonitor, "loki-index-gateway", timeout: clusterOpTimeout, pollInterval: clusterOpPollInterval, cancellationToken: controller.CancellationToken);
                    await k8s.WaitForStatefulSetAsync(KubeNamespace.NeonMonitor, "loki-ingester", timeout: clusterOpTimeout, pollInterval: clusterOpPollInterval, cancellationToken: controller.CancellationToken);
                    await k8s.WaitForDeploymentAsync(KubeNamespace.NeonMonitor, "loki-querier", timeout: clusterOpTimeout, pollInterval: clusterOpPollInterval, cancellationToken: controller.CancellationToken);
                    await k8s.WaitForDeploymentAsync(KubeNamespace.NeonMonitor, "loki-query-frontend", timeout: clusterOpTimeout, pollInterval: clusterOpPollInterval, cancellationToken: controller.CancellationToken);
                    await k8s.WaitForDeploymentAsync(KubeNamespace.NeonMonitor, "loki-ruler", timeout: clusterOpTimeout, pollInterval: clusterOpPollInterval, cancellationToken: controller.CancellationToken);
                    await k8s.WaitForDeploymentAsync(KubeNamespace.NeonMonitor, "loki-table-manager", timeout: clusterOpTimeout, pollInterval: clusterOpPollInterval, cancellationToken: controller.CancellationToken);
                });
        }

        /// <summary>
        /// Installs Tempo to the monitoring namespace.
        /// </summary>
        /// <param name="controller">The setup controller.</param>
        /// <param name="master">The master node where the operation will be performed.</param>
        /// <returns>The tracking <see cref="Task"/>.</returns>
        public static async Task InstallTempoAsync(ISetupController controller, NodeSshProxy<NodeDefinition> master)
        {
            await SyncContext.Clear;
            Covenant.Requires<ArgumentNullException>(controller != null, nameof(controller));
            Covenant.Requires<ArgumentNullException>(master != null, nameof(master));
            
            var cluster             = controller.Get<ClusterProxy>(KubeSetupProperty.ClusterProxy);
            var k8s                 = GetK8sClient(controller);
            var clusterAdvice       = controller.Get<KubeClusterAdvice>(KubeSetupProperty.ClusterAdvice);
            var tempoAdvice         = clusterAdvice.GetServiceAdvice(KubeClusterAdvice.Tempo);
            var compactorAdvice     = clusterAdvice.GetServiceAdvice(KubeClusterAdvice.TempoCompactor);
            var distributorAdvice   = clusterAdvice.GetServiceAdvice(KubeClusterAdvice.TempoDistributor);
            var ingesterAdvice      = clusterAdvice.GetServiceAdvice(KubeClusterAdvice.TempoIngester);
            var querierAdvice       = clusterAdvice.GetServiceAdvice(KubeClusterAdvice.TempoQuerier);
            var queryFrontendAdvice = clusterAdvice.GetServiceAdvice(KubeClusterAdvice.TempoQueryFrontend);

            await CreateHostPathStorageClass(controller, master, "neon-internal-tempo");

            controller.ThrowIfCancelled();
            await master.InvokeIdempotentAsync("setup/monitoring-tempo",
                async () =>
                {
                    controller.LogProgress(master, verb: "setup", message: "tempo");

                    var values = new Dictionary<string, object>();

                    values.Add("cluster.name", cluster.Definition.Name);
                    values.Add("cluster.domain", cluster.Definition.Domain);

                    values.Add($"compactor.replicas", compactorAdvice.ReplicaCount);
                    values.Add($"compactor.resources.requests.memory", ToSiString(compactorAdvice.PodMemoryRequest));
                    values.Add($"compactor.resources.limits.memory", ToSiString(compactorAdvice.PodMemoryLimit));
                    values.Add($"compactor.priorityClassName", PriorityClass.NeonMonitor.Name);

                    values.Add($"distributor.replicas", distributorAdvice.ReplicaCount);
                    values.Add($"distributor.resources.requests.memory", ToSiString(distributorAdvice.PodMemoryRequest));
                    values.Add($"distributor.resources.limits.memory", ToSiString(distributorAdvice.PodMemoryLimit));
                    values.Add($"distributor.priorityClassName", PriorityClass.NeonMonitor.Name);

                    values.Add($"ingester.replicas", ingesterAdvice.ReplicaCount);
                    values.Add($"ingester.resources.requests.memory", ToSiString(ingesterAdvice.PodMemoryRequest));
                    values.Add($"ingester.resources.limits.memory", ToSiString(ingesterAdvice.PodMemoryLimit));
                    values.Add($"ingester.priorityClassName", PriorityClass.NeonMonitor.Name);

                    values.Add($"querier.replicas", querierAdvice.ReplicaCount);
                    values.Add($"querier.resources.requests.memory", ToSiString(querierAdvice.PodMemoryRequest));
                    values.Add($"querier.resources.limits.memory", ToSiString(querierAdvice.PodMemoryLimit));
                    values.Add($"querier.priorityClassName", PriorityClass.NeonMonitor.Name);

                    values.Add($"queryFrontend.replicas", queryFrontendAdvice.ReplicaCount);
                    values.Add($"queryFrontend.resources.requests.memory", ToSiString(queryFrontendAdvice.PodMemoryRequest));
                    values.Add($"queryFrontend.resources.limits.memory", ToSiString(queryFrontendAdvice.PodMemoryLimit));
                    values.Add($"queryFrontend.priorityClassName", PriorityClass.NeonMonitor.Name);

                    values.Add($"serviceMonitor.enabled", tempoAdvice.MetricsEnabled ?? clusterAdvice.MetricsEnabled);
                    values.Add($"serviceMonitor.interval", tempoAdvice.MetricsInterval ?? clusterAdvice.MetricsInterval);
                    values.Add($"serviceMesh.enabled", cluster.Definition.Features.ServiceMesh);
                    values.Add($"tracing.enabled", cluster.Definition.Features.Tracing);

                    if (cluster.Definition.Nodes.Where(node => node.Labels.MetricsInternal).Count() > 1) {
                        values.Add($"storage.trace.backend", "s3");
                    }

                    values.Add($"minio.enabled", true);

                    await CreateMinioBucketAsync(controller, master, KubeMinioBucket.Tempo, clusterAdvice.TracesQuota);

                    int i = 0;

                    foreach (var taint in await GetTaintsAsync(controller, NodeLabels.LabelTracesInternal, "true"))
                    {
                        foreach (var component in new string[]
                        {
                            "ingester", "distributor", "compactor", "querier", "queryFrontend"
                        })
                        {
                            values.Add($"{component}.tolerations[{i}].key", $"{taint.Key.Split("=")[0]}");
                            values.Add($"{component}.tolerations[{i}].effect", taint.Effect);
                            values.Add($"{component}.tolerations[{i}].operator", "Exists");
                        }
                        i++;
                    }

                    await master.InstallHelmChartAsync(controller, "tempo",
                        releaseName: "tempo",
                        @namespace:   KubeNamespace.NeonMonitor,
                        prioritySpec: PriorityClass.NeonMonitor.Name,
                        values:       values);
                });

            controller.ThrowIfCancelled();
            await master.InvokeIdempotentAsync("setup/monitoring-tempo-ready",
                async () =>
                {
                    controller.LogProgress(master, verb: "wait for", message: "tempo");

                    await k8s.WaitForDeploymentAsync(KubeNamespace.NeonMonitor, "tempo-compactor", timeout: clusterOpTimeout, pollInterval: clusterOpPollInterval, cancellationToken: controller.CancellationToken);
                    await k8s.WaitForDeploymentAsync(KubeNamespace.NeonMonitor, "tempo-distributor", timeout: clusterOpTimeout, pollInterval: clusterOpPollInterval, cancellationToken: controller.CancellationToken);
                    await k8s.WaitForStatefulSetAsync(KubeNamespace.NeonMonitor, "tempo-ingester", timeout: clusterOpTimeout, pollInterval: clusterOpPollInterval, cancellationToken: controller.CancellationToken);
                    await k8s.WaitForDeploymentAsync(KubeNamespace.NeonMonitor, "tempo-querier", timeout: clusterOpTimeout, pollInterval: clusterOpPollInterval, cancellationToken: controller.CancellationToken);
                    await k8s.WaitForDeploymentAsync(KubeNamespace.NeonMonitor, "tempo-query-frontend", timeout: clusterOpTimeout, pollInterval: clusterOpPollInterval, cancellationToken: controller.CancellationToken);
                });
        }

        /// <summary>
        /// Installs Kube State Metrics to the monitoring namespace.
        /// </summary>
        /// <param name="controller">The setup controller.</param>
        /// <param name="master">The master node where the operation will be performed.</param>
        /// <returns>The tracking <see cref="Task"/>.</returns>
        public static async Task InstallKubeStateMetricsAsync(ISetupController controller, NodeSshProxy<NodeDefinition> master)
        {
            await SyncContext.Clear;
            Covenant.Requires<ArgumentNullException>(controller != null, nameof(controller));
            Covenant.Requires<ArgumentNullException>(master != null, nameof(master));

            var cluster       = controller.Get<ClusterProxy>(KubeSetupProperty.ClusterProxy);
            var k8s           = GetK8sClient(controller);
            var clusterAdvice = controller.Get<KubeClusterAdvice>(KubeSetupProperty.ClusterAdvice);
            var serviceAdvice = clusterAdvice.GetServiceAdvice(KubeClusterAdvice.KubeStateMetrics);

            controller.ThrowIfCancelled();
            await master.InvokeIdempotentAsync("setup/monitoring-kube-state-metrics",
                async () =>
                {
                    controller.LogProgress(master, verb: "deploy", message: "kube-state-metrics");

                    var values = new Dictionary<string, object>();

                    values.Add($"prometheus.monitor.enabled", serviceAdvice.MetricsEnabled ?? clusterAdvice.MetricsEnabled);
                    values.Add($"prometheus.monitor.interval", serviceAdvice.MetricsInterval ?? clusterAdvice.MetricsInterval);
                    values.Add("serviceMesh.enabled", cluster.Definition.Features.ServiceMesh);

                    int i = 0;
                    foreach (var taint in await GetTaintsAsync(controller, NodeLabels.LabelMetricsInternal, "true"))
                    {
                        values.Add($"tolerations[{i}].key", $"{taint.Key.Split("=")[0]}");
                        values.Add($"tolerations[{i}].effect", taint.Effect);
                        values.Add($"tolerations[{i}].operator", "Exists");
                        i++;
                    }

                    await master.InstallHelmChartAsync(controller, "kube-state-metrics",
                        releaseName:  "kube-state-metrics",
                        @namespace:   KubeNamespace.NeonMonitor,
                        prioritySpec: PriorityClass.NeonMonitor.Name,
                        values:       values);
                });

            controller.ThrowIfCancelled();
            await master.InvokeIdempotentAsync("setup/monitoring-kube-state-metrics-ready",
                async () =>
                {
                    controller.LogProgress(master, verb: "wait for", message: "kube-state-metrics");

                    await k8s.WaitForStatefulSetAsync(KubeNamespace.NeonMonitor, "kube-state-metrics", timeout: clusterOpTimeout, pollInterval: clusterOpPollInterval, cancellationToken: controller.CancellationToken);
                });
        }

        /// <summary>
        /// Installs Reloader to the Neon system nnamespace.
        /// </summary>
        /// <param name="controller">The setup controller.</param>
        /// <param name="master">The master node where the operation will be performed.</param>
        /// <returns>The tracking <see cref="Task"/>.</returns>
        public static async Task InstallReloaderAsync(ISetupController controller, NodeSshProxy<NodeDefinition> master)
        {
            await SyncContext.Clear;
            Covenant.Requires<ArgumentNullException>(controller != null, nameof(controller));
            Covenant.Requires<ArgumentNullException>(master != null, nameof(master));

            var cluster       = controller.Get<ClusterProxy>(KubeSetupProperty.ClusterProxy);
            var k8s           = GetK8sClient(controller);
            var clusterAdvice = controller.Get<KubeClusterAdvice>(KubeSetupProperty.ClusterAdvice);
            var serviceAdvice = clusterAdvice.GetServiceAdvice(KubeClusterAdvice.Reloader);

            controller.ThrowIfCancelled();
            await master.InvokeIdempotentAsync("setup/reloader",
                async () =>
                {
                    controller.LogProgress(master, verb: "setup", message: "reloader");

                    var values = new Dictionary<string, object>();

                    values.Add($"reloader.serviceMonitor.interval", serviceAdvice.MetricsInterval ?? clusterAdvice.MetricsInterval);
                    values.Add($"reloader.serviceMonitor.enabled", serviceAdvice.MetricsEnabled ?? clusterAdvice.MetricsEnabled);
                    values.Add("serviceMesh.enabled", cluster.Definition.Features.ServiceMesh);

                    await master.InstallHelmChartAsync(controller, "reloader",
                        releaseName:  "reloader",
                        @namespace:   KubeNamespace.NeonSystem,
                        prioritySpec: $"reloader.deployment.priorityClassName={PriorityClass.NeonOperator.Name}",
                        values:       values);
                });

            controller.ThrowIfCancelled();
            await master.InvokeIdempotentAsync("setup/reloader-ready",
                async () =>
                {
                    controller.LogProgress(master, verb: "wait for", message: "reloader");

                    await k8s.WaitForDeploymentAsync(KubeNamespace.NeonSystem, "reloader", timeout: clusterOpTimeout, pollInterval: clusterOpPollInterval, cancellationToken: controller.CancellationToken);
                });
        }

        /// <summary>
        /// Installs Grafana to the monitoring namespace.
        /// </summary>
        /// <param name="controller">The setup controller.</param>
        /// <param name="master">The master node where the operation will be performed.</param>
        /// <returns>The tracking <see cref="Task"/>.</returns>
        public static async Task InstallGrafanaAsync(ISetupController controller, NodeSshProxy<NodeDefinition> master)
        {
            await SyncContext.Clear;
            Covenant.Requires<ArgumentNullException>(controller != null, nameof(controller));
            Covenant.Requires<ArgumentNullException>(master != null, nameof(master));

            var cluster       = controller.Get<ClusterProxy>(KubeSetupProperty.ClusterProxy);
            var k8s           = GetK8sClient(controller);
            var clusterAdvice = controller.Get<KubeClusterAdvice>(KubeSetupProperty.ClusterAdvice);
            var serviceAdvice = clusterAdvice.GetServiceAdvice(KubeClusterAdvice.Grafana);

            controller.ThrowIfCancelled();
            await master.InvokeIdempotentAsync("setup/monitoring-grafana",
                async () =>
                {
                    controller.LogProgress(master, verb: "setup", message: "grafana");

                    var values = new Dictionary<string, object>();

                    values.Add("cluster.name", cluster.Definition.Name);
                    values.Add("cluster.domain", cluster.Definition.Domain);
                    values.Add($"cluster.datacenter", cluster.Definition.Datacenter);
                    values.Add($"cluster.version", cluster.Definition.ClusterVersion);
                    values.Add($"cluster.hostingEnvironment", cluster.Definition.Hosting.Environment);
                    values.Add("neonkube.clusterDomain.grafana", ClusterDomain.Grafana);
                    values.Add("neonkube.clusterDomain.sso", ClusterDomain.Sso);
                    values.Add($"serviceMonitor.enabled", serviceAdvice.MetricsEnabled ?? clusterAdvice.MetricsEnabled);
                    values.Add($"serviceMonitor.interval", serviceAdvice.MetricsInterval ?? clusterAdvice.MetricsInterval);
                    values.Add($"tracing.enabled", cluster.Definition.Features.Tracing);
                    values.Add("serviceMesh.enabled", cluster.Definition.Features.ServiceMesh);

                    controller.ThrowIfCancelled();
                    await master.InvokeIdempotentAsync("setup/db-credentials-grafana",
                        async () =>
                        {
                            var secret    = await k8s.ReadNamespacedSecretAsync(KubeConst.NeonSystemDbServiceSecret, KubeNamespace.NeonSystem);
                            var dexSecret = await k8s.ReadNamespacedSecretAsync(KubeConst.DexSecret, KubeNamespace.NeonSystem);

                            var monitorSecret = new V1Secret()
                            {
                                Metadata = new V1ObjectMeta()
                                {
                                    Name        = KubeConst.GrafanaSecret,
                                    Annotations = new Dictionary<string, string>()
                                    {
                                        {  "reloader.stakater.com/match", "true" }
                                    }
                                },
                                Type = "Opaque",
                                Data = new Dictionary<string, byte[]>()
                                {
                                    { "DATABASE_PASSWORD", secret.Data["password"] },
                                    { "CLIENT_ID", Encoding.UTF8.GetBytes("grafana") },
                                    { "CLIENT_SECRET", dexSecret.Data["GRAFANA_CLIENT_SECRET"] },
                                }
                            };

                            await k8s.CreateNamespacedSecretAsync(monitorSecret, KubeNamespace.NeonMonitor);
                        });

                    int i = 0;

                    foreach (var taint in await GetTaintsAsync(controller, NodeLabels.LabelMetricsInternal, "true"))
                    {
                        values.Add($"tolerations[{i}].key", $"{taint.Key.Split("=")[0]}");
                        values.Add($"tolerations[{i}].effect", taint.Effect);
                        values.Add($"tolerations[{i}].operator", "Exists");
                        i++;
                    }

                    if (serviceAdvice.PodMemoryRequest.HasValue && serviceAdvice.PodMemoryLimit.HasValue)
                    {
                        values.Add($"resources.requests.memory", ToSiString(serviceAdvice.PodMemoryRequest));
                        values.Add($"resources.limits.memory", ToSiString(serviceAdvice.PodMemoryLimit));
                    }

                    controller.ThrowIfCancelled();
                    await master.InstallHelmChartAsync(controller, "grafana",
                        releaseName:  "grafana",
                        @namespace:   KubeNamespace.NeonMonitor,
                        prioritySpec: PriorityClass.NeonMonitor.Name,
                        values:       values);
                });

            await master.InvokeIdempotentAsync("setup/monitoring-grafana-ready",
                async () =>
                {
                    controller.LogProgress(master, verb: "wait for", message: "grafana");

                    controller.ThrowIfCancelled();
                    await k8s.WaitForDeploymentAsync(KubeNamespace.NeonMonitor, "grafana-operator", timeout: clusterOpTimeout, pollInterval: clusterOpPollInterval, cancellationToken: controller.CancellationToken);

                    controller.ThrowIfCancelled();
                    await k8s.WaitForDeploymentAsync(KubeNamespace.NeonMonitor, "grafana-deployment", timeout: clusterOpTimeout, pollInterval: clusterOpPollInterval, cancellationToken: controller.CancellationToken);
                });

            if (cluster.Definition.Features.Kiali)
            {
                controller.ThrowIfCancelled();
                await master.InvokeIdempotentAsync("setup/monitoring-grafana-kiali-user",
                    async () =>
                    {
                        controller.LogProgress(master, verb: "create", message: "kiali-grafana-user");

                        var grafanaSecret   = await k8s.ReadNamespacedSecretAsync("grafana-admin-credentials", KubeNamespace.NeonMonitor);
                        var grafanaUser     = Encoding.UTF8.GetString(grafanaSecret.Data["GF_SECURITY_ADMIN_USER"]);
                        var grafanaPassword = Encoding.UTF8.GetString(grafanaSecret.Data["GF_SECURITY_ADMIN_PASSWORD"]);
                        var kialiSecret     = await k8s.ReadNamespacedSecretAsync("kiali", KubeNamespace.NeonSystem);
                        var kialiPassword   = Encoding.UTF8.GetString(kialiSecret.Data["grafanaPassword"]);

                        var cmd = new string[]
                        {
                            "/bin/bash",
                            "-c",
                            $@"curl -X POST http://{grafanaUser}:{grafanaPassword}@localhost:3000/api/admin/users -H 'Content-Type: application/json' -d '{{""name"":""kiali"",""email"":""kiali@cluster.local"",""login"":""kiali"",""password"":""{kialiPassword}"",""OrgId"":1}}'"
                        };

                        var pod = await k8s.GetNamespacedRunningPodAsync(KubeNamespace.NeonMonitor, labelSelector: "app=grafana");

                        controller.ThrowIfCancelled();

                        await k8s.NamespacedPodExecWithRetryAsync(
                                    retryPolicy:        podExecRetry,
                                    namespaceParameter: pod.Namespace(),
                                    name:               pod.Name(),
                                    container:          "grafana",
                                    command:            cmd);
                    });
            }

            await master.InvokeIdempotentAsync("setup/monitoring-grafana-config",
                async () =>
                {
                    controller.LogProgress(master, verb: "configure", message: "grafana");

                    var grafanaSecret   = await k8s.ReadNamespacedSecretAsync("grafana-admin-credentials", KubeNamespace.NeonMonitor);
                    var grafanaUser     = Encoding.UTF8.GetString(grafanaSecret.Data["GF_SECURITY_ADMIN_USER"]);
                    var grafanaPassword = Encoding.UTF8.GetString(grafanaSecret.Data["GF_SECURITY_ADMIN_PASSWORD"]);
                    var grafanaPod      = await k8s.GetNamespacedRunningPodAsync(KubeNamespace.NeonMonitor, labelSelector: "app=grafana");

                    var cmd = new string[]
                        {
                            "/bin/bash",
                            "-c",
                            $@"curl -X GET -H 'Content-Type: application/json' http://{grafanaUser}:{grafanaPassword}@localhost:3000/api/dashboards/uid/neonkube-default-dashboard"
                        };

                    string dashboardId = "";
                    await NeonHelper.WaitForAsync(
                            async () =>
                            {
                                try
                                {
                                    var defaultDashboard = (await k8s.NamespacedPodExecWithRetryAsync(
                                        retryPolicy:        podExecRetry,
                                        namespaceParameter: grafanaPod.Namespace(),
                                        name:               grafanaPod.Name(),
                                        container:          "grafana",
                                        command:            cmd)).EnsureSuccess();

                                    dashboardId = NeonHelper.JsonDeserialize<dynamic>(defaultDashboard.OutputText)["dashboard"]["id"];

                                    return true;
                                }
                                catch
                                {
                                    return false;
                                }
                            },
                            timeout: TimeSpan.FromSeconds(300),
                            pollInterval: TimeSpan.FromMilliseconds(250));

                    cmd = new string[]
                        {
                            "/bin/bash",
                            "-c",
                            $@"curl -X PUT -H 'Content-Type: application/json' -d '{{""theme"":"""",""homeDashboardId"":{dashboardId},""timezone"":"""",""weekStart"":""""}}' http://{grafanaUser}:{grafanaPassword}@localhost:3000/api/org/preferences"
                        };

                    (await k8s.NamespacedPodExecWithRetryAsync(
                                retryPolicy:        podExecRetry,
                                namespaceParameter: grafanaPod.Namespace(),
                                name:               grafanaPod.Name(),
                                container:          "grafana",
                                command:            cmd)).EnsureSuccess();
                });
        }

        /// <summary>
        /// Installs a Minio cluster to the monitoring namespace.
        /// </summary>
        /// <param name="controller">The setup controller.</param>
        /// <param name="master">The master node where the operation will be performed.</param>
        /// <returns>The tracking <see cref="Task"/>.</returns>
        public static async Task InstallMinioAsync(ISetupController controller, NodeSshProxy<NodeDefinition> master)
        {
            await SyncContext.Clear;
            Covenant.Requires<ArgumentNullException>(controller != null, nameof(controller));
            Covenant.Requires<ArgumentNullException>(master != null, nameof(master));

            var cluster        = controller.Get<ClusterProxy>(KubeSetupProperty.ClusterProxy);
            var k8s            = GetK8sClient(controller);
            var clusterAdvice  = controller.Get<KubeClusterAdvice>(KubeSetupProperty.ClusterAdvice);
            var serviceAdvice  = clusterAdvice.GetServiceAdvice(KubeClusterAdvice.Minio);
            var operatorAdvice = clusterAdvice.GetServiceAdvice(KubeClusterAdvice.MinioOperator);

            await master.InvokeIdempotentAsync("setup/minio-all",
                async () =>
                {
                    controller.ThrowIfCancelled();
                    await CreateHostPathStorageClass(controller, master, "neon-internal-minio");

                    controller.ThrowIfCancelled();
                    await master.InvokeIdempotentAsync("setup/minio",
                        async () =>
                        {
                            controller.LogProgress(master, verb: "setup", message: "minio");

                            var values = new Dictionary<string, object>();

                            values.Add("cluster.name", cluster.Definition.Name);
                            values.Add("cluster.domain", cluster.Definition.Domain);
                            values.Add("neonkube.clusterDomain.minio", ClusterDomain.Minio);
                            values.Add("neonkube.clusterDomain.sso", ClusterDomain.Sso);
                            values.Add($"metrics.serviceMonitor.enabled", serviceAdvice.MetricsEnabled ?? clusterAdvice.MetricsEnabled);
                            values.Add($"metrics.serviceMonitor.interval", serviceAdvice.MetricsInterval ?? clusterAdvice.MetricsInterval);
                            values.Add("image.organization", KubeConst.LocalClusterRegistry);
                            values.Add("mcImage.organization", KubeConst.LocalClusterRegistry);
                            values.Add("helmKubectlJqImage.organization", KubeConst.LocalClusterRegistry);
                            values.Add($"tenants[0].pools[0].servers", serviceAdvice.ReplicaCount);
                            values.Add($"tenants[0].pools[0].volumesPerServer", cluster.Definition.Storage.Minio.VolumesPerNode);

                            var volumesize = ByteUnits.Humanize(
                                ByteUnits.Parse(cluster.Definition.Storage.Minio.VolumeSize),
                                powerOfTwo:      true,
                                spaceBeforeUnit: false,
                                removeByteUnit:  true);

                            values.Add($"tenants[0].pools[0].size", volumesize);

                            if (serviceAdvice.ReplicaCount > 1)
                            {
                                values.Add($"mode", "distributed");
                            }

                            values.Add($"tenants[0].pools[0].resources.requests.memory", ToSiString(serviceAdvice.PodMemoryRequest));
                            values.Add($"tenants[0].pools[0].resources.limits.memory", ToSiString(serviceAdvice.PodMemoryLimit));

                            values.Add($"operator.resources.requests.memory", ToSiString(operatorAdvice.PodMemoryRequest));
                            values.Add($"operator.resources.limits.memory", ToSiString(operatorAdvice.PodMemoryLimit));

                            var accessKey = NeonHelper.GetCryptoRandomPassword(16);
                            var secretKey = NeonHelper.GetCryptoRandomPassword(cluster.Definition.Security.PasswordLength);

                            values.Add($"tenants[0].secrets.accessKey", accessKey);
                            values.Add($"clients.aliases.minio.accessKey", accessKey);
                            values.Add($"tenants[0].secrets.secretKey", secretKey);
                            values.Add($"clients.aliases.minio.secretKey", secretKey);

                            values.Add($"tenants[0].console.secrets.passphrase", NeonHelper.GetCryptoRandomPassword(cluster.Definition.Security.PasswordLength));
                            values.Add($"tenants[0].console.secrets.salt", NeonHelper.GetCryptoRandomPassword(cluster.Definition.Security.PasswordLength));
                            values.Add($"tenants[0].console.secrets.accessKey", NeonHelper.GetCryptoRandomPassword(16));
                            values.Add($"tenants[0].console.secrets.secretKey", NeonHelper.GetCryptoRandomPassword(cluster.Definition.Security.PasswordLength));

                            int i = 0;

                            foreach (var taint in await GetTaintsAsync(controller, NodeLabels.LabelMinioInternal, "true"))
                            {
                                foreach (var component in new string[]
                                {
                                    "tenants[0].pools[0]", "console", "operator"
                                })
                                {
                                    values.Add($"{component}.tolerations[{i}].key", $"{taint.Key.Split("=")[0]}");
                                    values.Add($"{component}.tolerations[{i}].effect", taint.Effect);
                                    values.Add($"{component}.tolerations[{i}].operator", "Exists");
                                }
                                i++;
                            }

                            values.Add("tenants[0].priorityClassName", PriorityClass.NeonStorage.Name);

                            await master.InstallHelmChartAsync(controller, "minio",
                                releaseName:  "minio",
                                @namespace:   KubeNamespace.NeonSystem,
                                prioritySpec: PriorityClass.NeonStorage.Name,
                                values:       values);
                        });

                    controller.ThrowIfCancelled();
                    await master.InvokeIdempotentAsync("configure/minio-secrets",
                        async () =>
                        {
                            controller.LogProgress(master, verb: "configure", message: "minio secret");

                            var secret = await k8s.ReadNamespacedSecretAsync("minio", KubeNamespace.NeonSystem);

                            secret.Metadata.NamespaceProperty = "monitoring";

                            var monitoringSecret = new V1Secret()
                            {
                                Metadata = new V1ObjectMeta()
                                {
                                    Name        = secret.Name(),
                                    Annotations = new Dictionary<string, string>()
                                    {
                                        { "reloader.stakater.com/match", "true" }
                                    }
                                },
                                Data = secret.Data,
                            };
                            await k8s.CreateNamespacedSecretAsync(monitoringSecret, KubeNamespace.NeonMonitor);
                        });

                    controller.ThrowIfCancelled();
                    await master.InvokeIdempotentAsync("setup/minio-ready",
                        async () =>
                        {
                            controller.LogProgress(master, verb: "wait for", message: "minio");

                            await NeonHelper.WaitAllAsync(
                                new List<Task>()
                                {
                                    k8s.WaitForStatefulSetAsync(KubeNamespace.NeonSystem, labelSelector: "app=minio", timeout: clusterOpTimeout, pollInterval: clusterOpPollInterval, cancellationToken: controller.CancellationToken),
                                    k8s.WaitForDeploymentAsync(KubeNamespace.NeonSystem, "minio-console", timeout: clusterOpTimeout, pollInterval: clusterOpPollInterval, cancellationToken: controller.CancellationToken),
                                    k8s.WaitForDeploymentAsync(KubeNamespace.NeonSystem, "minio-operator", timeout: clusterOpTimeout, pollInterval: clusterOpPollInterval, cancellationToken: controller.CancellationToken),
                                },
                                timeoutMessage:    "setup/minio-ready",
                                cancellationToken: controller.CancellationToken);
                        });

                    controller.ThrowIfCancelled();
                    await master.InvokeIdempotentAsync("setup/minio-policy",
                        async () =>
                        {
                            controller.LogProgress(master, verb: "wait for", message: "minio");

                            var minioPod = await k8s.GetNamespacedRunningPodAsync(KubeNamespace.NeonSystem, labelSelector: "app.kubernetes.io/name=minio-operator");

                            await k8s.NamespacedPodExecWithRetryAsync(
                                retryPolicy:              podExecRetry,
                                namespaceParameter: minioPod.Namespace(),
                                name:               minioPod.Name(),
                                container:          "minio-operator",
                                command:            new string[] {
                                    "/bin/bash",
                                    "-c",
                                    $@"echo '{{""Version"":""2012-10-17"",""Statement"":[{{""Effect"":""Allow"",""Action"":[""admin:*""]}},{{""Effect"":""Allow"",""Action"":[""s3:*""],""Resource"":[""arn:aws:s3:::*""]}}]}}' > /tmp/superadmin.json"
                                });

                            controller.ThrowIfCancelled();
                            await cluster.ExecMinioCommandAsync(
                                retryPolicy:    podExecRetry,
                                mcCommand:      "admin policy add minio superadmin /tmp/superadmin.json");
                        });
                });
        }

        /// <summary>
        /// Installs an Neon Monitoring to the monitoring namespace.
        /// </summary>
        /// <param name="controller">The setup controller.</param>
        /// <returns>The tracking <see cref="Task"/>.</returns>
        public static async Task InstallMonitoringAsync(ISetupController controller)
        {
            await SyncContext.Clear;
            Covenant.Requires<ArgumentNullException>(controller != null, nameof(controller));

            var cluster = controller.Get<ClusterProxy>(KubeSetupProperty.ClusterProxy);
            var master  = cluster.FirstMaster;
            var tasks   = new List<Task>();

            controller.LogProgress(master, verb: "setup", message: "cluster monitoring");

            tasks.Add(WaitForPrometheusAsync(controller, master));
            tasks.Add(InstallMemcachedAsync(controller, master));
            tasks.Add(InstallMimirAsync(controller, master));
            tasks.Add(InstallLokiAsync(controller, master));
            tasks.Add(InstallKubeStateMetricsAsync(controller, master));

            if (cluster.Definition.Features.Tracing)
            {
                tasks.Add(InstallTempoAsync(controller, master));
            }

            tasks.Add(InstallGrafanaAsync(controller, master));

            controller.LogProgress(master, verb: "wait", message: "for cluster monitoring");

            await NeonHelper.WaitAllAsync(tasks,
                timeoutMessage:    "install-monitoring",
                cancellationToken: controller.CancellationToken);
        }

        /// <summary>
        /// Installs a harbor container registry and required components.
        /// </summary>
        /// <param name="controller">The setup controller.</param>
        /// <param name="master">The master node where the operation will be performed.</param>
        /// <returns>The tracking <see cref="Task"/>.</returns>
        public static async Task InstallRedisAsync(ISetupController controller, NodeSshProxy<NodeDefinition> master)
        {
            await SyncContext.Clear;
            Covenant.Requires<ArgumentNullException>(controller != null, nameof(controller));
            Covenant.Requires<ArgumentNullException>(master != null, nameof(master));

            var cluster       = controller.Get<ClusterProxy>(KubeSetupProperty.ClusterProxy);
            var k8s           = GetK8sClient(controller);
            var clusterAdvice = controller.Get<KubeClusterAdvice>(KubeSetupProperty.ClusterAdvice);
            var serviceAdvice = clusterAdvice.GetServiceAdvice(KubeClusterAdvice.Redis);

            controller.ThrowIfCancelled();
            await master.InvokeIdempotentAsync("setup/redis",
                async () =>
                {
                    await SyncContext.Clear;

                    controller.LogProgress(master, verb: "setup", message: "redis");

                    var values = new Dictionary<string, object>();

                    values.Add("image.organization", KubeConst.LocalClusterRegistry);
                    values.Add($"replicas", serviceAdvice.ReplicaCount);
                    values.Add($"haproxy.metrics.enabled", serviceAdvice.MetricsEnabled ?? clusterAdvice.MetricsEnabled);
                    values.Add($"exporter.enabled", serviceAdvice.MetricsEnabled ?? clusterAdvice.MetricsEnabled);
                    values.Add($"exporter.serviceMonitor.interval", serviceAdvice.MetricsInterval ?? clusterAdvice.MetricsInterval);
                    values.Add("serviceMesh.enabled", cluster.Definition.Features.ServiceMesh);

                    if (serviceAdvice.ReplicaCount < 2)
                    {
                        values.Add($"hardAntiAffinity", false);
                        values.Add($"sentinel.quorum", 1);
                    }

                    int i = 0;

                    foreach (var taint in await GetTaintsAsync(controller, NodeLabels.LabelNeonSystemRegistry, "true"))
                    {
                        values.Add($"tolerations[{i}].key", $"{taint.Key.Split("=")[0]}");
                        values.Add($"tolerations[{i}].effect", taint.Effect);
                        values.Add($"tolerations[{i}].operator", "Exists");
                        i++;
                    }

                    await master.InstallHelmChartAsync(controller, "redis-ha",
                        releaseName:  "neon-redis",
                        @namespace:   KubeNamespace.NeonSystem,
                        prioritySpec: PriorityClass.NeonData.Name,
                        values:       values);
                });

            controller.ThrowIfCancelled();
            await master.InvokeIdempotentAsync("setup/redis-ready",
                async () =>
                {
                    await SyncContext.Clear;

                    controller.LogProgress(master, verb: "wait for", message: "redis");

                    await k8s.WaitForStatefulSetAsync(KubeNamespace.NeonSystem, "neon-redis-server", timeout: clusterOpTimeout, pollInterval: clusterOpPollInterval, cancellationToken: controller.CancellationToken);
                });
        }

        /// <summary>
        /// Installs a harbor container registry and required components.
        /// </summary>
        /// <param name="controller">The setup controller.</param>
        /// <param name="master">The master node where the operation will be performed.</param>
        /// <returns>The tracking <see cref="Task"/>.</returns>
        public static async Task InstallHarborAsync(ISetupController controller, NodeSshProxy<NodeDefinition> master)
        {
            await SyncContext.Clear;
            Covenant.Requires<ArgumentNullException>(controller != null, nameof(controller));
            Covenant.Requires<ArgumentNullException>(master != null, nameof(master));

            var cluster       = controller.Get<ClusterProxy>(KubeSetupProperty.ClusterProxy);
            var clusterLogin  = controller.Get<ClusterLogin>(KubeSetupProperty.ClusterLogin);
            var k8s           = GetK8sClient(controller);
            var clusterAdvice = controller.Get<KubeClusterAdvice>(KubeSetupProperty.ClusterAdvice);
            var serviceAdvice = clusterAdvice.GetServiceAdvice(KubeClusterAdvice.Harbor);

            controller.ThrowIfCancelled();
            await master.InvokeIdempotentAsync("configure/registry-minio-secret",
                async () =>
                {
                    controller.LogProgress(master, verb: "configure", message: "minio secret");

                    var minioSecret = await k8s.ReadNamespacedSecretAsync("minio", KubeNamespace.NeonSystem);

                    var secret = new V1Secret()
                    {
                        Metadata = new V1ObjectMeta()
                        {
                            Name              = "registry-minio",
                            NamespaceProperty = KubeNamespace.NeonSystem,
                            Annotations       = new Dictionary<string, string>()
                            {
                                {  "reloader.stakater.com/match", "true" }
                            }
                        },
                        Type = "Opaque",
                        Data = new Dictionary<string, byte[]>()
                        {
                            { "secret", minioSecret.Data["secretkey"] }
                        }
                    };

                    await k8s.CreateNamespacedSecretAsync(secret, KubeNamespace.NeonSystem);
                });

            controller.ThrowIfCancelled();
            await master.InvokeIdempotentAsync("setup/harbor-db",
                async () =>
                {
                    controller.LogProgress(master, verb: "configure", message: "harbor databases");

                    await CreateStorageClass(controller, master, "neon-internal-registry");

                    // Create the Harbor databases.

                    var dbSecret = await k8s.ReadNamespacedSecretAsync(KubeConst.NeonSystemDbServiceSecret, KubeNamespace.NeonSystem);

                    var harborSecret = new V1Secret()
                    {
                        Metadata = new V1ObjectMeta()
                        {
                            Name              = KubeConst.RegistrySecretKey,
                            NamespaceProperty = KubeNamespace.NeonSystem
                        },
                        Data       = new Dictionary<string, byte[]>(),
                        StringData = new Dictionary<string, string>()
                    };

                    if ((await k8s.ListNamespacedSecretAsync(KubeNamespace.NeonSystem)).Items.Any(s => s.Metadata.Name == KubeConst.RegistrySecretKey))
                    {
                        harborSecret = await k8s.ReadNamespacedSecretAsync(KubeConst.RegistrySecretKey, KubeNamespace.NeonSystem);

                        if (harborSecret.Data == null)
                        {
                            harborSecret.Data = new Dictionary<string, byte[]>();
                        }

                        harborSecret.StringData = new Dictionary<string, string>();
                    }

                    if (!harborSecret.Data.ContainsKey("postgresql-password"))
                    {
                        harborSecret.Data["postgresql-password"] = dbSecret.Data["password"];

                        await k8s.UpsertSecretAsync(harborSecret, KubeNamespace.NeonSystem);
                    }

                    if (!harborSecret.Data.ContainsKey("secret"))
                    {
                        harborSecret.StringData["secret"] = NeonHelper.GetCryptoRandomPassword(cluster.Definition.Security.PasswordLength);

                        await k8s.UpsertSecretAsync(harborSecret, KubeNamespace.NeonSystem);
                    }
                });

            controller.ThrowIfCancelled();
            await master.InvokeIdempotentAsync("setup/harbor",
                async () =>
                {
                    controller.LogProgress(master, verb: "configure", message: "harbor minio");

                    // Create the Harbor Minio bucket.

                    var minioSecret = await k8s.ReadNamespacedSecretAsync("minio", KubeNamespace.NeonSystem);
                    var accessKey   = Encoding.UTF8.GetString(minioSecret.Data["accesskey"]);
                    var secretKey   = Encoding.UTF8.GetString(minioSecret.Data["secretkey"]);
                    var serviceUser = await KubeHelper.GetClusterLdapUserAsync(k8s, "serviceuser");

                    await CreateMinioBucketAsync(controller, master, KubeMinioBucket.Harbor);

                    // Install the Harbor Helm chart.

                    var values = new Dictionary<string, object>();

                    values.Add("cluster.name", cluster.Definition.Name);
                    values.Add("cluster.domain", cluster.Definition.Domain);
                    values.Add($"metrics.enabled", serviceAdvice.MetricsEnabled ?? clusterAdvice.MetricsEnabled);
                    values.Add($"metrics.interval", serviceAdvice.MetricsInterval ?? clusterAdvice.MetricsInterval);
                    values.Add("serviceMesh.enabled", cluster.Definition.Features.ServiceMesh);

                    values.Add($"components.chartMuseum.enabled", cluster.Definition.Features.Harbor.ChartMuseum);
                    values.Add($"components.notary.enabled", cluster.Definition.Features.Harbor.Notary);
                    values.Add($"components.trivy.enabled", cluster.Definition.Features.Harbor.Trivy);
                    
                    values.Add("neonkube.clusterDomain.harborNotary", ClusterDomain.HarborNotary);
                    values.Add("neonkube.clusterDomain.harborRegistry", ClusterDomain.HarborRegistry);

                    values.Add($"storage.s3.accessKey", Encoding.UTF8.GetString(minioSecret.Data["accesskey"]));
                    values.Add($"storage.s3.secretKeyRef", "registry-minio");

                    var baseDN = $@"dc={string.Join($@"\,dc=", cluster.Definition.Domain.Split('.'))}";

                    values.Add($"ldap.baseDN", baseDN);
                    values.Add($"ldap.secret", serviceUser.Password);

                    int j = 0;

                    foreach (var taint in await GetTaintsAsync(controller, NodeLabels.LabelNeonSystemRegistry, "true"))
                    {
                        values.Add($"tolerations[{j}].key", $"{taint.Key.Split("=")[0]}");
                        values.Add($"tolerations[{j}].effect", taint.Effect);
                        values.Add($"tolerations[{j}].operator", "Exists");
                        j++;
                    }

                    values.Add("nginx.priorityClassName", PriorityClass.NeonData.Name);
                    values.Add("portal.priorityClassName", PriorityClass.NeonData.Name);
                    values.Add("core.priorityClassName", PriorityClass.NeonData.Name);
                    values.Add("jobservice.priorityClassName", PriorityClass.NeonData.Name);
                    values.Add("registry.priorityClassName", PriorityClass.NeonData.Name);
                    values.Add("chartmuseum.priorityClassName", PriorityClass.NeonData.Name);
                    values.Add("clair.priorityClassName", PriorityClass.NeonData.Name);
                    values.Add("notary.server.priorityClassName", PriorityClass.NeonData.Name);
                    values.Add("notary.signer.priorityClassName", PriorityClass.NeonData.Name);
                    values.Add("trivy.priorityClassName", PriorityClass.NeonData.Name);

                    await master.InstallHelmChartAsync(controller, "harbor",
                        releaseName:  "registry-harbor",
                        @namespace:   KubeNamespace.NeonSystem,
                        prioritySpec: PriorityClass.NeonData.Name,
                        values:       values);
                });

            controller.ThrowIfCancelled();
            await master.InvokeIdempotentAsync("setup/harbor-ready",
                async () =>
                {
                    controller.LogProgress(master, verb: "wait for", message: "harbor");

                    var tasks = new List<Task>();

                    if (cluster.Definition.Features.Harbor.ChartMuseum)
                    {
                        tasks.Add(k8s.WaitForDeploymentAsync(KubeNamespace.NeonSystem, "registry-harbor-harbor-chartmuseum", timeout: clusterOpTimeout, pollInterval: clusterOpPollInterval, cancellationToken: controller.CancellationToken));
                    }

                    tasks.Add(k8s.WaitForDeploymentAsync(KubeNamespace.NeonSystem, "registry-harbor-harbor-core", timeout: clusterOpTimeout, pollInterval: clusterOpPollInterval, cancellationToken: controller.CancellationToken));
                    tasks.Add(k8s.WaitForDeploymentAsync(KubeNamespace.NeonSystem, "registry-harbor-harbor-jobservice", timeout: clusterOpTimeout, pollInterval: clusterOpPollInterval, cancellationToken: controller.CancellationToken));

                    if (cluster.Definition.Features.Harbor.Notary)
                    {
                        tasks.Add(k8s.WaitForDeploymentAsync(KubeNamespace.NeonSystem, "registry-harbor-harbor-notaryserver", timeout: clusterOpTimeout, pollInterval: clusterOpPollInterval, cancellationToken: controller.CancellationToken));
                        tasks.Add(k8s.WaitForDeploymentAsync(KubeNamespace.NeonSystem, "registry-harbor-harbor-notarysigner", timeout: clusterOpTimeout, pollInterval: clusterOpPollInterval, cancellationToken: controller.CancellationToken));
                    }

                    tasks.Add(k8s.WaitForDeploymentAsync(KubeNamespace.NeonSystem, "registry-harbor-harbor-portal", timeout: clusterOpTimeout, pollInterval: clusterOpPollInterval, cancellationToken: controller.CancellationToken));
                    tasks.Add(k8s.WaitForDeploymentAsync(KubeNamespace.NeonSystem, "registry-harbor-harbor-registry", timeout: clusterOpTimeout, pollInterval: clusterOpPollInterval, cancellationToken: controller.CancellationToken));
                    
                    if (cluster.Definition.Features.Harbor.Trivy)
                    {
                        tasks.Add(k8s.WaitForDeploymentAsync(KubeNamespace.NeonSystem, "registry-harbor-harbor-trivy", timeout: clusterOpTimeout, pollInterval: clusterOpPollInterval, cancellationToken: controller.CancellationToken));
                    }

                    if ((bool)(serviceAdvice.MetricsEnabled ?? clusterAdvice.MetricsEnabled))
                    {
                        tasks.Add(k8s.WaitForDeploymentAsync(KubeNamespace.NeonSystem, "registry-harbor-harbor-exporter", timeout: clusterOpTimeout, pollInterval: clusterOpPollInterval, cancellationToken: controller.CancellationToken));
                    }

                    await NeonHelper.WaitAllAsync(tasks,
                        timeoutMessage:    "setup/harbor-ready",
                        cancellationToken: controller.CancellationToken);
                });

            controller.ThrowIfCancelled();
            await master.InvokeIdempotentAsync("setup/harbor-login",
                async () =>
                {
                    var user     = await KubeHelper.GetClusterLdapUserAsync(k8s, "root");
                    var password = user.Password;
                    var sbScript = new StringBuilder();
                    var sbArgs   = new StringBuilder();

                    sbScript.AppendLineLinux("#!/bin/bash");
                    sbScript.AppendLineLinux($"echo '{password}' | podman login neon-registry.node.local --username {user.Name} --password-stdin");

                    foreach (var node in cluster.Nodes)
                    {
                        controller.ThrowIfCancelled();

                        await NeonHelper.WaitForAsync(
                            async () =>
                            {
                                try
                                {
                                    master.SudoCommand(CommandBundle.FromScript(sbScript), RunOptions.None).EnsureSuccess();

                                    return await Task.FromResult(true);
                                }
                                catch
                                {
                                    return await Task.FromResult(false);
                                }
                            },
                            timeout: TimeSpan.FromSeconds(600),
                            pollInterval: TimeSpan.FromSeconds(1),
                            cancellationToken: controller.CancellationToken);
                    }
                });

            controller.ThrowIfCancelled();
            await master.InvokeIdempotentAsync("setup/harbor-login-workstation",
                async () =>
                {
                    var user     = await KubeHelper.GetClusterLdapUserAsync(k8s, "root");
                    var password = user.Password;
                    var sbScript = new StringBuilder();
                    var sbArgs   = new StringBuilder();

                    if (!string.IsNullOrEmpty(NeonHelper.DockerCli))
                    {
                        Console.WriteLine($"Login: Workstation to Harbor...");

                        var login = KubeHelper.GetClusterLogin(KubeHelper.CurrentContextName);

                        NeonHelper.Execute(NeonHelper.DockerCli,
                            new object[]
                            {
                                "login",
                                $"{ClusterDomain.HarborRegistry}.{login.ClusterDefinition.Domain}",
                                "--username",
                                "root",
                                "--password-stdin"
                            },
                            input: new StringReader(login.SsoPassword));
                    }
                });
        }

        /// <summary>
        /// Installs <b>neon-cluster-operator</b>.
        /// </summary>
        /// <param name="controller">The setup controller.</param>
        /// <param name="master">The master node where the operation will be performed.</param>
        /// <returns>The tracking <see cref="Task"/>.</returns>
        public static async Task InstallClusterOperatorAsync(ISetupController controller, NodeSshProxy<NodeDefinition> master)
        {
            await SyncContext.Clear;

            Covenant.Requires<ArgumentNullException>(controller != null, nameof(controller));
            Covenant.Requires<ArgumentNullException>(master != null, nameof(master));

            var k8s           = GetK8sClient(controller);
            var cluster       = controller.Get<ClusterProxy>(KubeSetupProperty.ClusterProxy);
            var clusterAdvice = controller.Get<KubeClusterAdvice>(KubeSetupProperty.ClusterAdvice);
            var serviceAdvice = clusterAdvice.GetServiceAdvice(KubeClusterAdvice.NeonClusterOperator);

            controller.ThrowIfCancelled();
            await master.InvokeIdempotentAsync("setup/cluster-operator",
                async () =>
                {
                    controller.LogProgress(master, verb: "setup", message: "neon-cluster-operator");

                    var values = new Dictionary<string, object>();

                    values.Add("image.organization", KubeConst.LocalClusterRegistry);
                    values.Add("image.tag", KubeVersions.NeonKubeContainerImageTag);
                    values.Add("serviceMesh.enabled", cluster.Definition.Features.ServiceMesh);
                    values.Add("resource.requests.memory", $"{ToSiString(serviceAdvice.PodMemoryRequest)}");
                    values.Add("resource.limits.memory", $"{ToSiString(serviceAdvice.PodMemoryLimit)}");
                    values.Add("metrics.enabled", serviceAdvice.MetricsEnabled ?? clusterAdvice.MetricsEnabled);
                    values.Add("metrics.servicemonitor.interval", serviceAdvice.MetricsInterval ?? clusterAdvice.MetricsInterval);

                    await master.InstallHelmChartAsync(controller, "neon-cluster-operator",
                        releaseName:  "neon-cluster-operator",
                        @namespace:   KubeNamespace.NeonSystem,
                        prioritySpec: PriorityClass.NeonOperator.Name,
                        values:       values);
                });

            controller.ThrowIfCancelled();
            await master.InvokeIdempotentAsync("setup/cluster-operator-ready",
                async () =>
                {
                    controller.LogProgress(master, verb: "wait for", message: "neon-cluster-operator");

                    await k8s.WaitForDaemonsetAsync(KubeNamespace.NeonSystem, "neon-cluster-operator", timeout: clusterOpTimeout, pollInterval: clusterOpPollInterval, cancellationToken: controller.CancellationToken);
                });
        }

        /// <summary>
        /// Installs <b>neon-node-agent</b>.
        /// </summary>
        /// <param name="controller">The setup controller.</param>
        /// <param name="master">The master node where the operation will be performed.</param>
        /// <returns>The tracking <see cref="Task"/>.</returns>
        public static async Task InstallNodeAgentAsync(ISetupController controller, NodeSshProxy<NodeDefinition> master)
        {
            await SyncContext.Clear;
            Covenant.Requires<ArgumentNullException>(controller != null, nameof(controller));
            Covenant.Requires<ArgumentNullException>(master != null, nameof(master));

            var k8s           = GetK8sClient(controller);
            var cluster       = controller.Get<ClusterProxy>(KubeSetupProperty.ClusterProxy);
            var clusterAdvice = controller.Get<KubeClusterAdvice>(KubeSetupProperty.ClusterAdvice);
            var serviceAdvice = clusterAdvice.GetServiceAdvice(KubeClusterAdvice.NeonDashboard);
            
            controller.ThrowIfCancelled();
            await master.InvokeIdempotentAsync("setup/neon-dashboard-resources",
                async () =>
                {
                    controller.LogProgress(master, verb: "setup", message: "neon-dashboard");

                    await CreateNeonDashboardAsync(
                            controller,
                            master,
                            name: "kubernetes",
                            url: $"https://{ClusterDomain.KubernetesDashboard}.{cluster.Definition.Domain}",
                            displayName: "Kubernetes",
                            enabled: true,
                            displayOrder: 1);

                    if (cluster.Definition.Features.Grafana)
                    {
                        await CreateNeonDashboardAsync(
                            controller,
                            master,
                            name:         "grafana",
                            url:          $"https://{ClusterDomain.Grafana}.{cluster.Definition.Domain}",
                            displayName:  "Grafana",
                            enabled:      true,
                            displayOrder: 10);
                    }
                    if (cluster.Definition.Features.Minio)
                    {
                        await CreateNeonDashboardAsync(
                            controller,
                            master,
                            name:         "minio",
                            url:          $"https://{ClusterDomain.Minio}.{cluster.Definition.Domain}",
                            displayName:  "Minio",
                            enabled:      true,
                            displayOrder: 10);
                    }
                    if (cluster.Definition.Features.Harbor.Enabled)
                    {
                        await CreateNeonDashboardAsync(
                            controller,
                            master,
                            name:         "harbor",
                            url:          $"https://{ClusterDomain.HarborRegistry}.{cluster.Definition.Domain}",
                            displayName:  "Harbor",
                            enabled:      true,
                            displayOrder: 10);
                    }
                    if (cluster.Definition.Features.Kiali)
                    {
                        await CreateNeonDashboardAsync(
                            controller,
                            master,
                            name:         "kiali",
                            url:          $"https://{ClusterDomain.Kiali}.{cluster.Definition.Domain}",
                            displayName:  "Kiali",
                            enabled:      true,
                            displayOrder: 10);
                    }
                });

            controller.ThrowIfCancelled();
            await master.InvokeIdempotentAsync("setup/neon-node-agent",
                async () =>
                {
                    controller.LogProgress(master, verb: "setup", message: "neon-node-agent");

                    var values = new Dictionary<string, object>();

                    values.Add("image.organization", KubeConst.LocalClusterRegistry);
                    values.Add("image.tag", KubeVersions.NeonKubeContainerImageTag);
                    values.Add("cluster.name", cluster.Definition.Name);
                    values.Add("cluster.domain", cluster.Definition.Domain);
                    values.Add($"cluster.datacenter", cluster.Definition.Datacenter);
                    values.Add($"cluster.version", cluster.Definition.ClusterVersion);
                    values.Add($"cluster.hostingEnvironment", cluster.Definition.Hosting.Environment);
                    values.Add($"neonkube.clusterDomain.neonDashboard", ClusterDomain.NeonDashboard);
                    values.Add("serviceMesh.enabled", cluster.Definition.Features.ServiceMesh);
                    values.Add("metrics.enabled", serviceAdvice.MetricsEnabled ?? clusterAdvice.MetricsEnabled);
                    values.Add("metrics.servicemonitor.interval", serviceAdvice.MetricsInterval ?? clusterAdvice.MetricsInterval);
                    values.Add("resource.requests.memory", $"{ToSiString(serviceAdvice.PodMemoryRequest)}");
                    values.Add("resource.limits.memory", $"{ToSiString(serviceAdvice.PodMemoryLimit)}");

                    await master.InstallHelmChartAsync(controller, "neon-node-agent",
                        releaseName:  "neon-node-agent",
                        @namespace:   KubeNamespace.NeonSystem,
                        prioritySpec: PriorityClass.NeonOperator.Name,
                        values:       values);
                });

            controller.ThrowIfCancelled();
            await master.InvokeIdempotentAsync("setup/neon-node-agent-ready",
                async () =>
                {
                    controller.LogProgress(master, verb: "wait for", message: "neon-node-agent");

                    await k8s.WaitForDaemonsetAsync(KubeNamespace.NeonSystem, "neon-node-agent", timeout: clusterOpTimeout, pollInterval: clusterOpPollInterval, cancellationToken: controller.CancellationToken);
                });
        }

        /// <summary>
        /// Installs <b>neon-dashboard</b>.
        /// </summary>
        /// <param name="controller">The setup controller.</param>
        /// <param name="master">The master node where the operation will be performed.</param>
        /// <returns>The tracking <see cref="Task"/>.</returns>
        public static async Task InstallNeonDashboardAsync(ISetupController controller, NodeSshProxy<NodeDefinition> master)
        {
            await SyncContext.Clear;
            Covenant.Requires<ArgumentNullException>(controller != null, nameof(controller));
            Covenant.Requires<ArgumentNullException>(master != null, nameof(master));

            var k8s           = GetK8sClient(controller);
            var cluster       = controller.Get<ClusterProxy>(KubeSetupProperty.ClusterProxy);
            var clusterAdvice = controller.Get<KubeClusterAdvice>(KubeSetupProperty.ClusterAdvice);
            var serviceAdvice = clusterAdvice.GetServiceAdvice(KubeClusterAdvice.NeonNodeAgent);

            controller.ThrowIfCancelled();
            await master.InvokeIdempotentAsync("setup/neon-dashboard",
                async () =>
                {
                    controller.LogProgress(master, verb: "setup", message: "neon-dashboard");

                    var values = new Dictionary<string, object>();

                    values.Add("image.organization", KubeConst.LocalClusterRegistry);
                    values.Add("image.tag", KubeVersions.NeonKubeContainerImageTag);
                    values.Add("cluster.name", cluster.Definition.Name);
                    values.Add("cluster.domain", cluster.Definition.Domain);
                    values.Add("neonkube.clusterDomain.neonDashboard", ClusterDomain.NeonDashboard);
                    values.Add("secrets.cipherKey", AesCipher.GenerateKey(256));
                    values.Add("serviceMesh.enabled", cluster.Definition.Features.ServiceMesh);
                    values.Add("metrics.enabled", serviceAdvice.MetricsEnabled ?? clusterAdvice.MetricsEnabled);
                    values.Add("metrics.servicemonitor.interval", serviceAdvice.MetricsInterval ?? clusterAdvice.MetricsInterval);

                    await master.InstallHelmChartAsync(controller, "neon-dashboard",
                        releaseName:  "neon-dashboard",
                        @namespace:   KubeNamespace.NeonSystem,
                        prioritySpec: PriorityClass.NeonApp.Name,
                        values:       values);
                });

            controller.ThrowIfCancelled();
            await master.InvokeIdempotentAsync("setup/neon-dashboard-ready",
                async () =>
                {
                    controller.LogProgress(master, verb: "wait for", message: "neon-dashboard");

                    await k8s.WaitForDeploymentAsync(KubeNamespace.NeonSystem, "neon-dashboard", timeout: clusterOpTimeout, pollInterval: clusterOpPollInterval, cancellationToken: controller.CancellationToken);
                });
        }

        /// <summary>
        /// Adds custom <see cref="V1NeonContainerRegistry"/> resources defined in the cluster definition to
        /// the cluster.  <b>neon-node-agent</b> will pick these up and regenerate the CRI-O configuration.
        /// </summary>
        /// <param name="controller">The setup controller.</param>
        /// <param name="master">The master node where the operation will be performed.</param>
        /// <returns>The tracking <see cref="Task"/>.</returns>
        /// <remarks>
        /// <note>
        /// This must be called after <see cref="InstallClusterOperatorAsync(ISetupController, NodeSshProxy{NodeDefinition})"/>
        /// because that's where the cluster CRDs get installed.
        /// </note>
        /// </remarks>
        public static async Task InstallContainerRegistryResourcesAsync(ISetupController controller, NodeSshProxy<NodeDefinition> master)
        {
            await SyncContext.Clear;
            Covenant.Requires<ArgumentNullException>(controller != null, nameof(controller));
            Covenant.Requires<ArgumentNullException>(master != null, nameof(master));

            controller.ThrowIfCancelled();
            await master.InvokeIdempotentAsync("setup/container-registries",
                async () =>
                {
                    var cluster = controller.Get<ClusterProxy>(KubeSetupProperty.ClusterProxy);
                    var k8s     = GetK8sClient(controller);

                    await cluster.AddContainerRegistryResourcesAsync();
                });
        }

        /// <summary>
        /// Creates the required namespaces.
        /// </summary>
        /// <param name="controller">The setup controller.</param>
        /// <param name="master">The master node where the operation will be performed.</param>
        /// <returns>The tracking <see cref="Task"/>.</returns>
        public static async Task<List<Task>> CreateNamespacesAsync(ISetupController controller, NodeSshProxy<NodeDefinition> master)
        {
            await SyncContext.Clear;

            Covenant.Requires<ArgumentNullException>(controller != null, nameof(controller));
            Covenant.Requires<ArgumentNullException>(master != null, nameof(master));

            await SyncContext.Clear;

            controller.ThrowIfCancelled();

            var tasks = new List<Task>();

            tasks.Add(CreateNamespaceAsync(controller, master, KubeNamespace.NeonMonitor, true));
            tasks.Add(CreateNamespaceAsync(controller, master, KubeNamespace.NeonStorage, false));
            tasks.Add(CreateNamespaceAsync(controller, master, KubeNamespace.NeonSystem, true));
            tasks.Add(CreateNamespaceAsync(controller, master, KubeNamespace.NeonStatus, false));

            return await Task.FromResult(tasks);
        }

        /// <summary>
        /// Installs a Citus-postgres database used by neon-system services.
        /// </summary>
        /// <param name="controller">The setup controller.</param>
        /// <param name="master">The master node where the operation will be performed.</param>
        /// <returns>The tracking <see cref="Task"/>.</returns>
        public static async Task InstallSystemDbAsync(ISetupController controller, NodeSshProxy<NodeDefinition> master)
        {
            await SyncContext.Clear;
            Covenant.Requires<ArgumentNullException>(controller != null, nameof(controller));
            Covenant.Requires<ArgumentNullException>(master != null, nameof(master));

            var cluster       = controller.Get<ClusterProxy>(KubeSetupProperty.ClusterProxy);
            var k8s           = GetK8sClient(controller);
            var clusterAdvice = controller.Get<KubeClusterAdvice>(KubeSetupProperty.ClusterAdvice);
            var serviceAdvice = clusterAdvice.GetServiceAdvice(KubeClusterAdvice.NeonSystemDb);

            var values = new Dictionary<string, object>();

            values.Add($"metrics.enabled", serviceAdvice.MetricsEnabled ?? clusterAdvice.MetricsEnabled);
            values.Add($"metrics.interval", serviceAdvice.MetricsInterval ?? clusterAdvice.MetricsInterval);

            if (cluster.Definition.IsDesktopBuiltIn)
            {
                values.Add($"persistence.size", "1Gi");
            }

            controller.ThrowIfCancelled();
            await CreateStorageClass(controller, master, "neon-internal-system-db");

            if (serviceAdvice.PodMemoryRequest.HasValue && serviceAdvice.PodMemoryLimit.HasValue)
            {
                values.Add($"resources.requests.memory", ToSiString(serviceAdvice.PodMemoryRequest));
                values.Add($"resources.limits.memory", ToSiString(serviceAdvice.PodMemoryLimit));
            }

            controller.ThrowIfCancelled();
            await master.InvokeIdempotentAsync("setup/db-credentials-admin",
                async () =>
                {
                    var username = KubeConst.NeonSystemDbAdminUser;
                    var password = NeonHelper.GetCryptoRandomPassword(cluster.Definition.Security.PasswordLength);

                    var secret = new V1Secret()
                    {
                        Metadata = new V1ObjectMeta()
                        {
                            Name        = KubeConst.NeonSystemDbAdminSecret,
                            Annotations = new Dictionary<string, string>()
                            {
                                {  "reloader.stakater.com/match", "true" }
                            }
                        },
                        Type       = "Opaque",
                        StringData = new Dictionary<string, string>()
                        {
                            { "username", username },
                            { "password", password }
                        }
                    };

                    await k8s.CreateNamespacedSecretAsync(secret, KubeNamespace.NeonSystem);
                });

            controller.ThrowIfCancelled();
            await master.InvokeIdempotentAsync("setup/db-credentials-service",
                async () =>
                {
                    var username = KubeConst.NeonSystemDbServiceUser;
                    var password = NeonHelper.GetCryptoRandomPassword(cluster.Definition.Security.PasswordLength);

                    var secret = new V1Secret()
                    {
                        Metadata = new V1ObjectMeta()
                        {
                            Name        = KubeConst.NeonSystemDbServiceSecret,
                            Annotations = new Dictionary<string, string>()
                            {
                                {  "reloader.stakater.com/match", "true" }
                            }
                        },
                        Type       = "Opaque",
                        StringData = new Dictionary<string, string>()
                        {
                            { "username", username },
                            { "password", password }
                        }
                    };

                    await k8s.CreateNamespacedSecretAsync(secret, KubeNamespace.NeonSystem);
                });

            controller.ThrowIfCancelled();
            await master.InvokeIdempotentAsync("setup/system-db",
                async () =>
                {
                    controller.LogProgress(master, verb: "setup", message: "neon-system-db");

                    values.Add($"replicas", serviceAdvice.ReplicaCount);
                    values.Add("serviceMesh.enabled", cluster.Definition.Features.ServiceMesh);
                    values.Add("healthCheck.image.tag", KubeVersions.NeonKubeContainerImageTag);

                    int i = 0;

                    foreach (var taint in await GetTaintsAsync(controller, NodeLabels.LabelNeonSystemDb, "true"))
                    {
                        values.Add($"tolerations[{i}].key", $"{taint.Key.Split("=")[0]}");
                        values.Add($"tolerations[{i}].effect", taint.Effect);
                        values.Add($"tolerations[{i}].operator", "Exists");
                        i++;
                    }

                    // We're going to set the pod priority class to the same value as 
                    // the postgres operator.

                    values.Add("podPriorityClassName", PriorityClass.NeonData.Name);

                    await master.InstallHelmChartAsync(controller, "postgres-operator",
                        releaseName:     "neon-system-db",
                        @namespace:      KubeNamespace.NeonSystem,
                        prioritySpec:    PriorityClass.NeonData.Name,
                        values:          values,
                        progressMessage: "neon-system-db");
                });

            controller.ThrowIfCancelled();
            await master.InvokeIdempotentAsync("setup/system-db-ready",
                async () =>
                {
                    controller.LogProgress(master, verb: "wait for", message: "neon-system-db");

                    await NeonHelper.WaitAllAsync(
                        new List<Task>()
                        {
                            k8s.WaitForDeploymentAsync(KubeNamespace.NeonSystem, "neon-system-db-postgres-operator", timeout: clusterOpTimeout, pollInterval: clusterOpPollInterval, cancellationToken: controller.CancellationToken),
                            k8s.WaitForStatefulSetAsync(KubeNamespace.NeonSystem, "neon-system-db", timeout: clusterOpTimeout, pollInterval: clusterOpPollInterval, cancellationToken: controller.CancellationToken),
                        });
                });
        }

        /// <summary>
        /// Installs Keycloak.
        /// </summary>
        /// <param name="controller">The setup controller.</param>
        /// <param name="master">The master node where the operation will be performed.</param>
        /// <returns>The tracking <see cref="Task"/>.</returns>
        public static async Task InstallSsoAsync(ISetupController controller, NodeSshProxy<NodeDefinition> master)
        {
            await SyncContext.Clear;
            Covenant.Requires<ArgumentNullException>(controller != null, nameof(controller));
            Covenant.Requires<ArgumentNullException>(master != null, nameof(master));

            controller.ThrowIfCancelled();
            await InstallGlauthAsync(controller, master);

            controller.ThrowIfCancelled();
            await InstallDexAsync(controller, master);

            controller.ThrowIfCancelled();
            await InstallNeonSsoProxyAsync(controller, master);

            controller.ThrowIfCancelled();
            await InstallOauth2ProxyAsync(controller, master);
        }

        /// <summary>
        /// Installs Dex.
        /// </summary>
        /// <param name="controller">The setup controller.</param>
        /// <param name="master">The master node where the operation will be performed.</param>
        /// <returns>The tracking <see cref="Task"/>.</returns>
        public static async Task InstallDexAsync(ISetupController controller, NodeSshProxy<NodeDefinition> master)
        {
            await SyncContext.Clear;
            Covenant.Requires<ArgumentNullException>(controller != null, nameof(controller));
            Covenant.Requires<ArgumentNullException>(master != null, nameof(master));

            var cluster       = controller.Get<ClusterProxy>(KubeSetupProperty.ClusterProxy);
            var k8s           = GetK8sClient(controller);
            var clusterAdvice = controller.Get<KubeClusterAdvice>(KubeSetupProperty.ClusterAdvice);
            var serviceAdvice = clusterAdvice.GetServiceAdvice(KubeClusterAdvice.Dex);
            var serviceUser   = await KubeHelper.GetClusterLdapUserAsync(k8s, "serviceuser");

            var values = new Dictionary<string, object>();

            values.Add("cluster.name", cluster.Definition.Name);
            values.Add("cluster.domain", cluster.Definition.Domain);
            values.Add("neonkube.clusterDomain.grafana", ClusterDomain.Grafana);
            values.Add("neonkube.clusterDomain.kiali", ClusterDomain.Kiali);
            values.Add("neonkube.clusterDomain.minio", ClusterDomain.Minio);
            values.Add("neonkube.clusterDomain.harborRegistry", ClusterDomain.HarborRegistry);
            values.Add("neonkube.clusterDomain.neonDashboard", ClusterDomain.NeonDashboard);
            values.Add("neonkube.clusterDomain.kubernetesDashboard", ClusterDomain.KubernetesDashboard);
            values.Add("neonkube.clusterDomain.sso", ClusterDomain.Sso);
            values.Add("serviceMesh.enabled", cluster.Definition.Features.ServiceMesh);

            values.Add("secrets.grafana", NeonHelper.GetCryptoRandomPassword(cluster.Definition.Security.PasswordLength));
            values.Add("secrets.harbor", NeonHelper.GetCryptoRandomPassword(cluster.Definition.Security.PasswordLength));
            values.Add("secrets.kubernetes", NeonHelper.GetCryptoRandomPassword(cluster.Definition.Security.PasswordLength));
            values.Add("secrets.minio", NeonHelper.GetCryptoRandomPassword(cluster.Definition.Security.PasswordLength));
            values.Add("secrets.ldap", serviceUser.Password);

            values.Add("config.issuer", $"https://{ClusterDomain.Sso}.{cluster.Definition.Domain}");

            // LDAP

            var baseDN = $@"dc={string.Join($@"\,dc=", cluster.Definition.Domain.Split('.'))}";

            values.Add("config.ldap.bindDN", $@"cn=serviceuser\,ou=admin\,{baseDN}");
            values.Add("config.ldap.userSearch.baseDN", $@"cn=users\,{baseDN}");
            values.Add("config.ldap.groupSearch.baseDN", $@"ou=users\,{baseDN}");

            if (serviceAdvice.PodMemoryRequest.HasValue && serviceAdvice.PodMemoryLimit.HasValue)
            {
                values.Add($"resources.requests.memory", ToSiString(serviceAdvice.PodMemoryRequest));
                values.Add($"resources.limits.memory", ToSiString(serviceAdvice.PodMemoryLimit));
            }

            controller.ThrowIfCancelled();
            await master.InvokeIdempotentAsync("setup/dex-install",
                async () =>
                {
                    await master.InstallHelmChartAsync(controller, "dex",
                        releaseName:     "dex",
                        @namespace:      KubeNamespace.NeonSystem,
                        prioritySpec:    PriorityClass.NeonApi.Name,
                        values:          values,
                        progressMessage: "dex");
                });

            controller.ThrowIfCancelled();
            await master.InvokeIdempotentAsync("setup/dex-ready",
                async () =>
                {
                    controller.LogProgress(master, verb: "wait for", message: "neon-sso");

                    await k8s.WaitForDeploymentAsync(KubeNamespace.NeonSystem, "neon-sso-dex", timeout: clusterOpTimeout, pollInterval: clusterOpPollInterval, cancellationToken: controller.CancellationToken);
                });
        }

        /// <summary>
        /// Installs Neon SSO Session Proxy.
        /// </summary>
        /// <param name="controller">The setup controller.</param>
        /// <param name="master">The master node where the operation will be performed.</param>
        /// <returns>The tracking <see cref="Task"/>.</returns>
        public static async Task InstallNeonSsoProxyAsync(ISetupController controller, NodeSshProxy<NodeDefinition> master)
        {
            await SyncContext.Clear;
            Covenant.Requires<ArgumentNullException>(controller != null, nameof(controller));
            Covenant.Requires<ArgumentNullException>(master != null, nameof(master));

            var cluster       = controller.Get<ClusterProxy>(KubeSetupProperty.ClusterProxy);
            var k8s           = GetK8sClient(controller);
            var clusterAdvice = controller.Get<KubeClusterAdvice>(KubeSetupProperty.ClusterAdvice);
            var serviceAdvice = clusterAdvice.GetServiceAdvice(KubeClusterAdvice.NeonSsoSessionProxy);
            var values        = new Dictionary<string, object>();

            values.Add("image.organization", KubeConst.LocalClusterRegistry);
            values.Add("image.tag", KubeVersions.NeonKubeContainerImageTag);
            values.Add("cluster.name", cluster.Definition.Name);
            values.Add("cluster.domain", cluster.Definition.Domain);
            values.Add("neonkube.clusterDomain.sso", ClusterDomain.Sso);
            values.Add("secrets.cipherKey", AesCipher.GenerateKey(256));
            values.Add($"metrics.enabled", serviceAdvice.MetricsEnabled ?? clusterAdvice.MetricsEnabled);
            values.Add("serviceMesh.enabled", cluster.Definition.Features.ServiceMesh);

            if (serviceAdvice.PodMemoryRequest.HasValue && serviceAdvice.PodMemoryLimit.HasValue)
            {
                values.Add($"resources.requests.memory", ToSiString(serviceAdvice.PodMemoryRequest));
                values.Add($"resources.limits.memory", ToSiString(serviceAdvice.PodMemoryLimit));
            }

            controller.ThrowIfCancelled();
            await master.InvokeIdempotentAsync("setup/neon-sso-session-proxy-install",
                async () =>
                {
                    await master.InstallHelmChartAsync(controller, "neon-sso-session-proxy",
                        releaseName:  "neon-sso-session-proxy",
                        @namespace:   KubeNamespace.NeonSystem,
                        prioritySpec: PriorityClass.NeonNetwork.Name,
                        values:       values);
                });

            controller.ThrowIfCancelled();
            await master.InvokeIdempotentAsync("setup/neon-sso-proxy-ready",
                async () =>
                {
                    controller.LogProgress(master, verb: "wait for", message: "neon-sso-session-proxy");

                    await k8s.WaitForDeploymentAsync(KubeNamespace.NeonSystem, "neon-sso-session-proxy", timeout: clusterOpTimeout, pollInterval: clusterOpPollInterval, cancellationToken: controller.CancellationToken);
                });
        }

        /// <summary>
        /// Installs Glauth.
        /// </summary>
        /// <param name="controller">The setup controller.</param>
        /// <param name="master">The master node where the operation will be performed.</param>
        /// <returns>The tracking <see cref="Task"/>.</returns>
        public static async Task InstallGlauthAsync(ISetupController controller, NodeSshProxy<NodeDefinition> master)
        {
            await SyncContext.Clear;
            Covenant.Requires<ArgumentNullException>(controller != null, nameof(controller));
            Covenant.Requires<ArgumentNullException>(master != null, nameof(master));

            var cluster       = controller.Get<ClusterProxy>(KubeSetupProperty.ClusterProxy);
            var clusterLogin  = controller.Get<ClusterLogin>(KubeSetupProperty.ClusterLogin);
            var k8s           = GetK8sClient(controller);
            var clusterAdvice = controller.Get<KubeClusterAdvice>(KubeSetupProperty.ClusterAdvice);
            var serviceAdvice = clusterAdvice.GetServiceAdvice(KubeClusterAdvice.Glauth);
            var values        = new Dictionary<string, object>();
            var dbSecret      = await k8s.ReadNamespacedSecretAsync(KubeConst.NeonSystemDbServiceSecret, KubeNamespace.NeonSystem);
            var dbPassword    = Encoding.UTF8.GetString(dbSecret.Data["password"]);

            values.Add("cluster.name", cluster.Definition.Name);
            values.Add("cluster.domain", cluster.Definition.Domain);
            values.Add("serviceMesh.enabled", cluster.Definition.Features.ServiceMesh);

            values.Add("config.backend.baseDN", $"dc={string.Join($@"\,dc=", cluster.Definition.Domain.Split('.'))}");
            values.Add("config.backend.database.user", KubeConst.NeonSystemDbServiceUser);
            values.Add("config.backend.database.password", dbPassword);

            values.Add("users.root.password", clusterLogin.SsoPassword);
            values.Add("users.serviceuser.password", NeonHelper.GetCryptoRandomPassword(cluster.Definition.Security.PasswordLength));

            if (serviceAdvice.PodMemoryRequest.HasValue && serviceAdvice.PodMemoryLimit.HasValue)
            {
                values.Add($"resources.requests.memory", ToSiString(serviceAdvice.PodMemoryRequest));
                values.Add($"resources.limits.memory", ToSiString(serviceAdvice.PodMemoryLimit));
            }

            controller.ThrowIfCancelled();
            await master.InvokeIdempotentAsync("setup/glauth-install",
                async () =>
                {
                    await master.InstallHelmChartAsync(controller, "glauth",
                        releaseName:  "glauth",
                        @namespace:   KubeNamespace.NeonSystem,
                        prioritySpec: PriorityClass.NeonApp.Name,
                        values:       values);
                });

            controller.ThrowIfCancelled();
            await master.InvokeIdempotentAsync("setup/glauth-ready",
                async () =>
                {
                    controller.LogProgress(master, verb: "wait for", message: "glauth");

                    await k8s.WaitForDeploymentAsync(KubeNamespace.NeonSystem, "neon-sso-glauth", timeout: clusterOpTimeout, pollInterval: clusterOpPollInterval, cancellationToken: controller.CancellationToken);

                    // Wait for the [glauth postgres.so] plugin to initialize its database
                    // by quering the three tables we'll be modifying later below.  The database
                    // will be ready when these queries succeed.

                    controller.ThrowIfCancelled();
                    await NeonHelper.WaitForAsync(
                        async () =>
                        {
                            // Verify [groups] table.

                            var result = await cluster.ExecSystemDbCommandAsync("glauth", "SELECT * FROM groups;", noSuccessCheck: true);

                            if (result.ExitCode != 0)
                            {
                                return false;
                            }

                            // Verify [users] table.

                            controller.ThrowIfCancelled();

                            result = await cluster.ExecSystemDbCommandAsync("glauth", "SELECT * FROM users;", noSuccessCheck: true);

                            if (result.ExitCode != 0)
                            {
                                return false;
                            }

                            // Verify [capabilities] table.

                            controller.ThrowIfCancelled();

                            result = await cluster.ExecSystemDbCommandAsync("glauth", "SELECT * FROM capabilities;", noSuccessCheck: true);

                            if (result.ExitCode != 0)
                            {
                                return false;
                            }

                            return true;
                        },
                        timeout:           clusterOpTimeout, 
                        pollInterval:      clusterOpPollInterval,
                        cancellationToken: controller.CancellationToken);
                });

            controller.ThrowIfCancelled();
            await master.InvokeIdempotentAsync("setup/glauth-users",
                async () =>
                {
                    controller.LogProgress(master, verb: "create", message: "glauth users");

                    var users  = await k8s.ReadNamespacedSecretAsync("glauth-users", KubeNamespace.NeonSystem);
                    var groups = await k8s.ReadNamespacedSecretAsync("glauth-groups", KubeNamespace.NeonSystem);

                    foreach (var key in groups.Data.Keys)
                    {
                        var group = NeonHelper.YamlDeserialize<GlauthGroup>(Encoding.UTF8.GetString(groups.Data[key]));

                        controller.ThrowIfCancelled();
                        await cluster.ExecSystemDbCommandAsync("glauth",
                            $@"INSERT INTO groups(name, gidnumber)
                                   VALUES('{group.Name}','{group.GidNumber}') 
                                       ON CONFLICT (name) DO UPDATE
                                           SET gidnumber = '{group.GidNumber}';");
                    }

                    foreach (var user in users.Data.Keys)
                    {
                        var userData     = NeonHelper.YamlDeserialize<GlauthUser>(Encoding.UTF8.GetString(users.Data[user]));
                        var name         = userData.Name;
                        var givenname    = userData.Name;
                        var mail         = $"{userData.Name}@{cluster.Definition.Domain}";
                        var uidnumber    = userData.UidNumber;
                        var primarygroup = userData.PrimaryGroup;
                        var passsha256   = CryptoHelper.ComputeSHA256String(userData.Password);

                        controller.ThrowIfCancelled();
                        await cluster.ExecSystemDbCommandAsync("glauth",
                             $@"INSERT INTO users(name, givenname, mail, uidnumber, primarygroup, passsha256)
                                    VALUES('{name}','{givenname}','{mail}','{uidnumber}','{primarygroup}','{passsha256}')
                                        ON CONFLICT (name) DO UPDATE
                                            SET givenname    = '{givenname}',
                                                mail         = '{mail}',
                                                uidnumber    = '{uidnumber}',
                                                primarygroup = '{primarygroup}',
                                                passsha256   = '{passsha256}';");

                        if (userData.Capabilities != null)
                        {
                            foreach (var capability in userData.Capabilities)
                            {
                                controller.ThrowIfCancelled();
                                await cluster.ExecSystemDbCommandAsync("glauth",
                                    $@"INSERT INTO capabilities(userid, action, object)
                                           VALUES('{uidnumber}','{capability.Action}','{capability.Object}');");
                            }
                        }
                    }
                });
        }

        /// <summary>
        /// Installs Oauth2-proxy.
        /// </summary>
        /// <param name="controller">The setup controller.</param>
        /// <param name="master">The master node where the operation will be performed.</param>
        /// <returns>The tracking <see cref="Task"/>.</returns>
        public static async Task InstallOauth2ProxyAsync(ISetupController controller, NodeSshProxy<NodeDefinition> master)
        {
            await SyncContext.Clear;
            Covenant.Requires<ArgumentNullException>(controller != null, nameof(controller));
            Covenant.Requires<ArgumentNullException>(master != null, nameof(master));

            var cluster       = controller.Get<ClusterProxy>(KubeSetupProperty.ClusterProxy);
            var k8s           = GetK8sClient(controller);
            var clusterAdvice = controller.Get<KubeClusterAdvice>(KubeSetupProperty.ClusterAdvice);
            var serviceAdvice = clusterAdvice.GetServiceAdvice(KubeClusterAdvice.Oauth2Proxy);

            controller.ThrowIfCancelled();
            await master.InvokeIdempotentAsync("setup/oauth2-proxy",
                async () =>
                {
                    controller.LogProgress(master, verb: "setup", message: "oauth2 proxy");

                    var values = new Dictionary<string, object>();

                    values.Add("cluster.name", cluster.Definition.Name);
                    values.Add("cluster.domain", cluster.Definition.Domain);
                    values.Add("config.cookieSecret", NeonHelper.ToBase64(NeonHelper.GetCryptoRandomPassword(24)));
                    values.Add("neonkube.clusterDomain.sso", ClusterDomain.Sso);
                    values.Add($"metrics.enabled", serviceAdvice.MetricsEnabled ?? clusterAdvice.MetricsEnabled);
                    values.Add($"metrics.servicemonitor.interval", serviceAdvice.MetricsInterval ?? clusterAdvice.MetricsInterval);

                    await master.InstallHelmChartAsync(controller, "oauth2-proxy",
                        releaseName:     "neon-sso",
                        @namespace:      KubeNamespace.NeonSystem,
                        prioritySpec:    PriorityClass.NeonApi.Name,
                        values:          values,
                        progressMessage: "neon-sso-oauth2-proxy");
                });
        }

        /// <summary>
        /// Returns the Postgres connection string for the default database for the
        /// cluster's <see cref="KubeService.NeonSystemDb"/> deployment.
        /// </summary>
        /// <param name="controller">The setup controller.</param>
        /// <returns>The connection string.</returns>
        public static async Task<string> GetSystemDatabaseConnectionStringAsync(ISetupController controller)
        {
            await SyncContext.Clear;
            Covenant.Requires<ArgumentNullException>(controller != null, nameof(controller));

            var k8s        = GetK8sClient(controller);
            var secret     = await k8s.ReadNamespacedSecretAsync(KubeConst.NeonSystemDbAdminSecret, KubeNamespace.NeonSystem);
            var username   = Encoding.UTF8.GetString(secret.Data["username"]);
            var password   = Encoding.UTF8.GetString(secret.Data["password"]);
            var dbHost     = KubeService.NeonSystemDb;
            var dbPort     = NetworkPorts.Postgres;
            var connString = $"Host={dbHost};Port={dbPort};Username={username};Password={password};Database=postgres";

            if (controller.Get<bool>(KubeSetupProperty.Redact, true))
            {
                controller.LogGlobal($"System database connection string: [{connString.Replace(password, "REDACTED")}]");
            }
            else
            {
                controller.LogGlobal($"System database connection string: [{connString}]");
            }

            return connString;
        }

        /// <summary>
        /// Creates a Minio bucket by using the mc client on one of the minio server pods.
        /// </summary>
        /// <param name="controller">The setup controller.</param>
        /// <param name="master">The master node where the operation will be performed.</param>
        /// <param name="name">The new bucket name.</param>
        /// <param name="quota">The bucket quota.</param>
        /// <returns>The tracking <see cref="Task"/>.</returns>
        public static async Task CreateMinioBucketAsync(ISetupController controller, NodeSshProxy<NodeDefinition> master, string name, string quota = null)
        {
            await SyncContext.Clear;
            Covenant.Requires<ArgumentNullException>(controller != null, nameof(controller));
            Covenant.Requires<ArgumentNullException>(master != null, nameof(master));
            Covenant.Requires<ArgumentNullException>(!string.IsNullOrEmpty(name), nameof(name));

            master.Status = $"create: [{name}] minio bucket";

            var cluster = controller.Get<ClusterProxy>(KubeSetupProperty.ClusterProxy);
            var minioSecret = await GetK8sClient(controller).ReadNamespacedSecretAsync("minio", KubeNamespace.NeonSystem);
            var accessKey = Encoding.UTF8.GetString(minioSecret.Data["accesskey"]);
            var secretKey = Encoding.UTF8.GetString(minioSecret.Data["secretkey"]);
            var k8s = GetK8sClient(controller);

            controller.ThrowIfCancelled();
            await master.InvokeIdempotentAsync($"setup/minio-bucket-{name}",
                async () =>
                {
                    await cluster.ExecMinioCommandAsync(
                        retryPolicy: podExecRetry,
                        mcCommand: $"mb minio/{name}");
                });

            controller.ThrowIfCancelled();
            if (!string.IsNullOrEmpty(quota))
            {
                await master.InvokeIdempotentAsync($"setup/minio-bucket-{name}-quota",
                    async () =>
                    {
                        await cluster.ExecMinioCommandAsync(
                            retryPolicy: podExecRetry,
                            mcCommand: $"admin bucket quota minio/{name} --hard {quota}");
                    });
            }
        }

        /// <summary>
        /// Creates a <see cref="V1NeonDashboard"/> idempotently.
        /// </summary>
        /// <param name="controller">The setup controller.</param>
        /// <param name="master">The master node where the operation will be performed.</param>
        /// <param name="name">The new bucket name.</param>
        /// <param name="url">The dashboard URL</param>
        /// <param name="displayName">The Dashboard display name.</param>
        /// <param name="enabled">Optionally specify whether the dashboard is enabled.</param>
        /// <param name="displayOrder">Optionally specify the display order.</param>
        /// <returns>The tracking <see cref="Task"/>.</returns>
        public static async Task CreateNeonDashboardAsync(
            ISetupController controller, 
            NodeSshProxy<NodeDefinition> master, 
            string name,
            string url, 
            string displayName  = null,
            bool   enabled      = true,
            int    displayOrder = int.MaxValue)
        {
            await SyncContext.Clear;

            Covenant.Requires<ArgumentNullException>(controller != null, nameof(controller));
            Covenant.Requires<ArgumentNullException>(master != null, nameof(master));
            Covenant.Requires<ArgumentNullException>(!string.IsNullOrEmpty(name), nameof(name));
            Covenant.Requires<ArgumentNullException>(!string.IsNullOrEmpty(url), nameof(url));

            master.Status = $"create: [{name}] dashboard CRD";

            var cluster     = controller.Get<ClusterProxy>(KubeSetupProperty.ClusterProxy);
            var k8s         = GetK8sClient(controller);

            var dashboard = new V1NeonDashboard()
            {
                Metadata = new V1ObjectMeta()
                {
                    Name = name
                },
                Spec = new V1NeonDashboard.NeonDashboardSpec()
                {
                    DisplayName  = displayName, 
                    Enabled      = enabled,
                    DisplayOrder = displayOrder,
                    Url          = url
                }
            };

            controller.ThrowIfCancelled();
            await master.InvokeIdempotentAsync($"setup/neon-dashboard-{name}",
                async () =>
                {
                    await k8s.CreateClusterCustomObjectAsync<V1NeonDashboard>(dashboard, dashboard.Name());
                });
        }


        /// <summary>
        /// Writes the <see cref="KubeConfigMapName.ClusterInfo"/>
        /// config map to the <see cref="KubeNamespace.NeonStatus"/> namespace.
        /// </summary>
        /// <param name="controller">The setup controller.</param>
        /// <param name="master">The master node where the operation will be performed.</param>
        /// <returns>The tracking <see cref="Task"/>.</returns>
        public static async Task WriteClusterInfoAsync(ISetupController controller, NodeSshProxy<NodeDefinition> master)
        {
            await SyncContext.Clear;

            Covenant.Requires<ArgumentNullException>(controller != null, nameof(controller));

            var cluster = controller.Get<ClusterProxy>(KubeSetupProperty.ClusterProxy);
            var k8s     = GetK8sClient(controller);

            await master.InvokeIdempotentAsync("setup/cluster-info",
                (Func<Task>)(async () =>
                {
                    var clusterInfoMap = new TypeSafeConfigMap<ClusterInfo>(
                        name:       KubeConfigMapName.ClusterInfo,
                        @namespace: KubeNamespace.NeonStatus,
                        config:     new ClusterInfo(cluster.Definition));

                    await k8s.CreateNamespacedConfigMapAsync(clusterInfoMap.ConfigMap, KubeNamespace.NeonStatus);
                }));
        }

        /// <summary>
        /// Writes the <see cref="KubeConfigMapName.ClusterHealth"/> and <see cref="KubeConfigMapName.ClusterLock"/> 
        /// config maps to the <see cref="KubeNamespace.NeonStatus"/> namespace.
        /// </summary>
        /// <param name="controller">The setup controller.</param>
        /// <param name="master">The master node where the operation will be performed.</param>
        /// <returns>The tracking <see cref="Task"/>.</returns>
        public static async Task WriteClusterConfigMapsAsync(ISetupController controller, NodeSshProxy<NodeDefinition> master)
        {
            await SyncContext.Clear;

            Covenant.Requires<ArgumentNullException>(controller != null, nameof(controller));

            var cluster = controller.Get<ClusterProxy>(KubeSetupProperty.ClusterProxy);
            var k8s     = GetK8sClient(controller);

            await master.InvokeIdempotentAsync("setup/cluster-lock",
                async () =>
                {
                    var clusterLockMap = new TypeSafeConfigMap<ClusterLock>(
                        name:       KubeConfigMapName.ClusterLock,
                        @namespace: KubeNamespace.NeonStatus,
                        config:     new ClusterLock()
                        {
                            IsLocked = cluster.Definition.IsLocked,
                        });

                    await k8s.CreateNamespacedConfigMapAsync(clusterLockMap.ConfigMap, KubeNamespace.NeonStatus);
                });

            await master.InvokeIdempotentAsync("setup/cluster-health",
                (Func<Task>)(async () =>
                {
                    var clusterHealthMap = new TypeSafeConfigMap<ClusterHealth>(
                        name:       KubeConfigMapName.ClusterHealth,
                        @namespace: KubeNamespace.NeonStatus,
                        config:     new ClusterHealth()
                        {
                            State   = ClusterState.Healthy,
                            Summary = "Cluster is healthy"
                        });

                    await k8s.CreateNamespacedConfigMapAsync(clusterHealthMap.ConfigMap, KubeNamespace.NeonStatus);
                }));
        }

        /// <summary>
        /// Converts a <c>decimal</c> into a nice byte units string.
        /// </summary>
        /// <param name="value">The input value (or <c>null</c>).</param>
        /// <returns>The formatted output (or <c>null</c>).</returns>
        public static string ToSiString(decimal? value)
        {
            if (!value.HasValue)
            {
                return null;
            }

            return new ResourceQuantity(value.GetValueOrDefault(), 0, ResourceQuantity.SuffixFormat.BinarySI).CanonicalizeString();
        }

        /// <summary>
        /// Converts a <c>double</c> value into a nice byte units string.
        /// </summary>
        /// <param name="value">The input value (or <c>null</c>).</param>
        /// <returns>The formatted output (or <c>null</c>).</returns>
        public static string ToSiString(double? value)
        {
            if (!value.HasValue)
            {
                return null;
            }

            return new ResourceQuantity((decimal)value.GetValueOrDefault(), 0, ResourceQuantity.SuffixFormat.BinarySI).CanonicalizeString();
        }
    }
}
