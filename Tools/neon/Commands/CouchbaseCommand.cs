﻿//-----------------------------------------------------------------------------
// FILE:	    CouchbaseCommand.cs
// CONTRIBUTOR: Jeff Lill
// COPYRIGHT:	Copyright (c) 2016-2017 by NeonForge, LLC.  All rights reserved.

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Consul;
using Couchbase;
using Couchbase.Configuration.Client;
using Couchbase.Core.Serialization;
using ICSharpCode.SharpZipLib.Zip;
using Newtonsoft;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

using Neon.Cluster;
using Neon.Common;
using Neon.Cryptography;
using Neon.Data;

namespace NeonTool
{
    /// <summary>
    /// Implements the <b>couchbase</b> command.
    /// </summary>
    public class CouchbaseCommand : CommandBase
    {
        private const string usage = @"
Implements Couchbase commands

USAGE:

    neon couchbase query TARGET QUERY
    neon couchbase query TARGET @QUERY-FILE
    neon couchbase query TARGET -

    neon couchbase upsert [--key=KEY] TARGET JSON
    neon couchbase upsert [--key=KEY] TARGET @JSON-FILE
    neon couchbase upsert [--key=KEY] TARGET -

ARGUMENTS:

    TARGET         - The target server, credentials, and bucket
                     formatted like:

                        http(s)://HOST:PORT@USER:PASSWORD:BUCKET
                        couchbase://HOST@USER:PASSWORD:BUCKET

    QUERY           - A N1QL query (quoted as required).
    QUERY-FILE      - Name of a file with a N1QL query.
    JSON            - JSON object being upserted.
    @JSON-FILE      - Name of the file with the JSON object
                      being upserted.
    -               - The query or JSON will be read from STDIN.

OPTIONS:

    --key           - (optional) key to user for upserts
    
COMMANDS:

    query       Performs a N1QL query and writes the JSON results
                to STDOUT.

    upsert      Upserts a JSON object to the database using KEY as
                the object key if present, using a GUID otherwise. 
";
        /// <inheritdoc/>
        public override string[] Words
        {
            get { return new string[] { "couchbase" }; }
        }

        /// <inheritdoc/>
        public override void Help()
        {
            Console.WriteLine(usage);
        }

        /// <inheritdoc/>
        public override void Run(CommandLine commandLine)
        {
            if (commandLine.HasHelpOption || commandLine.Arguments.Length == 0)
            {
                Console.WriteLine(usage);
                Program.Exit(0);
            }

            Program.ConnectCluster();

            // Process the command arguments.

            var command = commandLine.Arguments[0];

            commandLine = commandLine.Shift(1);

            if (commandLine.Arguments.Length == 0)
            {
                Console.WriteLine("*** ERROR: Expected a TARGET argument like: couchbase://HOST@USER:PASSWORD:BUCKET");
                Program.Exit(1);
            }

            // Open a Couchbase bucket for the TARGET.
            //
            //      http(s)://HOST:PORT@USER:PASSWORD:BUCKET
            //      couchbase://HOST@USER:PASSWORD:BUCKET

            var target = commandLine.Arguments[0];
            var error  = $"*** ERROR: [{target}] is not a valid Couchbase target.";
            var fields = target.Split(new char[] { '@' }, 2);

            if (fields.Length != 2)
            {
                Console.WriteLine(error);
                Program.Exit(1);
            }

            var uri = fields[0];

            fields = fields[1].Split(':');

            if (fields.Length != 3)
            {
                Console.WriteLine(error);
                Program.Exit(1);
            }

            var username   = fields[0];
            var password   = fields[1];
            var bucketName = fields[2];
            var config     = new CouchbaseSettings();

            config.Servers.Clear();
            config.Servers.Add(new Uri(uri));
            config.Bucket = bucketName;

            using (var bucket = config.OpenBucket(username, password))
            {
                commandLine = commandLine.Shift(1);

                switch (command)
                {
                    case "query":

                        // Get the N1QL query.

                        if (commandLine.Arguments.Length != 1)
                        {
                            Console.WriteLine("*** ERROR: QUERY argument expected.");
                            Program.Exit(1);
                        }

                        var query = commandLine.Arguments[0];

                        if (query == "-")
                        {
                            // Read the query from STDIN.

                            query = NeonHelper.ReadStandardInputText();
                        }
                        else if (query.StartsWith("@"))
                        {
                            // Read the query from the file.

                            query = File.ReadAllText(query.Substring(1));
                        }

                        var queryResults = bucket.Query<JToken>(query);

                        Console.WriteLine(JsonConvert.SerializeObject(queryResults, Formatting.Indented));
                        break;

                    case "upsert":

                        if (commandLine.Arguments.Length != 1)
                        {
                            Console.WriteLine("*** ERROR: JSON object argument expected.");
                            Program.Exit(1);
                        }

                        var jsonText = commandLine.Arguments[0];

                        if (jsonText == "-")
                        {
                            // Read the query from STDIN.

                            jsonText = NeonHelper.ReadStandardInputText();
                        }
                        else if (jsonText.StartsWith("@"))
                        {
                            // Read the query from the file.

                            jsonText = File.ReadAllText(jsonText.Substring(1));
                        }

                        var jToken  = JsonConvert.DeserializeObject<JToken>(jsonText);
                        var jObject = jToken as JObject;

                        if (jObject == null)
                        {
                            Console.WriteLine("*** ERROR: JSON argument must be an object.");
                            Program.Exit(1);
                        }

                        var key = commandLine.GetOption("--key");

                        if (string.IsNullOrWhiteSpace(key))
                        {
                            key = EntityHelper.GenerateUuid();
                        }

                        var upsertResult = bucket.Upsert(key, jObject);

                        upsertResult.EnsureSuccess();
                        break;

                    default:

                        Console.Error.WriteLine($"*** ERROR: Unknown subcommand [{command}].");
                        Program.Exit(1);
                        break;
                }
            }
        }

        /// <inheritdoc/>
        public override ShimInfo Shim(DockerShim shim)
        {
            // $todo(jeff.lill): 
            //
            // I suppose this should be shimmed to be consistent with the
            // other commands that are, but it's not that important.

            return new ShimInfo(isShimmed: false, ensureConnection: true);
        }
    }
}
