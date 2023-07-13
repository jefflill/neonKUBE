//-----------------------------------------------------------------------------
// FILE:        KubeTestHelper.cs
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
using System.Linq;
using System.Text;
using System.Threading.Tasks;

using Grpc.Core;

using k8s.Models;

using Neon.Common;
using Neon.IO;
using Neon.Kube;
using Neon.Kube.Xunit;
using Neon.Xunit;

using Xunit;

namespace Neon.Kube.Xunit
{
    /// <summary>
    /// Misc local unit test helpers.
    /// </summary>
    public static class KubeTestHelper
    {
        /// <summary>
        /// <b>ghcr.io/neonrelease/test</b> image test user name.
        /// </summary>
        public const string TestUsername = "test";

        /// <summary>
        /// <b>ghcr.io/neonrelease/test</b> image test user ID.
        /// </summary>
        public const string TestUID = "5555";

        /// <summary>
        /// <b>ghcr.io/neonrelease/test</b> image test group ID.
        /// </summary>
        public const string TestGID = "6666";

        /// <summary>
        /// Creates and optionally populates a temporary test folder with test files.
        /// </summary>
        /// <param name="files">
        /// The files to be created.  The first item in each tuple entry will be 
        /// the local file name and the second the contents of the file.
        /// </param>
        /// <returns>The <see cref="TempFolder"/>.</returns>
        /// <remarks>
        /// <note>
        /// Ensure that the <see cref="TempFolder"/> returned is disposed so it and
        /// any files within will be deleted.
        /// </note>
        /// </remarks>
        public static TempFolder CreateTestFolder(params Tuple<string, string>[] files)
        {
            var folder = new TempFolder();

            if (files != null)
            {
                foreach (var file in files)
                {
                    File.WriteAllText(Path.Combine(folder.Path, file.Item1), file.Item2 ?? string.Empty);
                }
            }

            return folder;
        }

        /// <summary>
        /// Creates and populates a temporary test folder with a test file.
        /// </summary>
        /// <param name="data">The file name</param>
        /// <param name="filename">The file data.</param>
        /// <returns>The <see cref="TempFolder"/>.</returns>
        /// <remarks>
        /// <note>
        /// Ensure that the <see cref="TempFolder"/> returned is disposed so it and
        /// any files within will be deleted.
        /// </note>
        /// </remarks>
        public static TempFolder TempFolder(string filename, string data)
        {
            Covenant.Requires<ArgumentNullException>(!string.IsNullOrEmpty(filename), nameof(filename));

            var folder = new TempFolder();

            File.WriteAllText(Path.Combine(folder.Path, filename), data ?? string.Empty);

            return folder;
        }

        /// <summary>
        /// Starts and returns a <see cref="KubeTestManager"/> instance.  This will put <see cref="KubeHelper"/>
        /// into test mode.  You must dispose the instance before the tests complete to revert back
        /// to normal mode.
        /// </summary>
        /// <returns>The <see cref="KubeTestManager"/>.</returns>
        public static KubeTestManager KubeTestManager()
        {
            return new KubeTestManager();
        }

        /// <summary>
        /// Call this method in your unit test, passing the type of your test class to clear
        /// any existing logs captured from a previous test run.
        /// </summary>
        /// <param name="testClassType">Identifies the unit test class type.</param>
        /// <param name="testName">Identifies the unit test.</param>
        /// <remarks>
        /// <para>
        /// This is used in conjunction with <see cref="CaptureDeploymentLogsAndThrow(Exception, Type, string, int?)"/>
        /// to capture deployment logs on test failures.  This method handles the removal of any
        /// logs captured from previous test runs and <see cref="CaptureDeploymentLogsAndThrow(Exception, Type, string, int?)"/>
        /// is called when tests fail, capturing the logs and then wraps the test exception in a
        /// <see cref="NeonKubeException"/> and throws that.
        /// </para>
        /// <para>These methods use the <b>NEONTEST_CLUSTERDEPLOYMENT_classname_testname</b> environment
        /// variable to coordinate, where <b>classname</b> is the fully qualified name of the test class
        /// and <b>testname</b> identifies the test.  This environment variable is scoped to the test
        /// runner process.  The <b>NEONTEST_CLUSTERDEPLOYMENT_*</b> environment variable won't exist
        /// the first time this method is called for a specific test class/test.  This method removes
        /// any logs captured by previous runs of these tests when the variable isn't present and then
        /// sets the environment variable so subsequent calls in the context of the current test runner
        /// won't clear logs.
        /// </para>
        /// </remarks>
        public static void CleanDeploymentLogs(Type testClassType, string testName)
        {
            Covenant.Requires<ArgumentNullException>(testClassType != null, nameof(testClassType));
            Covenant.Requires<ArgumentNullException>(!string.IsNullOrEmpty(testName), nameof(testName));

            var variable      = $"NEONTEST_CLUSTERDEPLOYMENT_{testClassType.FullName}_{testName}";
            var archiveFolder = Path.Combine(KubeHelper.DevelopmentFolder, "test-logs", $"{testClassType.FullName}.{testName}");

            if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable(variable)))
            {
                NeonHelper.DeleteFolder(archiveFolder);
            }

            Environment.SetEnvironmentVariable(variable, "CLEANED");
        }

        /// <summary>
        /// Used by cluster deployment related unit tests to capture the deployment logs
        /// for deployment failures.
        /// </summary>
        /// <param name="e">Specifies the exception that reported the failure.</param>
        /// <param name="testClassType">Identifies the unit test class type.</param>
        /// <param name="testName">Identifies the unit test.</param>
        /// <param name="iteration">Optionally specifies the test iteration.</param>
        /// <remarks>
        /// <para>
        /// Deployment logs will be copied to the user's <b>~/.neonkube-dev/test-logs/classname.testname[/iteration]</b>
        /// folder.
        /// </para>
        /// <para>
        /// This is pretty easy to use:
        /// </para>
        /// <list type="number">
        /// <item>
        /// Call <see cref="CleanDeploymentLogs(Type, string)"/> at the top of your test
        /// methods, passing your test class type as well as the test method's name.
        /// </item>
        /// <item>
        /// Wrap your test code in a <c>try/catch</c> and call this method in the exception handler.
        /// </item>
        /// </list>
        /// <para>
        /// That's all there is to it.  The <see cref="CleanDeploymentLogs(Type, string)"/>
        /// call will clear any previously captured logs the first time it's called by
        /// the test runner and will do nothing thereafter.  When called, this method
        /// copies the deployment log files from <b>~/.neonkube/logs/**</b> to the
        /// folder named like <b>~/.neonkube-dev/test-logs/...</b> folder and then it
        /// throws a <see cref="NeonKubeException"/> wrapping <paramref name="e"/>.
        /// </para>
        /// </remarks>
        public static void CaptureDeploymentLogsAndThrow(Exception e, Type testClassType, string testName, int? iteration = null)
        {
            Covenant.Requires<ArgumentNullException>(e != null, nameof(e));
            Covenant.Requires<ArgumentNullException>(testClassType != null, nameof(testClassType));
            Covenant.Requires<ArgumentNullException>(!string.IsNullOrEmpty(testName), nameof(testName));

            // Capture the deployment logs.

            var archiveFolder = Path.Combine(KubeHelper.DevelopmentFolder, "test-logs", $"{testClassType.FullName}.{testName}");

            if (iteration.HasValue)
            {
                archiveFolder = Path.Combine(archiveFolder, iteration.ToString());
            }

            Directory.CreateDirectory(archiveFolder);
            NeonHelper.CopyFolder(KubeHelper.LogFolder, archiveFolder);

            var detailsFolder = Path.Combine(archiveFolder, "details");

            Directory.CreateDirectory(detailsFolder);

            // Capture additional information about the cluster state, including pod status, pod logs,
            // as well as deployment, daemonset, and satefulset status.  Note that we're going to
            // capture basic high-level information as text as well as detailed YAML status information.

            // Capture current pod status.

            var response = NeonHelper.ExecuteCapture(KubeHelper.NeonCliPath, new object[] { "get", "pods", "-A" });

            File.WriteAllText(Path.Combine(detailsFolder, "pods.txt"), response.OutputText);

            response = NeonHelper.ExecuteCapture(KubeHelper.NeonCliPath, new object[] { "get", "pods", "-A", "-o=yaml" });

            File.WriteAllText(Path.Combine(detailsFolder, "pods.yaml"), response.OutputText);

            // Capture current deployment status.

            response = NeonHelper.ExecuteCapture(KubeHelper.NeonCliPath, new object[] { "get", "deployments", "-A" });

            File.WriteAllText(Path.Combine(detailsFolder, "deployments.txt"), response.OutputText);

            response = NeonHelper.ExecuteCapture(KubeHelper.NeonCliPath, new object[] { "get", "deployments", "-A", "-o=yaml" });

            File.WriteAllText(Path.Combine(detailsFolder, "deployments.yaml"), response.OutputText);

            // Capture current statefulset status.

            response = NeonHelper.ExecuteCapture(KubeHelper.NeonCliPath, new object[] { "get", "statefulsets", "-A" });

            File.WriteAllText(Path.Combine(detailsFolder, "statefulsets.txt"), response.OutputText);

            response = NeonHelper.ExecuteCapture(KubeHelper.NeonCliPath, new object[] { "get", "statefulsets", "-A", "-o=yaml" });

            File.WriteAllText(Path.Combine(detailsFolder, "statefulsets.yaml"), response.OutputText);

            // Capture current daemonset status.

            response = NeonHelper.ExecuteCapture(KubeHelper.NeonCliPath, new object[] { "get", "daemonsets", "-A" });

            File.WriteAllText(Path.Combine(detailsFolder, "daemonsets.txt"), response.OutputText);

            response = NeonHelper.ExecuteCapture(KubeHelper.NeonCliPath, new object[] { "get", "daemonsets", "-A", "-o=yaml" });

            File.WriteAllText(Path.Combine(detailsFolder, "daemonsets.yaml"), response.OutputText);

            // Capture logs from all pods, adding "(not-ready)" to the log file name for
            // pods with containers that aren't ready yet.

            using (var k8s = KubeHelper.CreateKubernetesClient())
            {
                foreach (var pod in k8s.CoreV1.ListAllPodsAsync().Result.Items)
                {
                    var notReady = string.Empty;

                    if (!pod.Status.ContainerStatuses.Any(status => status.Ready))
                    {
                        notReady = " (not-ready)";
                    }

                    response = NeonHelper.ExecuteCapture(KubeHelper.NeonCliPath, new object[] { "logs", pod.Name(), $"--namespace={pod.Namespace()}" })
                        .EnsureSuccess();

                    File.WriteAllText(Path.Combine(detailsFolder, $"{pod.Name()}@{pod.Namespace()}{notReady}.log"), response.OutputText);
                }
            }

            throw new NeonKubeException($"Cluster deployment failed.  Deployment logs archived here:\r\n\r\n{archiveFolder}", e);
        }
    }
}
