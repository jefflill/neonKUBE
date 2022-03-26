﻿//-----------------------------------------------------------------------------
// FILE:	    ClusterFixture.cs
// CONTRIBUTOR: Jeff Lill
// COPYRIGHT:	Copyright (c) 2005-2022 by neonFORGE LLC.  All rights reserved.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.Contracts;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using k8s;
using k8s.KubeConfigModels;

using Newtonsoft.Json.Linq;
using Xunit;

using Neon.Common;
using Neon.Data;
using Neon.Deployment;
using Neon.IO;
using Neon.Retry;
using Neon.Net;
using Neon.SSH;
using Neon.Xunit;
using Neon.Tasks;
using Xunit.Abstractions;

// $hack(jeff):
//
// We're using [Task.WaitWithoutAggregate()] and [Task.ResultWithoutAggregate] which
// call [Task.Wait()] and [Task.Result] respectively.  This isn't ideal.  This will be
// probably be OK since this will never be called by UX code and isn't really going
// to consume a bunch of threads.
//
// I'm not sure what else we can do because we need to await operations in the class
// constructor and destructors and I don't want to change the method signture for
// things like [Reset()] to make them async.

namespace Neon.Kube.Xunit
{
    /// <summary>
    /// <para>
    /// Fixture for testing against neonKUBE clusters.  This can execute against an existing
    /// cluster or it can manage the lifecycle of a new cluster during test runs.
    /// </para>
    /// <note>
    /// The <c>NEON_CLUSTER_TESTING</c> environment variable must be defined on the current
    /// machine to enable this feature.
    /// </note>
    /// </summary>
    /// <remarks>
    /// <note>
    /// <para>
    /// <b>IMPORTANT:</b> The base Neon <see cref="TestFixture"/> implementation <b>DOES NOT</b>
    /// support parallel test execution.  You need to explicitly disable parallel execution in 
    /// all test assemblies that rely on these test fixtures by adding a C# file named 
    /// <c>AssemblyInfo.cs</c> with:
    /// </para>
    /// <code language="csharp">
    /// [assembly: CollectionBehavior(DisableTestParallelization = true, MaxParallelThreads = 1)]
    /// </code>
    /// <para>
    /// and then define your test classes like:
    /// </para>
    /// <code language="csharp">
    /// public class MyTests : IClassFixture&lt;ClusterFixture&gt;
    /// {
    ///     private const string clusterDefinitionYaml =
    /// @"name: test
    /// datacenter: test
    /// environment: test
    /// isLocked: false
    /// timeSources:
    /// - pool.ntp.org
    /// kubernetes:
    ///   allowPodsOnMasters: true
    /// hosting:
    ///   environment: hyperv
    ///   hyperv:
    ///     useInternalSwitch: true
    ///   vm:
    ///     namePrefix: "test"
    ///     cores: 4
    ///     memory: 12 GiB
    ///     osDisk: 40 GiB
    /// network:
    ///   premiseSubnet: 100.64.0.0/24
    ///   gateway: 100.64.0.1
    /// nodes:
    ///   master:
    ///     role: master
    ///     address: 100.64.0.2
    /// ";
    ///     
    ///     private ClusterFixture foxture;
    /// 
    ///     public MyTests(ClusterFixture fixture)
    ///     {
    ///         this.fixture = foxture;    
    /// 
    ///         var status = fixture.StartAsync(clusterDefinitionYaml);
    ///         
    ///         switch (status)
    ///         {
    ///             case TestFixtureStatus.Disabled:
    ///             
    ///                 return;
    ///                 
    ///             case TestFixtureStatus.Started:
    ///             
    ///                 // The fixture ensures that the cluster is reset when
    ///                 // [Start()] is called the first time for a 
    ///                 // fixture instance.
    ///                 
    ///                 break;
    ///                 
    ///             case TestFixtureStatus.AlreadyRunning:
    ///             
    ///                 // Reset the cluster between test method calls.
    /// 
    ///                 fixture.Reset();
    ///                 break;
    ///         }
    ///     }
    ///     
    ///     [Collection(TestCollection.NonParallel)]
    ///     [CollectionDefinition(TestCollection.NonParallel, DisableParallelization = true)]
    ///     [ClusterFact]
    ///     public void Test()
    ///     {
    ///         // Implement your test here.  Note that [fixture.Cluster] returns a [clusterProxy]
    ///         // that can be used to manage the cluster and [fixture.K8s] returns an
    ///         // [IKubernetes] client connected to the cluster with root privileges.
    ///     }
    /// }
    /// </code>
    /// </note>
    /// <para>
    /// This fixture can be used to run tests against an existing neonKUBE cluster as well
    /// as a new clusters deployed by the fixture.  The idea here is that you'll have
    /// your unit test class inherit from <see cref="IClassFixture{TFixture}"/>, passing
    /// <see cref="ClusterFixture"/> as the type parameter and then implementing a test class
    /// constructor that has a <see cref="ClusterFixture"/> parameter that will receive an 
    /// instance of the fixture and use that to initialize the test cluster using 
    /// <see cref="Start(ClusterDefinition, ClusterFixtureOptions)"/> or one it its overrides.
    /// </para>
    /// <para>
    /// <see cref="Start(ClusterDefinition, ClusterFixtureOptions)"/> handles the deployment of 
    /// the test cluster when it doesn't already exist as well as the  removal of any previous 
    /// cluster, depending on the parameters passed.  You'll be calling this in your test class
    /// constructor.
    /// </para>
    /// <para>
    /// The <b>Start()</b> methods accept a cluster definition in various forms and returns
    /// <see cref="TestFixtureStatus.Disabled"/> when cluster unit testing is disabled on the 
    /// current machine, <see cref="TestFixtureStatus.Started"/> the first time one of these methods 
    /// have been called on the fixture instance or <see cref="TestFixtureStatus.AlreadyRunning"/>
    /// when <b>StartedAsync()</b> has already been called on the fixture.  Your test class typically
    /// use this value to decide whether to reset the cluster and or whether additional cluster 
    /// configuration is required (e.g. deploying test applications).
    /// </para>
    /// <para>
    /// It's up to you to call <see cref="ClusterFixture.Reset()"/> within your test class constructor
    /// when you wish to reset the cluster state between test method executions.  Alternatively, you 
    /// could design your tests such that each method runs in its own namespace to improve test performance
    /// while still providing some isolation across test cases.
    /// </para>
    /// <para><b>MANAGING YOUR TEST CLUSTER</b></para>
    /// <para>
    /// You're tests will need to be able to deploy applications and otherwise to the test cluster and
    /// otherwise manage your test cluster.  The <see cref="K8s"/> property returns a <see cref="IKubernetes"/>
    /// client for the cluster and the <see cref="Cluster"/> property returns a <see cref="ClusterProxy"/>
    /// that provides some higher level functionality.  Most developers should probably stick with using
    /// <see cref="K8s"/>.
    /// </para>
    /// <para>
    /// The fixture also provides the <see cref="NeonExecute(string[])"/> method which can be used for 
    /// executing <b>kubectl</b>, <b>helm</b>, and other commands using the <b>neon-cli</b>.  Commands
    /// will be executed against the test cluster (as the current config) and a <see cref="ExecuteResponse"/>
    /// will be returned holding the command exit code as well as the output text.
    /// </para>
    /// <para><b>CLUSTER TEST METHOD ATTRIBUTES</b></para>
    /// <para>
    /// Tests that require a neonKUBE cluster will generally be quite slow and will require additional
    /// resources on the machine where the test is executing and potentially external resources including
    /// XenServer hosts, cloud accounts, specific network configuration, etc.  This means that cluster
    /// based unit tests can generally run only on specifically configured enviroments.
    /// </para>
    /// <para>
    /// We provide the <see cref="ClusterFactAttribute"/> and <see cref="ClusterTheoryAttribute"/> attributes
    /// to manage this.  These derive from <see cref="FactAttribute"/> and <see cref="TheoryAttribute"/>
    /// respectively and set the base class <c>Skip</c> property when the <c>NEON_CLUSTER_TESTING</c> environment
    /// variable <b>does not exist</b>.
    /// </para>
    /// <para>
    /// Test methods that require neonKUBE clusters should be tagged with <see cref="ClusterFactAttribute"/> or
    /// <see cref="ClusterTheoryAttribute"/> instead of <see cref="FactAttribute"/> or <see cref="TheoryAttribute"/>.
    /// Then by default, these methods won't be executed unless the user has explicitly enabled this on the test
    /// machine by defining the <c>NEON_CLUSTER_TESTING</c> environment variable.
    /// </para>
    /// <para>
    /// In addition to tagging test methods like this, you'll need to modify your test class constructors to do
    /// nothing when the fixture's <c>Start()</c> methods return <see cref="TestFixtureStatus.Disabled"/>.
    /// You can also use <see cref="TestHelper.IsClusterTestingEnabled"/> determine when cluster testing is
    /// disabled.
    /// </para>
    /// <para><b>TESTING SCENARIOS</b></para>
    /// <para>
    /// <see cref="ClusterFixture"/> is designed to support some common testing scenarios, controlled by
    /// <see cref="ClusterFixtureOptions"/>.
    /// </para>
    /// <list type="table">
    /// <item>
    ///     <term><b>Fresh cluster</b></term>
    ///     <description>
    ///     The fixture will remove any existing cluster and deploy a fresh cluster for the tests.  Configure
    ///     this by setting <see cref="ClusterFixtureOptions.RemoveClusterOnStart"/> to <c>true</c>.  This is
    ///     the slowest option because deploying clusters can take 10-20 minutes.
    ///     </description>
    /// </item>
    /// <item>
    ///     <term><b>Reuse cluster</b></term>
    ///     <description>
    ///     The fixture will reuse an existing cluster if its reachable, healthy, and the the existing
    ///     cluster definition matches the test cluster definition.   Configure this by setting 
    ///     <see cref="ClusterFixtureOptions.RemoveClusterOnStart"/> to <c>false</c>.  This is the default and 
    ///     fastest option when the the required conditions are met.  Otherwise, the existing cluster will
    ///     be removed and a new cluster will be deployed.
    ///     </description>
    /// </item>
    /// <item>
    ///     <term>Remove cluster</term>
    ///     <description>
    ///     <para>
    ///     Your test class can indicate that the test cluster will be removed after your test class finishes
    ///     running test methods.  Configure this by setting <see cref="ClusterFixtureOptions.RemoveClusterOnDispose"/>
    ///     to <c>true</c>.  This defaults to <c>false</c> because reusing a running cluster is the fastest way
    ///     to run cluster based tests.
    ///     </para>
    ///     <note>
    ///     Clusters will continue running when the <see cref="ClusterFixture"/> is never disposed.  This happens
    ///     when the test runner fails or is stopped while debugging etc.
    ///     </note>
    ///     </description>
    /// </item>
    /// </list>
    /// <para>
    /// The default <see cref="ClusterFixtureOptions"/> settings are configured to <b>reuse clusters</b> for
    /// better performance, leaving clusters running after running test cases.  This is recommended for most
    /// user scenarios when you have enough resources to keep a test cluster running.
    /// </para>
    /// <para><b>CLUSTER CONFLICTS</b></para>
    /// <para>
    /// One thing you'll need to worry about is the possibility that a cluster created by one of the <b>Start()</b> 
    /// methods may conflict with an existing production or neonDESKTOP built-in cluster.  This fixture helps
    /// somewhat by persisting cluster state such as kubconfigs, logins, logs, etc. for each deployed cluster
    /// within separate directories named like <b>$(USERPROFILE)\.neonkube\automation\()fixture</b>.
    /// This effectively isolates clusters deployed by the fixture from the user clusters.
    /// </para>
    /// <para>
    /// <b>IMPORTANT:</b> You'll need to ensure that your cluster name does not conflict with any existing
    /// clusters deployed to the same environment and also that the node IP addresses don't conflict with
    /// existing clusters deployed on shared infrastructure such as local machines, Hyper-V or XenServer
    /// instances.  You don't need to worry about IP address conflicts for cloud environments because nodes
    /// run on private networks there.
    /// </para>
    /// <para>
    /// We recommend that you prefix your cluster name with something identifying the machine deploying
    /// the cluster.  This could be the machine name, user or a combination of the machine and the current
    /// username, like <b>runner0-</b> or <b>jeff-</b>, or <b>runner0-jeff-</b>...
    /// </para>
    /// <note>
    /// neonKUBE maintainers can also use <see cref="IProfileClient"/> combined with the <b>neon-assistant</b>
    /// tool to reference per-user and/or per-machine profile settings including things like cluster name prefixes, 
    /// reserved node IP addresses, etc.  These can be referenced by cluster definitions using special macros like
    /// <c>$&lt;$&lt;$&lt;NAME&gt;&gt;&gt;</c> as described here: <see cref="PreprocessReader"/>.
    /// </note>
    /// <para>
    /// The goal here is prevent cluster and/or VM naming conflicts for test clusters deployed in parallel
    /// by different runners or developers on their own workstations as well as specifying environment specific
    /// settings such as host hypervisors, LAN configuration, and node IP addresses.
    /// </para>
    /// </remarks>
    public class ClusterFixture : TestFixture
    {
        private ClusterFixtureOptions   options;
        private bool                    started = false;
        private string                  automationFolder;

        /// <summary>
        /// Constructor.
        /// </summary>
        public ClusterFixture()
        {
        }

        /// <inheritdoc/>
        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                if (!base.IsDisposed)
                {
                    Reset();
                }

                GC.SuppressFinalize(this);
            }
        }

        /// <summary>
        /// Returns a <see cref="ClusterProxy"/> instance that can be used to manage the attached
        /// cluster after it has been started.
        /// </summary>
        public ClusterProxy Cluster { get; private set; }

        /// <summary>
        /// Returns a <see cref="IKubernetes"/> client instance with root privileges that can be 
        /// used to manage the test cluster after it has been started.
        /// </summary>
        public IKubernetes K8s => Cluster?.K8s;

        /// <summary>
        /// Returns the cluster definition for cluster deployed by this fixture via one of the
        /// <b>Start()</b> methods or <c>null</c> when the fixture was connected to the cluster
        /// via one of the <b>ConnectAsync()</b> methods.
        /// </summary>
        public ClusterDefinition ClusterDefinition { get; private set; }

        /// <summary>
        /// Writes a line of text to the test output (when an <see cref="ITestOutputHelper"/>
        /// was passed top one of the <b>Start()</b> methods.
        /// </summary>
        /// <param name="line">The line to be written or <c>null</c> for a blank line.</param>
        private void WriteTestOutputLine(string line = null)
        {
            options?.TestOutputHelper?.WriteLine(line ?? string.Empty);
        }

        /// <summary>
        /// <para>
        /// Deploys a new cluster as specified by the cluster definition model passed.
        /// </para>
        /// <note>
        /// This method removes any existing neonKUBE cluster before deploying a fresh one.
        /// </note>
        /// </summary>
        /// <param name="clusterDefinition">The cluster definition model.</param>
        /// <param name="options">
        /// Optionally specifies the options that <see cref="ClusterFixture"/> will use to
        /// manage the test cluster.
        /// </param>
        /// <returns>
        /// <para>
        /// The <see cref="TestFixtureStatus"/>:
        /// </para>
        /// <list type="table">
        /// <item>
        ///     <term><see cref="TestFixtureStatus.Disabled"/></term>
        ///     <description>
        ///     Returned when cluster unit testing is disabled due to the <c>NEON_CLUSTER_TESTING</c> environment
        ///     variable not being present on the current machine which means that <see cref="TestHelper.IsClusterTestingEnabled"/>
        ///     returns <c>false</c>.
        ///     </description>
        /// </item>
        /// <item>
        ///     <term><see cref="TestFixtureStatus.Started"/></term>
        ///     <description>
        ///     Returned when one of the <c>Start()</c> methods is called for the first time for the fixture
        ///     instance, indicating that an existing cluster has been connected or a new cluster has been deployed.
        ///     </description>
        /// </item>
        /// <item>
        ///     <term><see cref="TestFixtureStatus.AlreadyRunning"/></term>
        ///     <description>
        ///     Returned when one of the <c>Start()</c> methods has already been called by your test
        ///     class instance.
        ///     </description>
        /// </item>
        /// </list>
        /// </returns>
        /// <remarks>
        /// <para>
        /// <b>IMPORTANT:</b> Only one <see cref="ClusterFixture"/> can be run at a time on
        /// any one computer.  This is due to the fact that cluster state like the kubeconfig,
        /// neonKUBE logins, logs and other files will be written to <b>$(USERPROFILE)/.neonkube/automation/(fixture)/*.*</b>
        /// so multiple fixture instances will be confused when trying to manage these same files.
        /// </para>
        /// <para>
        /// This means that not only will running <see cref="ClusterFixture"/> based tests in parallel
        /// within the same instance of Visual Studio fail, but but running these tests in different
        /// Visual Studio instances will also fail.
        /// </para>
        /// </remarks>
        public TestFixtureStatus Start(ClusterDefinition clusterDefinition, ClusterFixtureOptions options = null)
        {
            Covenant.Requires<ArgumentNullException>(clusterDefinition != null, nameof(clusterDefinition));

            if (!TestHelper.IsClusterTestingEnabled)
            {
                return TestFixtureStatus.Disabled;
            }

            if (started)
            {
                return TestFixtureStatus.AlreadyRunning;
            }
            
            options    ??= new ClusterFixtureOptions();
            this.options = options;

            try
            {
                if (this.Cluster != null)
                {
                    return TestFixtureStatus.AlreadyRunning;
                }

                // Set the automation mode, using any previously downloaded node image unless
                // the user specifies a custom image.  We're going to host the fixture state
                // files in this fixed folder:
                //
                //      $(USERPROFILE)\.neonkube\automation\(fixture)\*.*

                automationFolder = KubeHelper.SetAutomationMode(string.IsNullOrEmpty(options.ImageUriOrPath) ? KubeAutomationMode.EnabledWithSharedCache : KubeAutomationMode.Enabled, KubeHelper.AutomationPrefix("fixture"));

                // Figure out whether the user passed an image URI or file path to override
                // the default node image.

                var imageUriOrPath = options.ImageUriOrPath;
                var imageUri       = (string)null;
                var imagePath      = (string)null;

                if (string.IsNullOrEmpty(imageUriOrPath))
                {
                    imageUriOrPath = KubeDownloads.GetDefaultNodeImageUri(clusterDefinition.Hosting.Environment);
                }

                if (imageUriOrPath.StartsWith("http://", StringComparison.InvariantCultureIgnoreCase) || imageUriOrPath.StartsWith("https://", StringComparison.InvariantCultureIgnoreCase))
                {
                    imageUri = imageUriOrPath;
                }
                else
                {
                    imagePath = imageUriOrPath;
                }

                // Determine whether a test cluster with the same name exists and if
                // its cluster definition matches the test cluster's definition.

                var clusterExists      = false;
                var clusterContextName = KubeContextName.Parse($"root@{clusterDefinition.Name}");
                var clusterContext     = KubeHelper.Config.GetContext(clusterContextName);
                var clusterLogin       = KubeHelper.GetClusterLogin(clusterContextName);

                if (clusterContext != null && clusterContext != null)
                {
                    clusterExists = NeonHelper.JsonEquals(clusterDefinition, clusterLogin.ClusterDefinition);
                }

                if (clusterExists && !options.RemoveClusterOnStart)
                {
                    // It looks like the test cluster may already exist.  We'll make it
                    // the current cluster and then verify that it's running and healthy
                    // and use it when it looks good.  Otherwise we'll remove the context
                    // and login and then deploy a new cluster below.

                    try
                    {
                        KubeHelper.SetCurrentContext(clusterContextName);

                        Cluster = new ClusterProxy(clusterLogin.ClusterDefinition, new HostingManagerFactory());

                        if (Cluster.GetClusterStatusAsync().ResultWithoutAggregate().State == ClusterState.Healthy)
                        {
                            return TestFixtureStatus.Started;
                        }

                        Cluster.RemoveAsync(removeExisting: true).WaitWithoutAggregate();

                        Cluster?.Dispose();
                        Cluster = null;
                    }
                    catch
                    {
                        Cluster?.Dispose();
                        Cluster = null;

                        KubeHelper.SetCurrentContext((string)null);
                        throw;
                    }

                    return TestFixtureStatus.Started;
                }

                // Provision the cluster.

                try
                {
                    var controller = KubeSetup.CreateClusterPrepareController(
                        clusterDefinition:  clusterDefinition,
                        nodeImageUri:       imageUri,
                        nodeImagePath:      imagePath,
                        maxParallel:        options.MaxParallel,
                        unredacted:         options.Unredacted,
                        headendUri:         options.HeadendUri);

                    switch (controller.RunAsync().ResultWithoutAggregate())
                    {
                        case SetupDisposition.Succeeded:

                            break;

                        case SetupDisposition.Failed:

                            throw new NeonKubeException("Cluster prepare failed.");

                        case SetupDisposition.Cancelled:
                        default:

                            throw new NotImplementedException();
                    }
                }
                finally
                {
                    if (options.CaptureDeploymentLogs)
                    {
                        CaptureDeploymentLogs();
                    }
                }

                // Setup the cluster.

                try
                {
                    var controller = KubeSetup.CreateClusterSetupController(
                        clusterDefinition,
                        maxParallel:    options.MaxParallel,
                        unredacted:     options.Unredacted);

                    switch (controller.RunAsync().ResultWithoutAggregate())
                    {
                        case SetupDisposition.Succeeded:

                            break;

                        case SetupDisposition.Failed:

                            throw new NeonKubeException("Cluster setup failed.");

                        case SetupDisposition.Cancelled:
                        default:

                            throw new NotImplementedException();
                    }
                }
                finally
                {
                    if (options.CaptureDeploymentLogs)
                    {
                        CaptureDeploymentLogs();
                    }
                }
            }
            finally
            {
            }

            started = true;

            return TestFixtureStatus.Started;
        }

        /// <summary>
        /// <para>
        /// Deploys a new cluster as specified by the cluster definition YAML definition.
        /// </para>
        /// <note>
        /// This method removes any existing neonKUBE cluster before deploying a fresh one.
        /// </note>
        /// </summary>
        /// <param name="clusterDefinitionYaml">The cluster definition YAML.</param>
        /// <param name="options">
        /// Optionally specifies the options that <see cref="ClusterFixture"/> will use to
        /// manage the test cluster.
        /// </param>
        /// <returns>
        /// <para>
        /// The <see cref="TestFixtureStatus"/>:
        /// </para>
        /// <list type="table">
        /// <item>
        ///     <term><see cref="TestFixtureStatus.Disabled"/></term>
        ///     <description>
        ///     Returned when cluster unit testing is disabled due to the <c>NEON_CLUSTER_TESTING</c> environment
        ///     variable not being present on the current machine which means that <see cref="TestHelper.IsClusterTestingEnabled"/>
        ///     returns <c>false</c>.
        ///     </description>
        /// </item>
        /// <item>
        ///     <term><see cref="TestFixtureStatus.Started"/></term>
        ///     <description>
        ///     Returned when one of the <c>Start()</c> methods is called for the first time for the fixture
        ///     instance, indicating that an existing cluster has been connected or a new cluster has been deployed.
        ///     </description>
        /// </item>
        /// <item>
        ///     <term><see cref="TestFixtureStatus.AlreadyRunning"/></term>
        ///     <description>
        ///     Returned when one of the <c>Start()</c> methods has already been called by your test
        ///     class instance.
        ///     </description>
        /// </item>
        /// </list>
        /// </returns>
        /// <remarks>
        /// <para>
        /// <b>IMPORTANT:</b> Only one <see cref="ClusterFixture"/> can be run at a time on
        /// any one computer.  This is due to the fact that cluster state like the kubeconfig,
        /// neonKUBE logins, logs and other files will be written to <b>$(USERPROFILE)/.neonkube/automation/(fixture)/*.*</b>
        /// so multiple fixture instances will be confused when trying to manage these same files.
        /// </para>
        /// <para>
        /// This means that not only will running <see cref="ClusterFixture"/> based tests in parallel
        /// within the same instance of Visual Studio fail, but but running these tests in different
        /// Visual Studio instances will also fail.
        /// </para>
        /// </remarks>
        public TestFixtureStatus Start(string clusterDefinitionYaml, ClusterFixtureOptions options = null)
        {
            Covenant.Requires<ArgumentNullException>(clusterDefinitionYaml != null, nameof(clusterDefinitionYaml));

            return Start(ClusterDefinition.FromYaml(clusterDefinitionYaml), options);
        }

        /// <summary>
        /// <para>
        /// Deploys a new cluster as specified by a cluster definition YAML file.
        /// </para>
        /// <note>
        /// This method removes any existing neonKUBE cluster before deploying a fresh one.
        /// </note>
        /// </summary>
        /// <param name="clusterDefinitionFile"><see cref="FileInfo"/> for the cluster definition YAML file.</param>
        /// <param name="options">
        /// Optionally specifies the options that <see cref="ClusterFixture"/> will use to
        /// manage the test cluster.
        /// </param>
        /// <returns>
        /// <para>
        /// The <see cref="TestFixtureStatus"/>:
        /// </para>
        /// <list type="table">
        /// <item>
        ///     <term><see cref="TestFixtureStatus.Disabled"/></term>
        ///     <description>
        ///     Returned when cluster unit testing is disabled due to the <c>NEON_CLUSTER_TESTING</c> environment
        ///     variable not being present on the current machine which means that <see cref="TestHelper.IsClusterTestingEnabled"/>
        ///     returns <c>false</c>.
        ///     </description>
        /// </item>
        /// <item>
        ///     <term><see cref="TestFixtureStatus.Started"/></term>
        ///     <description>
        ///     Returned when one of the <c>Start()</c> methods is called for the first time for the fixture
        ///     instance, indicating that an existing cluster has been connected or a new cluster has been deployed.
        ///     </description>
        /// </item>
        /// <item>
        ///     <term><see cref="TestFixtureStatus.AlreadyRunning"/></term>
        ///     <description>
        ///     Returned when one of the <c>Start()</c> methods has already been called by your test
        ///     class instance.
        ///     </description>
        /// </item>
        /// </list>
        /// </returns>
        /// <remarks>
        /// <para>
        /// <b>IMPORTANT:</b> Only one <see cref="ClusterFixture"/> can be run at a time on
        /// any one computer.  This is due to the fact that cluster state like the kubeconfig,
        /// neonKUBE logins, logs and other files will be written to <b>$(USERPROFILE)/.neonkube/automation/(fixture)/*.*</b>
        /// so multiple fixture instances will be confused when trying to manage these same files.
        /// </para>
        /// <para>
        /// This means that not only will running <see cref="ClusterFixture"/> based tests in parallel
        /// within the same instance of Visual Studio fail, but but running these tests in different
        /// Visual Studio instances will also fail.
        /// </para>
        /// </remarks>
        public TestFixtureStatus Start(FileInfo clusterDefinitionFile, ClusterFixtureOptions options = null)
        {
            Covenant.Requires<ArgumentNullException>(clusterDefinitionFile != null, nameof(clusterDefinitionFile));

            return Start(ClusterDefinition.FromFile(clusterDefinitionFile.FullName), options);
        }

        /// <summary>
        /// Reads the deployment log files and writes their content to <see cref="ClusterFixtureOptions.TestOutputHelper"/>
        /// when enabled.
        /// </summary>
        private void CaptureDeploymentLogs()
        {
            if (!options.CaptureDeploymentLogs || options.TestOutputHelper == null)
            {
                return;
            }

            var logFolder      = KubeHelper.LogFolder;
            var clusterLogPath = Path.Combine(logFolder, KubeConst.ClusterLogName);

            // Capture [cluster.log] first.

            if (File.Exists(clusterLogPath))
            {
                WriteTestOutputLine($"# FILE: {KubeConst.ClusterLogName}");
                WriteTestOutputLine();

                using (var reader = new StreamReader(clusterLogPath))
                {
                    foreach (var line in reader.Lines())
                    {
                        WriteTestOutputLine(line);
                    }
                }

                WriteTestOutputLine();
            }

            // Capture any other log files.

            foreach (var path in Directory.GetFiles(logFolder, "*.log", SearchOption.TopDirectoryOnly)
                .Where(path => path != clusterLogPath)
                .OrderBy(path => path, StringComparer.InvariantCultureIgnoreCase))
                {
                    WriteTestOutputLine($"# FILE: {Path.GetFileName(path)}");
                    WriteTestOutputLine();

                    using (var reader = new StreamReader(path))
                    {
                        foreach (var line in reader.Lines())
                        {
                            WriteTestOutputLine(line);
                        }
                    }

                    WriteTestOutputLine();
                }
        }

        /// <summary>
        /// Executes a <b>neon-cli</b> command against the current test cluster.
        /// </summary>
        /// <param name="args">The command arguments.</param>
        /// <returns>An <see cref="ExecuteResponse"/> with the exit code and output text.</returns>
        /// <remarks>
        /// <para>
        /// <b>neon-cli</b> is a wrapper around the <b>kubectl</b> and <b>helm</b> tools.
        /// </para>
        /// <para><b>KUBECTL COMMANDS:</b></para>
        /// <para>
        /// <b>neon-cli</b> implements <b>kubectl</b> commands directly like:
        /// </para>
        /// <code>
        /// neon get pods
        /// neon apply -f myapp.yaml
        /// </code>
        /// <para><b>HELM COMMANDS:</b></para>
        /// <para>
        /// <b>neon-cli</b> implements <b>helm</b> commands like <b>neon helm...</b>:
        /// </para>
        /// <code>
        /// neon helm install -f values.yaml myapp .
        /// neon helm uninstall myapp
        /// </code>
        /// <para><b>THROW EXCEPTION ON ERRORS</b></para>
        /// <para>
        /// Rather than explicitly checking the <see cref="ExecuteResponse.ExitCode"/> and throwing
        /// exceptions yourself, you can call <see cref="ExecuteResponse.EnsureSuccess()"/> which
        /// throws an <see cref="ExecuteException"/> for non-zero exit codes or you can use
        /// <see cref="NeonExecuteWithCheck(string[])"/> which does this for you.
        /// </para>
        /// </remarks>
        public ExecuteResponse NeonExecute(params string[] args)
        {
            return NeonHelper.ExecuteCapture("neon", args);
        }

        /// <summary>
        /// Executes a <b>neon-cli</b> command against the current test cluster, throwing an
        /// <see cref="ExecuteException"/> for non-zero exit codes.
        /// </summary>
        /// <param name="args">The command arguments.</param>
        /// <returns>An <see cref="ExecuteResponse"/> with the exit code and output text.</returns>
        /// <remarks>
        /// <para>
        /// <b>neon-cli</b> is a wrapper around the <b>kubectl</b> and <b>helm</b> tools.
        /// </para>
        /// <para><b>KUBECTL COMMANDS:</b></para>
        /// <para>
        /// <b>neon-cli</b> implements <b>kubectl</b> commands directly like:
        /// </para>
        /// <code>
        /// neon get pods
        /// neon apply -f myapp.yaml
        /// </code>
        /// <para><b>HELM COMMANDS:</b></para>
        /// <para>
        /// <b>neon-cli</b> implements <b>helm</b> commands like <b>neon helm...</b>:
        /// </para>
        /// <code>
        /// neon helm install -f values.yaml myapp .
        /// neon helm uninstall myapp
        /// </code>
        /// </remarks>
        public ExecuteResponse NeonExecuteWithCheck(params string[] args)
        {
            return NeonExecute(args).EnsureSuccess();
        }

        /// <inheritdoc/>
        public override void Reset()
        {
            if (TestHelper.IsClusterTestingEnabled)
            {
                Cluster.ResetAsync(options.ResetOptions, message => WriteTestOutputLine(message)).WaitWithoutAggregate();
            }

            base.Reset();
        }
    }
}
