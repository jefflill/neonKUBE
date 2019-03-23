﻿//-----------------------------------------------------------------------------
// FILE:	    MainForm.cs
// CONTRIBUTOR: Jeff Lill
// COPYRIGHT:	Copyright (c) 2016-2019 by neonFORGE, LLC.  All rights reserved.
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
using System.Data;
using System.Diagnostics;
using System.Diagnostics.Contracts;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;

using Microsoft.Net.Http.Server;

using Neon;
using Neon.Common;
using Neon.Cryptography;
using Neon.IO;
using Neon.Kube;
using Neon.Net;

namespace WinDesktop
{
    /// <summary>
    /// The main application form.  Note that this form will always be hidden.
    /// </summary>
    public partial class MainForm : Form
    {
        //---------------------------------------------------------------------
        // Static members

        /// <summary>
        /// Returns the current (and only) main form instance so that other
        /// parts of the app can manipulate the UI.
        /// </summary>
        public static MainForm Current { get; private set; }

        //---------------------------------------------------------------------
        // Instance members

        private const double animationFrameRate = 2;
        private const string headendError       = "Unable to contact the neonKUBE headend service.";

        private object              syncLock = new object();
        private Icon                appIcon;
        private Icon                disconnectedIcon;
        private Icon                connectedIcon;
        private Icon                errorIcon;
        private AnimatedIcon        connectingAnimation;
        private AnimatedIcon        workingAnimation;
        private AnimatedIcon        errorAnimation;
        private ContextMenu         contextMenu;
        private bool                operationInProgress;
        private RemoteOperation     remoteOperation;
        private Stack<NotifyState>  notifyStack;
        private List<ReverseProxy>  proxies;
        private KubeConfigContext   proxiedContext;

        /// <summary>
        /// Constructor.
        /// </summary>
        public MainForm()
        {
            MainForm.Current = this;

            InitializeComponent();

            Load  += MainForm_Load;
            Shown += (s, a) => Visible = false; // The main form should always be hidden

            // Ensure that temporary files are written to the users temporary folder because
            // there's a decent chance that this folder will be encrypted at rest.

            TempFile.Root   = KubeHelper.TempFolder;
            TempFolder.Root = KubeHelper.TempFolder;

            // Preload the notification icons and animations for better performance.

            appIcon             = new Icon(@"Images\app.ico");
            connectedIcon       = new Icon(@"Images\connected.ico");
            disconnectedIcon    = new Icon(@"Images\disconnected.ico");
            errorIcon           = new Icon(@"Images\error.ico");
            connectingAnimation = AnimatedIcon.Load("Images", "connecting", animationFrameRate);
            workingAnimation    = AnimatedIcon.Load("Images", "working", animationFrameRate);
            errorAnimation      = AnimatedIcon.Load("Images", "error", animationFrameRate);
            notifyStack         = new Stack<NotifyState>();

            // Initialize the cluster hosting provider components.

            HostingLoader.Initialize();

            // Initialize the client state.

            proxies = new List<ReverseProxy>();
            Headend = new HeadendClient();
            KubeHelper.LoadClientConfig();
        }

        /// <summary>
        /// Indicates whether the application is connected to a cluster.
        /// </summary>
        public bool IsConnected => KubeHelper.CurrentContext != null;

        /// <summary>
        /// Returns the neonKUBE head client to be used to query the headend services.
        /// </summary>
        public HeadendClient Headend { get; private set; }

        /// <summary>
        /// Handles form initialization.
        /// </summary>
        /// <param name="sender">The sender.</param>
        /// <param name="args">The arguments.</param>
        private void MainForm_Load(object sender, EventArgs args)
        {
            // Start the desktop API service that [neon-cli] will use
            // to communicate with the desktop application.  Note that
            // this will fail if another instance of the desktop is 
            // running.

            try
            {
                DesktopService.Start();
            }
            catch
            {
                MessageBox.Show($"Another neonKUBE Desktop instance is already running or another application is already listening on [127.0.0.1:{KubeHelper.ClientConfig.DesktopServicePort}].",
                    "neonKUBE Desktop", MessageBoxButtons.OK, MessageBoxIcon.Error);

                Environment.Exit(1);
            }

            // Set the text labels on the main form.  Nobody should ever see
            // this because the form should remain hidden but we'll put something
            // here just in case.

            productNameLabel.Text  = $"{Build.ProductName}  v{Build.ProductVersion}";
            copyrightLabel.Text    = Build.Copyright;
            licenseLinkLabel.Text  = Build.ProductLicense;

            // Initialize the notify icon and its context memu.

            SetBalloonText(Build.ProductName);

            notifyIcon.Icon        = disconnectedIcon;
            notifyIcon.ContextMenu = contextMenu = new ContextMenu();
            notifyIcon.Visible     = true;
            contextMenu.Popup     += Menu_Popup;

            // Set the initial notify icon state and setup a timer
            // to periodically keep the UI in sync with any changes.

            UpdateUIState();

            statusTimer.Interval = (int)TimeSpan.FromSeconds(KubeHelper.ClientConfig.StatusPollSeconds).TotalMilliseconds;
            statusTimer.Tick    += (s, a) => UpdateUIState();
            statusTimer.Start();
        }

        /// <summary>
        /// Handles license link clicks.
        /// </summary>
        /// <param name="sender">The sender.</param>
        /// <param name="args">The arguments.</param>
        private void licenseLinkLabel_LinkClicked(object sender, LinkLabelLinkClickedEventArgs args)
        {
            NeonHelper.OpenBrowser(Build.ProductLicenseUrl);
        }

        /// <summary>
        /// Ensures that an action is performed on the UI thread.
        /// </summary>
        /// <param name="action">The action.</param>
        private void InvokeOnUIThread(Action action)
        {
            if (action == null)
            {
                return;
            }

            if (InvokeRequired)
            {
                Invoke(action);
            }
            else
            {
                action();
            }
        }

        /// <summary>
        /// <para>
        /// Sets the notify icon balloon text.
        /// </para>
        /// <note>
        /// Windows limits the balloon text to 64 characters.  This method will trim
        /// the text to fit if necessary.
        /// </note>
        /// </summary>
        /// <param name="balloonText">The balloon text.</param>
        private void SetBalloonText(string balloonText)
        {
            balloonText = balloonText ?? string.Empty;

            if (balloonText.Length > 64)
            {
                balloonText = balloonText.Substring(0, 64 - 3) + "...";
            }

            notifyIcon.Text = balloonText;
        }

        /// <summary>
        /// Starts a notify icon animation.
        /// </summary>
        /// <param name="animatedIcon">The icon animation.</param>
        /// <param name="balloonText">Optional text to be displayed in the balloon during the animation.</param>
        /// <param name="isError">Optionally indicates that we're in an error state.</param>
        /// <remarks>
        /// Calls to this method may be recursed and should be matched 
        /// with a call to <see cref="StopNotifyAnimation"/>.  The
        /// amimation will actually stop when the last matching
        /// <see cref="StartNotifyAnimation"/> call was matched with
        /// the last <see cref="StopNotifyAnimation"/>.
        /// </remarks>
        private void StartNotifyAnimation(AnimatedIcon animatedIcon, string balloonText = null, bool isError = false)
        {
            Covenant.Requires<ArgumentNullException>(animatedIcon != null);

            notifyStack.Push(new NotifyState(animatedIcon, balloonText, isError));

            if (!string.IsNullOrEmpty(balloonText))
            {
                SetBalloonText(balloonText);
            }

            if (animatedIcon != null)
            {
                animationTimer.Stop();
                animationTimer.Interval = (int)TimeSpan.FromSeconds(1 / animatedIcon.FrameRate).TotalMilliseconds;
                animationTimer.Tick    +=
                    (s, a) =>
                    {
                        notifyIcon.Icon = animatedIcon.GetNextFrame();
                    };

                animationTimer.Start();
            }
        }

        /// <summary>
        /// Stops the notify icon animation.
        /// </summary>
        /// <param name="force">Optionally force the animation to stop regardless of the nesting level.</param>
        private void StopNotifyAnimation(bool force = false)
        {
            if (force)
            {
                if (notifyStack.Count > 0)
                {
                    animationTimer.Stop();
                    UpdateUIState();
                    notifyStack.Clear();
                }

                return;
            }

            if (notifyStack.Count == 0)
            {
                throw new InvalidOperationException("StopNotifyAnimation: Stack underflow.");
            }

            notifyStack.Pop();
            animationTimer.Stop();

            if (notifyStack.Count == 0)
            {
                UpdateUIState();
            }
            else
            {
                // We need to restart the previous icon animation in the
                // stack (if there is one).

                var animatedIcon = (AnimatedIcon)null;

                for (int i = notifyStack.Count - 1; i >= 0; i--)
                {
                    var notifyState = notifyStack.ElementAt(i);

                    if (notifyState.AnimatedIcon != null)
                    {
                        animatedIcon = notifyState.AnimatedIcon;
                        break;
                    }
                }

                if (animatedIcon != null)
                {
                    animationTimer.Interval = (int)TimeSpan.FromSeconds(1 / animatedIcon.FrameRate).TotalMilliseconds;
                    animationTimer.Tick    +=
                        (s, a) =>
                        {
                            notifyIcon.Icon = animatedIcon.GetNextFrame();
                        };

                    animationTimer.Start();
                }
            }
        }

        /// <summary>
        /// Displays the notify icon's balloon (AKA toast).
        /// </summary>
        /// <param name="text">The message text.</param>
        /// <param name="title">The ballon title text (defaults to the application name).</param>
        /// <param name="icon">The optional tool tip icon (defaults to <see cref="ToolTipIcon.Info"/>).</param>
        private void ShowToast(string text, string title = null, ToolTipIcon icon = ToolTipIcon.Info)
        {
            notifyIcon.ShowBalloonTip(0, title ?? this.Text, text, icon);
        }

        /// <summary>
        /// Indicates that an operation is starting by optionally displaying a working
        /// animation and optionally displaying a status toast.
        /// </summary>
        /// <param name="animatedIcon">The optional notify icon animation.</param>
        /// <param name="toastText">The optional toast text.</param>
        private void StartOperation(AnimatedIcon animatedIcon = null, string toastText = null)
        {
            operationInProgress = true;

            if (animatedIcon != null)
            {
                StartNotifyAnimation(animatedIcon);
            }

            if (!string.IsNullOrEmpty(toastText))
            {
                ShowToast(toastText);
            }
        }

        /// <summary>
        /// Indicates that the current operation has completed.
        /// </summary>
        private void StopOperation()
        {
            StopNotifyAnimation(force: true);
            operationInProgress = false;

            UpdateUIState();
        }

        /// <summary>
        /// Indicates that the current operation failed.
        /// </summary>
        /// <param name="toastErrorText">The optional toast error text.</param>
        private void StopFailedOperation(string toastErrorText = null)
        {
            StopNotifyAnimation(force: true);
            UpdateUIState();

            if (!string.IsNullOrEmpty(toastErrorText))
            {
                ShowToast(toastErrorText, icon: ToolTipIcon.Error);
            }
        }

        /// <summary>
        /// Places the application in the error state.
        /// </summary>
        /// <param name="balloonText">The message to be displayed in the notify icon balloon.</param>
        private void SetErrorState(string balloonText)
        {
            if (InErrorState)
            {
                // Just update the existing error state on the stack.

                notifyStack.Peek().BalloonText = balloonText;
            }
            else
            {
                StartNotifyAnimation(errorAnimation, balloonText, isError: true);
            }

            if (!string.IsNullOrEmpty(balloonText))
            {
                SetBalloonText(balloonText);
            }
        }

        /// <summary>
        /// Returns <c>true</c> if the application is currently in an error state.
        /// </summary>
        private bool InErrorState => notifyStack.Count > 0 && notifyStack.Peek().IsError;

        /// <summary>
        /// Resets the application error state.
        /// </summary>
        private void ResetErrorState()
        {
            if (!InErrorState)
            {
                return;
            }

            StopNotifyAnimation();
        }

        /// <summary>
        /// Stops any running reverse proxies.
        /// </summary>
        private void StopProxies()
        {
            foreach (var proxy in proxies)
            {
                proxy.Dispose();
            }

            proxies.Clear();
        }

        /// <summary>
        /// Updates the running proxies to match the current cluster 
        /// (if there is one).
        /// </summary>
        private void UpdateProxies()
        {
            if (KubeHelper.CurrentContext == null)
            {
                StopProxies();
            }
            else
            {
                var cluster = Program.GetCluster();

                // We're going to use the current Kubenetes context name and cluster ID
                // to determine whether we're still connected to the same cluster.

                if (proxiedContext != null)
                {
                    if (proxiedContext.Name == KubeHelper.CurrentContext.Name &&
                        proxiedContext.Extension.ClusterId == KubeHelper.CurrentContext.Extension.ClusterId)
                    {
                        // We're still proxying the same cluster so no changes 
                        // are required.

                        return;
                    }
                }

                StopProxies();

                if (KubeHelper.CurrentContext == null)
                {
                    // Wr're not logged into a cluster so don't start any proxies.

                    return;
                }

                //-------------------------------------------------------------
                // The Kubernetes dashboard reverse proxy.

                // Setup a callback that transparently adds an [Authentication] header
                // to all requests with the correct bearer token.  We'll need to
                // obtain the token secret via two steps:
                //
                //      1. Identify the dashboard token secret by listing all secrets
                //         in the [kube-system] namespace looking for one named like
                //         [root-user-token-*].
                //
                //      2. Reading that secret and extracting the value.

                var response       = KubeHelper.Kubectl("--namespace", "kube-system", "get", "secrets", "-o=name");
                var secretName     = string.Empty;
                var dashboardToken = string.Empty;

                if (response.ExitCode != 0)
                {
                    try
                    {
                        response.EnsureSuccess();
                    }
                    catch (Exception e)
                    {
                        LogError(e);
                        SetErrorState($"{KubeHelper.CurrentContextName}: Kubernetes API failure");
                        return;
                    }
                }

                // Step 1: Determine the secret name.

                using (var reader = new StringReader(response.OutputText))
                {
                    const string secretPrefix = "secret/";

                    secretName = reader.Lines().FirstOrDefault(line => line.StartsWith($"{secretPrefix}root-user-token-"));

                    Covenant.Assert(!string.IsNullOrEmpty(secretName));

                    secretName = secretName.Substring(secretPrefix.Length);
                }

                // Step 2: Describe the secret and extract the token value.  This
                //         is a bit of a hack because I'm making assumptions about
                //         the output format.

                response = KubeHelper.Kubectl("--namespace", "kube-system", "describe", "secret", secretName);

                if (response.ExitCode != 0)
                {
                    try
                    {
                        response.EnsureSuccess();
                    }
                    catch (Exception e)
                    {
                        LogError(e);
                        SetErrorState($"{KubeHelper.CurrentContextName}: Kubernetes API failure");
                        return;
                    }
                }

                using (var reader = new StringReader(response.OutputText))
                {
                    var tokenLine = reader.Lines().FirstOrDefault(line => line.StartsWith("token:"));

                    Covenant.Assert(!string.IsNullOrEmpty(tokenLine));

                    dashboardToken = tokenLine.Split(new char[] { ' ' }, 2).Skip(1).First().Trim();
                }

                Action<RequestContext> dashboardRequestHandler =
                    context =>
                    {
                        context.Request.Headers.Add("Authorization", $"Bearer {dashboardToken}");
                    };

                // Start the proxy.

                var userContext   = KubeHelper.Config.GetUser(KubeHelper.CurrentContext.Properties.User);
                var certPem       = Encoding.UTF8.GetString(Convert.FromBase64String(userContext.Properties.ClientCertificateData));
                var keyPem        = Encoding.UTF8.GetString(Convert.FromBase64String(userContext.Properties.ClientKeyData));
                var dashboardCert = TlsCertificate.Parse(KubeHelper.CurrentContext.Extension.KubernetesDashboardCertificate).ToX509(publicOnly: true);

                var kubeDashboardProxy =
                    new ReverseProxy(
                        localPort:        KubeHelper.ClientConfig.KubeDashboardProxyPort,
                        remotePort:       KubeHostPorts.KubeDashboard,
                        remoteHost:       cluster.GetReachableMaster().PrivateAddress.ToString(),
                        validCertificate: dashboardCert,
                        requestHandler:   dashboardRequestHandler);

                proxies.Add(kubeDashboardProxy);

                //-------------------------------------------------------------
                // Remember which cluster context we're proxying.

                proxiedContext = KubeHelper.CurrentContext;
            }
        }

        //---------------------------------------------------------------------
        // These methods are called by [DesktopService] (and perhaps from some
        // other places):

        /// <summary>
        /// Synchronizes the UI state with the current cluster configuration.
        /// </summary>
        public void UpdateUIState()
        {
            InvokeOnUIThread(
                () =>
                {
                    KubeHelper.LoadConfig();

                    UpdateProxies();

                    if (InErrorState)
                    {
                        return;
                    }

                    if (!operationInProgress)
                    {
                        notifyIcon.Icon = IsConnected ? connectedIcon : disconnectedIcon;

                        if (notifyStack.Count > 0 && !string.IsNullOrEmpty(notifyStack.Peek().BalloonText))
                        {
                            SetBalloonText(notifyStack.Peek().BalloonText);
                        }
                        else if (IsConnected)
                        {
                            SetBalloonText($"{Text}: {KubeHelper.CurrentContextName}");
                        }
                        else
                        {
                            SetBalloonText($"{Text}: disconnected");
                        }

                        return;
                    }

                    if (remoteOperation != null)
                    {
                        if (Process.GetProcessById(remoteOperation.ProcessId) == null)
                        {
                            // The original [neon-cli] process is no longer running;
                            // it must have terminated before signalling the end
                            // of the operation.  We're going to terminate the
                            // operation status.
                            //
                            // This is an important fail-safe.

                            StopOperation();
                            return;
                        }
                    }
                    else
                    {
                        notifyIcon.Icon = IsConnected ? connectedIcon : disconnectedIcon;

                        if (IsConnected)
                        {
                            SetBalloonText($"{Text}: {KubeHelper.CurrentContextName}");
                        }
                        else
                        {
                            SetBalloonText($"{Text}: disconnected");
                        }
                    }
                });
        }

        /// <summary>
        /// Signals the start of a long-running operation.
        /// </summary>
        /// <param name="operation">The <b>neon-cli</b> operation information.</param>
        public void OnStartOperation(RemoteOperation operation)
        {
            InvokeOnUIThread(
                () =>
                {
                    if (operationInProgress)
                    {
                        // Another operation is already in progress.  If the current
                        // operation was initiated by the same [neon-cli] process then
                        // we'll just substitute the new operation info otherwise
                        // we'll start a new operation.
                        //
                        // If the operation was initiated by the Desktop app then
                        // we'll ignore the new operation.

                        if (remoteOperation != null && remoteOperation.ProcessId == operation.ProcessId)
                        {
                            remoteOperation = operation;
                            UpdateUIState();
                        }
                        else
                        {
                            remoteOperation = operation;
                            StartOperation(workingAnimation);
                        }

                        return;
                    }
                    else
                    {
                        remoteOperation = operation;
                        StartOperation(workingAnimation);
                    }
                });
        }

        /// <summary>
        /// Signals the end of a long-running operation.
        /// </summary>
        /// <param name="operation">The <b>neon-cli</b> operation information.</param>
        public void OnEndOperation(RemoteOperation operation)
        {
            InvokeOnUIThread(
                () =>
                {
                    if (operationInProgress)
                    {
                        // Stop the operation only if the the current operation
                        // was initiated by the same [neon-cli] process that
                        // started the operation.

                        if (remoteOperation != null && remoteOperation.ProcessId == operation.ProcessId)
                        {
                            remoteOperation = null;
                            StopOperation();

                            if (!string.IsNullOrEmpty(operation.CompletedToast))
                            {
                                ShowToast(operation.CompletedToast);
                            }
                        }
                    }
                });
        }

        /// <summary>
        /// Signals that the workstation has logged into a cluster.
        /// </summary>
        public void OnLogin()
        {
            InvokeOnUIThread(
                () =>
                {
                    UpdateUIState();
                });
        }

        /// <summary>
        /// Signals that the workstation has logged out of a cluster.
        /// </summary>
        public void OnLogout()
        {
            InvokeOnUIThread(
                () =>
                {
                    UpdateUIState();
                });
        }

        /// <summary>
        /// Logs an exception as an error.
        /// </summary>
        /// <param name="e">The exception.</param>
        public void LogError(Exception e)
        {
            lock (syncLock)
            {
                // $todo(jeff.lill): Implement this
            }
        }

        /// <summary>
        /// Logs an error message.
        /// </summary>
        /// <param name="message">The message.</param>
        public void LogError(string message)
        {
            lock (syncLock)
            {
                // $todo(jeff.lill): Implement this
            }
        }

        /// <summary>
        /// Logs a warning message.
        /// </summary>
        /// <param name="message">The message.</param>
        public void LogWarning(string message)
        {
            lock (syncLock)
            {
                // $todo(jeff.lill): Implement this
            }
        }

        /// <summary>
        /// Logs an informational message.
        /// </summary>
        /// <param name="message">The message.</param>
        public void LogInfo(string message)
        {
            lock (syncLock)
            {
                // $todo(jeff.lill): Implement this
            }
        }

        //---------------------------------------------------------------------
        // Menu commands

        /// <summary>
        /// Poulates the context menu when it is clicked, based on the current
        /// application state.
        /// </summary>
        /// <param name="sender">The sender.</param>
        /// <param name="args">The arguments.</param>
        private void Menu_Popup(object sender, EventArgs args)
        {
            contextMenu.MenuItems.Clear();

            // Append submenus for each of the cluster contexts that have
            // neonKUBE extensions.  We're not going to try to manage 
            // non-neonKUBE clusters.
            //
            // Put a check mark next to the logged in cluster (if there
            // is one) and also enable [Logout] if we're logged in.

            var contexts = KubeHelper.Config.Contexts
                .Where(c => c.Extension != null)
                .OrderBy(c => c.Name)
                .ToArray();

            var currentContextName = (string)KubeHelper.CurrentContextName;
            var loggedIn           = !string.IsNullOrEmpty(currentContextName);;

            if (contexts.Length > 0)
            {
                var contextsMenu = new MenuItem(loggedIn ? currentContextName : "Login to") { Checked = loggedIn, Enabled = !operationInProgress };

                contextsMenu.RadioCheck = loggedIn;

                if (loggedIn)
                {
                    contextsMenu.MenuItems.Add(new MenuItem(currentContextName) { Checked = true, Enabled = !operationInProgress });
                }

                var addedContextsSeparator = false;

                foreach (var context in contexts.Where(c => c.Name != currentContextName))
                {
                    if (!addedContextsSeparator)
                    {
                        contextsMenu.MenuItems.Add("-");
                        addedContextsSeparator = true;
                    }

                    contextsMenu.MenuItems.Add(new MenuItem(context.Name, OnClusterContext) { Enabled = !operationInProgress });
                }

                contextsMenu.MenuItems.Add("-");
                contextsMenu.MenuItems.Add(new MenuItem("Logout", OnLogoutCommand) { Enabled = loggedIn && !operationInProgress });

                contextMenu.MenuItems.Add(contextsMenu);
            }

            // Append cluster-specific menus.

            if (loggedIn)
            {
                contextMenu.MenuItems.Add("-");

                var dashboardsMenu = new MenuItem("Dashboard") { Enabled = loggedIn && !operationInProgress };

                dashboardsMenu.MenuItems.Add(new MenuItem("Kubernetes", OnKubernetesDashboardCommand) { Enabled = loggedIn && !operationInProgress });

#if TODO
                var addedDashboardSeparator = false;

                if (KubeHelper.CurrentContext.Extension.ClusterDefinition.Ceph.Enabled)
                {
                    if (!addedDashboardSeparator)
                    {
                        dashboardsMenu.MenuItems.Add(new MenuItem("-"));
                        addedDashboardSeparator = true;
                    }

                    dashboardsMenu.MenuItems.Add(new MenuItem("Ceph", OnCephDashboardCommand) { Enabled = loggedIn && !operationInProgress });
                }

                if (KubeHelper.CurrentContext.Extension.ClusterDefinition.EFK.Enabled)
                {
                    if (!addedDashboardSeparator)
                    {
                        dashboardsMenu.MenuItems.Add(new MenuItem("-"));
                        addedDashboardSeparator = true;
                    }

                    dashboardsMenu.MenuItems.Add(new MenuItem("Kibana", OnKibanaDashboardCommand) { Enabled = loggedIn && !operationInProgress });
                }

                if (KubeHelper.CurrentContext.Extension.ClusterDefinition.Prometheus.Enabled)
                {
                    if (!addedDashboardSeparator)
                    {
                        dashboardsMenu.MenuItems.Add(new MenuItem("-"));
                        addedDashboardSeparator = true;
                    }

                    dashboardsMenu.MenuItems.Add(new MenuItem("Prometheus", OnPrometheusDashboardCommand) { Enabled = loggedIn && !operationInProgress });
                }
#endif

                contextMenu.MenuItems.Add(dashboardsMenu);
            }

            // Append the static commands.

            contextMenu.MenuItems.Add("-");
            contextMenu.MenuItems.Add(new MenuItem("GitHub", OnGitHubCommand));
            contextMenu.MenuItems.Add(new MenuItem("Help", OnHelpCommand));
            contextMenu.MenuItems.Add(new MenuItem("About", OnAboutCommand));
            contextMenu.MenuItems.Add("-");
            contextMenu.MenuItems.Add(new MenuItem("Settings", OnSettingsCommand));
            contextMenu.MenuItems.Add(new MenuItem("Check for Updates", OnCheckForUpdatesCommand) { Enabled = !operationInProgress });
            contextMenu.MenuItems.Add("-");
            contextMenu.MenuItems.Add(new MenuItem("Exit", OnExitCommand));
        }

        /// <summary>
        /// Handles the <b>Github</b> command.
        /// </summary>
        /// <param name="sender">The sender.</param>
        /// <param name="args">The arguments.</param>
        private async void OnGitHubCommand(object sender, EventArgs args)
        {
            StartOperation(workingAnimation);

            try
            {
                var clientInfo = await Headend.GetClientInfoAsync();

                NeonHelper.OpenBrowser(clientInfo.GitHubUrl);
            }
            catch
            {
                StopFailedOperation(headendError);
                return;
            }
            finally
            {
                StopOperation();
            }
        }

        /// <summary>
        /// Handles the <b>Help</b> command.
        /// </summary>
        /// <param name="sender">The sender.</param>
        /// <param name="args">The arguments.</param>
        private async void OnHelpCommand(object sender, EventArgs args)
        {
            StartOperation(workingAnimation);

            try
            {
                var clientInfo = await Headend.GetClientInfoAsync();

                NeonHelper.OpenBrowser(clientInfo.HelpUrl);
            }
            catch
            {
                StopFailedOperation(headendError);
                return;
            }
            finally
            {
                StopOperation();
            }
        }

        /// <summary>
        /// Handles the <b>About</b> command.
        /// </summary>
        /// <param name="sender">The sender.</param>
        /// <param name="args">The arguments.</param>
        private void OnAboutCommand(object sender, EventArgs args)
        {
            var aboutBox = new AboutBox();

            aboutBox.ShowDialog();
        }

        /// <summary>
        /// Handles the <b>Settings</b> command.
        /// </summary>
        /// <param name="sender">The sender.</param>
        /// <param name="args">The arguments.</param>
        private void OnSettingsCommand(object sender, EventArgs args)
        {
            MessageBox.Show("$todo(jeff.lill): Not implemented yet.", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }

        /// <summary>
        /// Handles the <b>Settings</b> command.
        /// </summary>
        /// <param name="sender">The sender.</param>
        /// <param name="args">The arguments.</param>
        private async void OnCheckForUpdatesCommand(object sender, EventArgs args)
        {
            StartOperation(workingAnimation);

            try
            {
                var clientInfo = await Headend.GetClientInfoAsync();

                if (clientInfo.UpdateVersion == null)
                {
                    MessageBox.Show("The latest version of neonKUBE is installed.", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
                else
                {
                    MessageBox.Show("$todo(jeff.lill): Not implemented yet.", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
            }
            catch
            {
                StopFailedOperation("Update check failed");
                return;
            }
            finally
            {
                StopOperation();
            }
        }

        /// <summary>
        /// Handles cluster context commands.
        /// </summary>
        /// <param name="sender">The sender.</param>
        /// <param name="args">The arguments.</param>
        private void OnClusterContext(object sender, EventArgs args)
        {
            // The cluster context name is the text of the sending menu item.

            var menuItem    = (MenuItem)sender;
            var contextName = menuItem.Text;

            StartOperation(connectingAnimation);

            try
            {
                KubeHelper.SetCurrentContext(contextName);
                ShowToast($"Logged into: {contextName}");
            }
            catch
            {
                StopFailedOperation($"Cannot log into: {contextName}");
            }
            finally
            {
                StopOperation();
            }
        }

        /// <summary>
        /// Handles the <b>Logout</b> command.
        /// </summary>
        /// <param name="sender">The sender.</param>
        /// <param name="args">The arguments.</param>
        private void OnLogoutCommand(object sender, EventArgs args)
        {
            if (KubeHelper.CurrentContext != null)
            {
                ShowToast($"Logging out of: {KubeHelper.CurrentContext.Name}");
                KubeHelper.SetCurrentContext((string)null);
                UpdateUIState();
            }
        }

        /// <summary>
        /// Handles the <b>Kubernetes Dashboard</b> command.
        /// </summary>
        /// <param name="sender">The sender.</param>
        /// <param name="args">The arguments.</param>
        private void OnKubernetesDashboardCommand(object sender, EventArgs args)
        {
            NeonHelper.OpenBrowser($"http://localhost:{KubeHelper.ClientConfig.KubeDashboardProxyPort}/");
        }

        /// <summary>
        /// Handles the <b>Ceph Dashboard</b> command.
        /// </summary>
        /// <param name="sender">The sender.</param>
        /// <param name="args">The arguments.</param>
        private void OnCephDashboardCommand(object sender, EventArgs args)
        {
            MessageBox.Show("$todo(jeff.lill): Not implemented yet.", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }

        /// <summary>
        /// Handles the <b>Kibana Dashboard</b> command.
        /// </summary>
        /// <param name="sender">The sender.</param>
        /// <param name="args">The arguments.</param>
        private void OnKibanaDashboardCommand(object sender, EventArgs args)
        {
            MessageBox.Show("$todo(jeff.lill): Not implemented yet.", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }

        /// <summary>
        /// Handles the <b>Prometheus Dashboard</b> command.
        /// </summary>
        /// <param name="sender">The sender.</param>
        /// <param name="args">The arguments.</param>
        private void OnPrometheusDashboardCommand(object sender, EventArgs args)
        {
            MessageBox.Show("$todo(jeff.lill): Not implemented yet.", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }

        /// <summary>
        /// Handles the <b>Exit</b> command.
        /// </summary>
        /// <param name="sender">The sender.</param>
        /// <param name="args">The arguments.</param>
        private void OnExitCommand(object sender, EventArgs args)
        {
            StopNotifyAnimation(force: true);
            notifyIcon.Visible = false;
            DesktopService.Stop();

            Environment.Exit(0);
        }
    }
}
