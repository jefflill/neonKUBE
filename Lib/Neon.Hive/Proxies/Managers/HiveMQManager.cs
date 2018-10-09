﻿//-----------------------------------------------------------------------------
// FILE:	    HiveMQManager.cs
// CONTRIBUTOR: Jeff Lill
// COPYRIGHT:	Copyright (c) 2016-2018 by neonFORGE, LLC.  All rights reserved.

using System;
using System.Collections.Generic;
using System.Diagnostics.Contracts;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Consul;
using Newtonsoft.Json;

using Neon.Common;
using Neon.Cryptography;
using Neon.HiveMQ;
using Neon.IO;
using Neon.Net;
using Neon.Retry;
using Neon.Time;
using EasyNetQ.Management.Client;

namespace Neon.Hive
{
    /// <summary>
    /// Handles hive message cluster related operations.
    /// </summary>
    public sealed class HiveMQManager
    {
        //---------------------------------------------------------------------
        // Local types

        /// <summary>
        /// Provides messaging related services for internal neonHIVE components.
        /// </summary>
        public class InternalManager
        {
            private object          syncLock = new object();
            private HiveMQManager   parent;
            private HiveBus         neonHiveBus;

            /// <summary>
            /// Internal constructor.
            /// </summary>
            /// <param name="parent">The parent <see cref="HiveMQManager"/>.</param>
            internal InternalManager(HiveMQManager parent)
            {
                this.parent = parent;
            }

            /// <summary>
            /// <para>
            /// <b>INTERNAL USE ONLY:</b> Returns a <see cref="HiveBus"/> connected to the hive
            /// <b>neon</b> virtual host.  This property is intended for use only by neonHIVE
            /// tools, services and containers.
            /// </para>
            /// <note>
            /// <b>WARNING:</b> The <see cref="HiveBus"/> instance returned should <b>never be disposed</b>.
            /// </note>
            /// </summary>
            /// <param name="useBootstrap">
            /// Optionally specifies that the settings returned should directly
            /// reference to the HiveMQ cluster nodes rather than routing traffic
            /// through the <b>private</b> load balancer.  This is used internally
            /// to resolve chicken-and-the-egg dilemmas for the load balancer and
            /// proxy implementations that rely on HiveMQ messaging.
            /// </param>
            public HiveBus NeonHiveBus(bool useBootstrap = false)
            {
                var bus = neonHiveBus;

                if (bus != null)
                {
                    return bus;
                }

                lock (syncLock)
                {
                    if (neonHiveBus == null)
                    {
                        neonHiveBus = parent.GetNeonSettings(useBootstrap).ConnectHiveBus();
                    }

                    return neonHiveBus;
                }
            }

            /// <summary>
            /// <b>INTERNAL USE ONLY:</b> Creates the <see cref="HiveMQChannels.ProxyNotify"/> 
            /// channel if it doesn't already exist and returns the channel.
            /// </summary>
            /// <param name="useBootstrap">
            /// Optionally specifies that the settings returned should directly
            /// reference to the HiveMQ cluster nodes rather than routing traffic
            /// through the <b>private</b> load balancer.  This is used internally
            /// to resolve chicken-and-the-egg dilemmas for the load balancer and
            /// proxy implementations that rely on HiveMQ messaging.
            /// </param>
            /// <returns>The <see cref="BroadcastChannel"/>.</returns>
            public BroadcastChannel GetProxyNotifyChannel(bool useBootstrap = false)
            {
                // WARNING:
                //
                // Changing any of the channel properties will require that underlying
                // queue be removed and recreated and all services and containers that
                // use the queue be restarted.

                return NeonHiveBus(useBootstrap).GetBroadcastChannel(
                    name: HiveMQChannels.ProxyNotify,
                    durable: true,
                    messageTTL: null,
                    maxLength: null,
                    maxLengthBytes: null);
            }
        }

        //---------------------------------------------------------------------
        // Implementation

        private HiveProxy   hive;

        /// <summary>
        /// Internal constructor.
        /// </summary>
        /// <param name="hive">The parent <see cref="HiveProxy"/>.</param>
        internal HiveMQManager(HiveProxy hive)
        {
            Covenant.Requires<ArgumentNullException>(hive != null);

            this.hive     = hive;
            this.Internal = new InternalManager(this);
        }

        /// <summary>
        /// Returns a <see cref="ManagementClient"/> instance that can be used to perform
        /// management related operations on the HiveMQ.
        /// </summary>
        /// <param name="useBootstrap">
        /// Optionally specifies that the client returned should connect
        /// directly to the HiveMQ cluster nodes rather than routing traffic
        /// through the <b>private</b> load balancer.  This is used internally
        /// to resolve chicken-and-the-egg dilemmas for the load balancer and
        /// proxy implementations that rely on HiveMQ messaging.
        /// </param>
        public ManagementClient ConnectHiveMQManager(bool useBootstrap = false)
        {
            return GetSystemSettings(useBootstrap).ConnectManager();
        }

        /// <summary>
        /// Parses and returns the specified hive global as a <see cref="HiveMQSettings"/>.
        /// </summary>
        /// <param name="globalName">Identifies the source hive global name.</param>
        /// <returns>The parsed <see cref="HiveMQSettings"/>.</returns>
        /// <exception cref="HiveException">Thrown if the required global setting is not present or could not be deserialized.</exception>
        private HiveMQSettings GetHiveMQSettings(string globalName)
        {
            if (!hive.Globals.TryGetObject<HiveMQSettings>(globalName, out var settings))
            {
                throw new HiveException($"The [{globalName}] hive global does not exist in Consul or could not be parsed as a [{nameof(HiveMQSettings)}..");
            }

            return settings;
        }

        /// <summary>
        /// <para>
        /// Returns the <see cref="HiveMQSettings"/> for the <see cref="HiveMQOptions.SysadminUser"/>.
        /// You can use this to retrieve a client that can perform messaging operations within the
        /// root <b>/</b>, <see cref="HiveMQOptions.AppVHost"/>, or <see cref="HiveMQOptions.NeonVHost"/>
        /// virtual hosts.
        /// </para>
        /// </summary>
        /// <param name="useBootstrap">
        /// Optionally specifies that the settings returned should directly
        /// reference to the HiveMQ cluster nodes rather than routing traffic
        /// through the <b>private</b> load balancer.  This is used internally
        /// to resolve chicken-and-the-egg dilemmas for the load balancer and
        /// proxy implementations that rely on HiveMQ messaging.
        /// </param>
        /// <exception cref="HiveException">Thrown if the required global setting is not present.</exception>
        public HiveMQSettings GetSystemSettings(bool useBootstrap = false)
        {
            var settings = GetHiveMQSettings(HiveGlobals.HiveMQSettingSysadmin);

            if (!useBootstrap)
            {
                return settings;
            }

            var bootstrap = GetBootstrapSettings();

            bootstrap.Username    = settings.Username;
            bootstrap.Password    = settings.Password;
            bootstrap.VirtualHost = settings.VirtualHost;

            return bootstrap;
        }

        /// <summary>
        /// <para>
        /// Returns the <see cref="HiveMQSettings"/> for the <see cref="HiveMQOptions.NeonUser"/>.
        /// You can use this to retrieve a client that can perform messaging operations within the
        /// <see cref="HiveMQOptions.NeonVHost"/> virtual host.
        /// </para>
        /// </summary>
        /// <param name="useBootstrap">
        /// Optionally specifies that the settings returned should directly
        /// reference to the HiveMQ cluster nodes rather than routing traffic
        /// through the <b>private</b> load balancer.  This is used internally
        /// to resolve chicken-and-the-egg dilemmas for the load balancer and
        /// proxy implementations that rely on HiveMQ messaging.
        /// </param>
        /// <exception cref="HiveException">Thrown if the required global setting is not present.</exception>
        public HiveMQSettings GetNeonSettings(bool useBootstrap = false)
        {
            var settings = GetHiveMQSettings(HiveGlobals.HiveMQSettingsNeon);

            if (!useBootstrap)
            {
                return settings;
            }

            var bootstrap = GetBootstrapSettings();

            bootstrap.Username    = settings.Username;
            bootstrap.Password    = settings.Password;
            bootstrap.VirtualHost = settings.VirtualHost;

            return bootstrap;
        }

        /// <summary>
        /// <para>
        /// Returns the <see cref="HiveMQSettings"/> for the <see cref="HiveMQOptions.AppUser"/>.
        /// You can use this to retrieve a client that can perform messaging operations within the
        /// <see cref="HiveMQOptions.AppVHost"/> virtual host.
        /// </para>
        /// </summary>
        /// <param name="useBootstrap">
        /// Optionally specifies that the settings returned should directly
        /// reference to the HiveMQ cluster nodes rather than routing traffic
        /// through the <b>private</b> load balancer.  This is used internally
        /// to resolve chicken-and-the-egg dilemmas for the load balancer and
        /// proxy implementations that rely on HiveMQ messaging.
        /// </param>
        /// <exception cref="HiveException">Thrown if the required global setting is not present.</exception>
        public HiveMQSettings GetAppSettings(bool useBootstrap = false)
        {
            var settings = GetHiveMQSettings(HiveGlobals.HiveMQSettingsApp);

            if (!useBootstrap)
            {
                return settings;
            }

            var bootstrap = GetBootstrapSettings();

            bootstrap.Username    = settings.Username;
            bootstrap.Password    = settings.Password;
            bootstrap.VirtualHost = settings.VirtualHost;

            return bootstrap;
        }

        /// <summary>
        /// <para>
        /// <b>INTERNAL USE ONLY:</b> Returns the bootstrap <see cref="HiveMQSettings"/> 
        /// for the <see cref="HiveMQOptions.NeonVHost"/> that directly reference the HiveMQ
        /// nodes rather than load balancer rules.
        /// </para>
        /// <para>
        /// This works by obtaining the bootstrap settings from the Consul <see cref="HiveGlobals.HiveMQSettingsBootstrap"/>
        /// setting combined with the credentials from <see cref="GetNeonSettings(bool)"/>.
        /// </para>
        /// </summary>
        /// <exception cref="HiveException">Thrown if the required global setting is not present.</exception>
        public HiveMQSettings GetBootstrapSettings()
        {
            return GetHiveMQSettings(HiveGlobals.HiveMQSettingsBootstrap);
        }

        /// <summary>
        /// <para>
        /// <b>Internal use by neonHIVE services only:</b> Generates a <see cref="HiveMQSettings"/> instance
        /// that directly references the HiveMQ nodes and then persists this to Consul as 
        /// <see cref="HiveGlobals.HiveMQSettingsBootstrap"/>.
        /// </para>
        /// <note>
        /// The persisted settings do not include any credentials.
        /// </note>
        /// </summary>
        public void SaveBootstrapSettings()
        {
            var settings = new HiveMQSettings()
            {
                AmqpPort    = HiveHostPorts.HiveMQAMPQ,
                AdminPort   = HiveHostPorts.HiveMQManagement,
                TlsEnabled  = false,
                Username    = null,
                Password    = null,
                VirtualHost = hive.Definition.HiveMQ.NeonVHost
            };

            foreach (var node in hive.Definition.SortedNodes.Where(n => n.Labels.HiveMQ))
            {
                settings.AmqpHosts.Add($"{node.Name}.{hive.Definition.Hostnames.HiveMQ}");
            }

            foreach (var node in hive.Definition.SortedNodes.Where(n => n.Labels.HiveMQManager))
            {
                settings.AdminHosts.Add($"{node.Name}.{hive.Definition.Hostnames.HiveMQ}");
            }

            hive.Globals.Set(HiveGlobals.HiveMQSettingsBootstrap, settings);
        }

        /// <summary>
        /// <b>INTERNAL USE ONLY:</b> Returns a manager used internally by hive components
        /// and services.
        /// </summary>
        public InternalManager Internal { get; private set; }
    }
}
