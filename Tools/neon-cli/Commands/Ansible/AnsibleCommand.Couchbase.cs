﻿//-----------------------------------------------------------------------------
// FILE:	    AnsibleCommand.Module.Couchbase.cs
// CONTRIBUTOR: Jeff Lill
// COPYRIGHT:	Copyright (c) 2016-2018 by neonFORGE, LLC.  All rights reserved.

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Runtime.Serialization;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Consul;
using Couchbase;
using ICSharpCode.SharpZipLib.Zip;
using Newtonsoft;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

using Neon.Cluster;
using Neon.Cryptography;
using Neon.Common;
using Neon.Data;
using Neon.IO;
using Neon.Net;

namespace NeonCli
{
    public partial class AnsibleCommand : CommandBase
    {
        //---------------------------------------------------------------------
        // Common Couchbase module code.

        private enum CouchbaseFileFormat
        {
            /// <summary>
            /// Format with one JSON document per line.
            /// </summary>
            [EnumMember(Value = "json-lines")]
            JsonLines = 0,

            /// <summary>
            /// Format as a JSON array of documents.
            /// </summary>
            [EnumMember(Value = "json-array")]
            JsonArray
        }

        /// <summary>
        /// Common Couchbase module arguments.
        /// </summary>
        private class CouchbaseArgs
        {
            /// <summary>
            /// Constructor.
            /// </summary>
            public CouchbaseArgs()
            {
                Settings    = new CouchbaseSettings();
                Credentials = new Credentials();
            }

            /// <summary>
            /// The Couchbase settings.
            /// </summary>
            public CouchbaseSettings Settings { get; set; }

            /// <summary>
            /// The Couchbase credentials.
            /// </summary>
            public Credentials Credentials { get; set; }
        }

        /// <summary>
        /// Parses the Couchbase settings from the common module arguments.
        /// </summary>
        /// <param name="context">The module context.</param>
        /// <returns>The <see cref="CouchbaseArgs"/> or <c>null</c> if there's an error.</returns>
        private CouchbaseArgs ParseCouchbaseSettings(ModuleContext context)
        {
            var cluster       = NeonClusterHelper.Cluster;
            var nodeGroups    = cluster.Definition.GetNodeGroups(excludeAllGroup: true);
            var couchbaseArgs = new CouchbaseArgs();

            var servers = context.ParseStringArray("servers");

            if (servers.Count == 0)
            {
                throw new ArgumentException($"[servers] must specify at least one Couchbase server.");
            }

            var ssl = context.ParseBool("ssl");

            if (!ssl.HasValue)
            {
                ssl = false;
            }

            var scheme = ssl.Value ? "https" : "http";

            var port = context.ParseInt("port", v => 0 < v && v <= ushort.MaxValue);

            foreach (var server in servers)
            {
                // The server can be an IP address, FQDN (with at least one dot), a cluster
                // node name or a cluster node group name.

                if (IPAddress.TryParse(server, out var address))
                {
                    couchbaseArgs.Settings.Servers.Add(new Uri($"{scheme}://{address}:{port}"));
                }
                else if (server.Contains("."))
                {
                    // Must be a FQDN.

                    couchbaseArgs.Settings.Servers.Add(new Uri($"{scheme}://{server}:{port}"));
                }
                else if (nodeGroups.TryGetValue(server, out var group))
                {
                    // It's a node group so add a URL with the IP address for each
                    // node in the group.

                    foreach (var node in group)
                    {
                        couchbaseArgs.Settings.Servers.Add(new Uri($"{scheme}://{node.PrivateAddress}:{port}"));
                    }
                }
                else
                {
                    // Must be a node name.

                    if (cluster.Definition.NodeDefinitions.TryGetValue(server, out var node))
                    {
                        couchbaseArgs.Settings.Servers.Add(new Uri($"{scheme}://{node.PrivateAddress}:{port}"));
                    }
                    else
                    {
                        context.WriteErrorLine($"[{server}] is not a valid IP address, FQDN, or known cluster node or node group name.");
                        return null;
                    }
                }
            }

            couchbaseArgs.Settings.Bucket = context.ParseString("bucket", v => !string.IsNullOrWhiteSpace(v));

            couchbaseArgs.Credentials.Username = context.ParseString("username");
            couchbaseArgs.Credentials.Password = context.ParseString("password");

            if (!couchbaseArgs.Settings.IsValid)
            {
                context.WriteErrorLine("Invalid Couchbase connection settings.");
                return null;
            }

            return couchbaseArgs;
        }
    }
}
