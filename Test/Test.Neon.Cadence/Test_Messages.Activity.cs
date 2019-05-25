﻿//-----------------------------------------------------------------------------
// FILE:        Test_Messages.Workflow.cs
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
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;

using Neon.Cadence;
using Neon.Cadence.Internal;
using Neon.Common;
using Neon.Cryptography;
using Neon.Data;
using Neon.IO;
using Neon.Time;
using Neon.Xunit;
using Neon.Xunit.Cadence;

using Newtonsoft.Json;
using Test.Neon.Models;
using Xunit;

namespace TestCadence
{
    public partial class Test_Messages
    {
        [Fact]
        [Trait(TestCategory.CategoryTrait, TestCategory.NeonCadence)]
        public void Test_ActivityExecuteRequest()
        {
            ActivityExecuteRequest message;

            using (var stream = new MemoryStream())
            {
                message = new ActivityExecuteRequest();

                Assert.Equal(InternalMessageTypes.ActivityExecuteReply, message.ReplyType);

                // Empty message.

                stream.SetLength(0);
                stream.Write(message.Serialize());
                stream.Seek(0, SeekOrigin.Begin);

                message = ProxyMessage.Deserialize<ActivityExecuteRequest>(stream);
                Assert.NotNull(message);
                Assert.Equal(0, message.RequestId);
                Assert.Null(message.Args);
                Assert.Null(message.Options);

                // Round-trip

                message.RequestId = 555;
                message.Args = new byte[] { 0, 1, 2, 3, 4 };
                message.Options = new InternalActivityOptions()
                {
                    TaskList               = "my-tasklist",
                    ScheduleToCloseTimeout = 1000,
                    ScheduleToStartTimeout = 2000,
                    StartToCloseTimeout    = 3000,
                    HeartbeatTimeout       = 4000,
                    WaitForCancellation    = true,
                    RetryPolicy            = new InternalRetryPolicy() { MaximumInterval = 5 }
                };

                Assert.Equal(555, message.RequestId);
                Assert.Equal(new byte[] { 0, 1, 2, 3, 4 }, message.Args);
                Assert.NotNull(message.Options);
                Assert.Equal("my-tasklist", message.Options.TaskList);
                Assert.Equal(1000, message.Options.ScheduleToCloseTimeout);
                Assert.Equal(2000, message.Options.ScheduleToStartTimeout);
                Assert.Equal(3000, message.Options.StartToCloseTimeout);
                Assert.Equal(4000, message.Options.HeartbeatTimeout);
                Assert.True(message.Options.WaitForCancellation);
                Assert.NotNull(message.Options.RetryPolicy);
                Assert.Equal(5, message.Options.RetryPolicy.MaximumInterval);

                stream.SetLength(0);
                stream.Write(message.Serialize());
                stream.Seek(0, SeekOrigin.Begin);

                message = ProxyMessage.Deserialize<ActivityExecuteRequest>(stream);
                Assert.NotNull(message);
                Assert.Equal(555, message.RequestId);
                Assert.Equal(new byte[] { 0, 1, 2, 3, 4 }, message.Args);
                Assert.NotNull(message.Options);
                Assert.Equal("my-tasklist", message.Options.TaskList);
                Assert.Equal(1000, message.Options.ScheduleToCloseTimeout);
                Assert.Equal(2000, message.Options.ScheduleToStartTimeout);
                Assert.Equal(3000, message.Options.StartToCloseTimeout);
                Assert.Equal(4000, message.Options.HeartbeatTimeout);
                Assert.True(message.Options.WaitForCancellation);
                Assert.NotNull(message.Options.RetryPolicy);
                Assert.Equal(5, message.Options.RetryPolicy.MaximumInterval);

                // Echo the message via the connection's web server and verify.

                message = EchoToClient(message);
                Assert.NotNull(message);
                Assert.Equal(555, message.RequestId);
                Assert.Equal(new byte[] { 0, 1, 2, 3, 4 }, message.Args);
                Assert.NotNull(message.Options);
                Assert.Equal("my-tasklist", message.Options.TaskList);
                Assert.Equal(1000, message.Options.ScheduleToCloseTimeout);
                Assert.Equal(2000, message.Options.ScheduleToStartTimeout);
                Assert.Equal(3000, message.Options.StartToCloseTimeout);
                Assert.Equal(4000, message.Options.HeartbeatTimeout);
                Assert.True(message.Options.WaitForCancellation);
                Assert.NotNull(message.Options.RetryPolicy);
                Assert.Equal(5, message.Options.RetryPolicy.MaximumInterval);

                // Echo the message via the associated [cadence-proxy] and verify.

                message = EchoToProxy(message);
                Assert.NotNull(message);
                Assert.Equal(555, message.RequestId);
                Assert.Equal(new byte[] { 0, 1, 2, 3, 4 }, message.Args);
                Assert.NotNull(message.Options);
                Assert.Equal("my-tasklist", message.Options.TaskList);
                Assert.Equal(1000, message.Options.ScheduleToCloseTimeout);
                Assert.Equal(2000, message.Options.ScheduleToStartTimeout);
                Assert.Equal(3000, message.Options.StartToCloseTimeout);
                Assert.Equal(4000, message.Options.HeartbeatTimeout);
                Assert.True(message.Options.WaitForCancellation);
                Assert.NotNull(message.Options.RetryPolicy);
                Assert.Equal(5, message.Options.RetryPolicy.MaximumInterval);
            }
        }

        [Fact]
        [Trait(TestCategory.CategoryTrait, TestCategory.NeonCadence)]
        public void Test_ActivityExecuteReply()
        {
            ActivityExecuteReply message;

            using (var stream = new MemoryStream())
            {
                message = new ActivityExecuteReply();

                // Empty message.

                stream.SetLength(0);
                stream.Write(message.Serialize());
                stream.Seek(0, SeekOrigin.Begin);

                message = ProxyMessage.Deserialize<ActivityExecuteReply>(stream);
                Assert.NotNull(message);
                Assert.Equal(0, message.RequestId);
                Assert.Null(message.Error);
                Assert.Null(message.Result);

                // Round-trip

                message.RequestId = 555;
                Assert.Equal(555, message.RequestId);
                message.Error = new CadenceError("MyError");
                Assert.Equal("MyError", message.Error.String);
                message.Result = new byte[] { 5, 6, 7, 8, 9 };
                Assert.Equal(new byte[] { 5, 6, 7, 8, 9 }, message.Result);

                stream.SetLength(0);
                stream.Write(message.Serialize());
                stream.Seek(0, SeekOrigin.Begin);

                message = ProxyMessage.Deserialize<ActivityExecuteReply>(stream);
                Assert.NotNull(message);
                Assert.Equal(555, message.RequestId);
                Assert.Equal("MyError", message.Error.String);
                Assert.Equal(new byte[] { 5, 6, 7, 8, 9 }, message.Result);

                // Echo the message via the connection's web server and verify.

                message = EchoToClient(message);
                Assert.NotNull(message);
                Assert.Equal(555, message.RequestId);
                Assert.Equal("MyError", message.Error.String);
                Assert.Equal(new byte[] { 5, 6, 7, 8, 9 }, message.Result);

                // Echo the message via the associated [cadence-proxy] and verify.

                message = EchoToProxy(message);
                Assert.NotNull(message);
                Assert.Equal(555, message.RequestId);
                Assert.Equal("MyError", message.Error.String);
                Assert.Equal(new byte[] { 5, 6, 7, 8, 9 }, message.Result);
            }
        }

        [Fact]
        [Trait(TestCategory.CategoryTrait, TestCategory.NeonCadence)]
        public void Test_ActivityInvokeRequest()
        {
            ActivityInvokeRequest message;

            using (var stream = new MemoryStream())
            {
                message = new ActivityInvokeRequest();

                Assert.Equal(InternalMessageTypes.ActivityInvokeReply, message.ReplyType);

                // Empty message.

                stream.SetLength(0);
                stream.Write(message.Serialize());
                stream.Seek(0, SeekOrigin.Begin);

                message = ProxyMessage.Deserialize<ActivityInvokeRequest>(stream);
                Assert.NotNull(message);
                Assert.Equal(0, message.RequestId);
                Assert.Null(message.Args);

                // Round-trip

                message.RequestId = 555;
                message.Args = new byte[] { 0, 1, 2, 3, 4 };

                Assert.Equal(555, message.RequestId);
                Assert.Equal(new byte[] { 0, 1, 2, 3, 4 }, message.Args);

                stream.SetLength(0);
                stream.Write(message.Serialize());
                stream.Seek(0, SeekOrigin.Begin);

                message = ProxyMessage.Deserialize<ActivityInvokeRequest>(stream);
                Assert.NotNull(message);
                Assert.Equal(555, message.RequestId);
                Assert.Equal(new byte[] { 0, 1, 2, 3, 4 }, message.Args);

                // Echo the message via the connection's web server and verify.

                message = EchoToClient(message);
                Assert.NotNull(message);
                Assert.Equal(555, message.RequestId);
                Assert.Equal(new byte[] { 0, 1, 2, 3, 4 }, message.Args);

                // Echo the message via the associated [cadence-proxy] and verify.

                message = EchoToProxy(message);
                Assert.NotNull(message);
                Assert.Equal(555, message.RequestId);
                Assert.Equal(new byte[] { 0, 1, 2, 3, 4 }, message.Args);
            }
        }

        [Fact]
        [Trait(TestCategory.CategoryTrait, TestCategory.NeonCadence)]
        public void Test_ActivityInvokeReply()
        {
            ActivityInvokeReply message;

            using (var stream = new MemoryStream())
            {
                message = new ActivityInvokeReply();

                // Empty message.

                stream.SetLength(0);
                stream.Write(message.Serialize());
                stream.Seek(0, SeekOrigin.Begin);

                message = ProxyMessage.Deserialize<ActivityInvokeReply>(stream);
                Assert.NotNull(message);
                Assert.Equal(0, message.RequestId);
                Assert.Null(message.Error);
                Assert.Null(message.Result);

                // Round-trip

                message.RequestId = 555;
                Assert.Equal(555, message.RequestId);
                message.Error = new CadenceError("MyError");
                Assert.Equal("MyError", message.Error.String);
                message.Result = new byte[] { 5, 6, 7, 8, 9 };
                Assert.Equal(new byte[] { 5, 6, 7, 8, 9 }, message.Result);

                stream.SetLength(0);
                stream.Write(message.Serialize());
                stream.Seek(0, SeekOrigin.Begin);

                message = ProxyMessage.Deserialize<ActivityInvokeReply>(stream);
                Assert.NotNull(message);
                Assert.Equal(555, message.RequestId);
                Assert.Equal("MyError", message.Error.String);
                Assert.Equal(new byte[] { 5, 6, 7, 8, 9 }, message.Result);

                // Echo the message via the connection's web server and verify.

                message = EchoToClient(message);
                Assert.NotNull(message);
                Assert.Equal(555, message.RequestId);
                Assert.Equal("MyError", message.Error.String);
                Assert.Equal(new byte[] { 5, 6, 7, 8, 9 }, message.Result);

                // Echo the message via the associated [cadence-proxy] and verify.

                message = EchoToProxy(message);
                Assert.NotNull(message);
                Assert.Equal(555, message.RequestId);
                Assert.Equal("MyError", message.Error.String);
                Assert.Equal(new byte[] { 5, 6, 7, 8, 9 }, message.Result);
            }
        }

        [Fact]
        [Trait(TestCategory.CategoryTrait, TestCategory.NeonCadence)]
        public void Test_ActivityGetHeartbeatDetailsRequest()
        {
            ActivityGetHeartbeatDetailsRequest message;

            using (var stream = new MemoryStream())
            {
                message = new ActivityGetHeartbeatDetailsRequest();

                Assert.Equal(InternalMessageTypes.ActivityGetHeartbeatDetailsReply, message.ReplyType);

                // Empty message.

                stream.SetLength(0);
                stream.Write(message.Serialize());
                stream.Seek(0, SeekOrigin.Begin);

                message = ProxyMessage.Deserialize<ActivityGetHeartbeatDetailsRequest>(stream);
                Assert.NotNull(message);
                Assert.Equal(0, message.RequestId);

                // Round-trip

                message.RequestId = 555;

                Assert.Equal(555, message.RequestId);

                stream.SetLength(0);
                stream.Write(message.Serialize());
                stream.Seek(0, SeekOrigin.Begin);

                message = ProxyMessage.Deserialize<ActivityGetHeartbeatDetailsRequest>(stream);
                Assert.NotNull(message);
                Assert.Equal(555, message.RequestId);

                // Echo the message via the connection's web server and verify.

                message = EchoToClient(message);
                Assert.NotNull(message);
                Assert.Equal(555, message.RequestId);

                // Echo the message via the associated [cadence-proxy] and verify.

                message = EchoToProxy(message);
                Assert.NotNull(message);
                Assert.Equal(555, message.RequestId);
            }
        }

        [Fact]
        [Trait(TestCategory.CategoryTrait, TestCategory.NeonCadence)]
        public void Test_ActivityGetHeartbeatDetailsReply()
        {
            ActivityGetHeartbeatDetailsReply message;

            using (var stream = new MemoryStream())
            {
                message = new ActivityGetHeartbeatDetailsReply();

                // Empty message.

                stream.SetLength(0);
                stream.Write(message.Serialize());
                stream.Seek(0, SeekOrigin.Begin);

                message = ProxyMessage.Deserialize<ActivityGetHeartbeatDetailsReply>(stream);
                Assert.NotNull(message);
                Assert.Equal(0, message.RequestId);
                Assert.Null(message.Error);
                Assert.Null(message.Details);

                // Round-trip

                message.RequestId = 555;
                Assert.Equal(555, message.RequestId);
                message.Error = new CadenceError("MyError");
                Assert.Equal("MyError", message.Error.String);
                message.Details = new byte[] { 5, 6, 7, 8, 9 };
                Assert.Equal(new byte[] { 5, 6, 7, 8, 9 }, message.Details);

                stream.SetLength(0);
                stream.Write(message.Serialize());
                stream.Seek(0, SeekOrigin.Begin);

                message = ProxyMessage.Deserialize<ActivityGetHeartbeatDetailsReply>(stream);
                Assert.NotNull(message);
                Assert.Equal(555, message.RequestId);
                Assert.Equal("MyError", message.Error.String);
                Assert.Equal(new byte[] { 5, 6, 7, 8, 9 }, message.Details);

                // Echo the message via the connection's web server and verify.

                message = EchoToClient(message);
                Assert.NotNull(message);
                Assert.Equal(555, message.RequestId);
                Assert.Equal("MyError", message.Error.String);
                Assert.Equal(new byte[] { 5, 6, 7, 8, 9 }, message.Details);

                // Echo the message via the associated [cadence-proxy] and verify.

                message = EchoToProxy(message);
                Assert.NotNull(message);
                Assert.Equal(555, message.RequestId);
                Assert.Equal("MyError", message.Error.String);
                Assert.Equal(new byte[] { 5, 6, 7, 8, 9 }, message.Details);
            }
        }

        [Fact]
        [Trait(TestCategory.CategoryTrait, TestCategory.NeonCadence)]
        public void Test_ActivityRecordHeartbeatRequest()
        {
            ActivityRecordHeartbeatRequest message;

            using (var stream = new MemoryStream())
            {
                message = new ActivityRecordHeartbeatRequest();

                Assert.Equal(InternalMessageTypes.ActivityRecordHeartbeatReply, message.ReplyType);

                // Empty message.

                stream.SetLength(0);
                stream.Write(message.Serialize());
                stream.Seek(0, SeekOrigin.Begin);

                message = ProxyMessage.Deserialize<ActivityRecordHeartbeatRequest>(stream);
                Assert.NotNull(message);
                Assert.Equal(0, message.RequestId);
                Assert.Null(message.Details);

                // Round-trip

                message.RequestId = 555;
                message.Details = new byte[] { 0, 1, 2, 3, 4 };
                Assert.Equal(555, message.RequestId);
                Assert.Equal(new byte[] { 0, 1, 2, 3, 4 }, message.Details);

                stream.SetLength(0);
                stream.Write(message.Serialize());
                stream.Seek(0, SeekOrigin.Begin);

                message = ProxyMessage.Deserialize<ActivityRecordHeartbeatRequest>(stream);
                Assert.NotNull(message);
                Assert.Equal(555, message.RequestId);
                Assert.Equal(new byte[] { 0, 1, 2, 3, 4 }, message.Details);

                // Echo the message via the connection's web server and verify.

                message = EchoToClient(message);
                Assert.NotNull(message);
                Assert.Equal(555, message.RequestId);
                Assert.Equal(new byte[] { 0, 1, 2, 3, 4 }, message.Details);

                // Echo the message via the associated [cadence-proxy] and verify.

                message = EchoToProxy(message);
                Assert.NotNull(message);
                Assert.Equal(555, message.RequestId);
                Assert.Equal(new byte[] { 0, 1, 2, 3, 4 }, message.Details);
            }
        }

        [Fact]
        [Trait(TestCategory.CategoryTrait, TestCategory.NeonCadence)]
        public void Test_ActivityRecordHeartbeatReply()
        {
            ActivityRecordHeartbeatReply message;

            using (var stream = new MemoryStream())
            {
                message = new ActivityRecordHeartbeatReply();

                // Empty message.

                stream.SetLength(0);
                stream.Write(message.Serialize());
                stream.Seek(0, SeekOrigin.Begin);

                message = ProxyMessage.Deserialize<ActivityRecordHeartbeatReply>(stream);
                Assert.NotNull(message);
                Assert.Equal(0, message.RequestId);
                Assert.Null(message.Error);

                // Round-trip

                message.RequestId = 555;
                Assert.Equal(555, message.RequestId);
                message.Error = new CadenceError("MyError");
                Assert.Equal("MyError", message.Error.String);

                stream.SetLength(0);
                stream.Write(message.Serialize());
                stream.Seek(0, SeekOrigin.Begin);

                message = ProxyMessage.Deserialize<ActivityRecordHeartbeatReply>(stream);
                Assert.NotNull(message);
                Assert.Equal(555, message.RequestId);
                Assert.Equal("MyError", message.Error.String);

                // Echo the message via the connection's web server and verify.

                message = EchoToClient(message);
                Assert.NotNull(message);
                Assert.Equal(555, message.RequestId);
                Assert.Equal("MyError", message.Error.String);

                // Echo the message via the associated [cadence-proxy] and verify.

                message = EchoToProxy(message);
                Assert.NotNull(message);
                Assert.Equal(555, message.RequestId);
                Assert.Equal("MyError", message.Error.String);
            }
        }

        [Fact]
        [Trait(TestCategory.CategoryTrait, TestCategory.NeonCadence)]
        public void Test_ActivityHasHeartbeatDetailsRequest()
        {
            ActivityHasHeartbeatDetailsRequest message;

            using (var stream = new MemoryStream())
            {
                message = new ActivityHasHeartbeatDetailsRequest();

                Assert.Equal(InternalMessageTypes.ActivityHasHeartbeatDetailsReply, message.ReplyType);

                // Empty message.

                stream.SetLength(0);
                stream.Write(message.Serialize());
                stream.Seek(0, SeekOrigin.Begin);

                message = ProxyMessage.Deserialize<ActivityHasHeartbeatDetailsRequest>(stream);
                Assert.NotNull(message);
                Assert.Equal(0, message.RequestId);

                // Round-trip

                message.RequestId = 555;
                Assert.Equal(555, message.RequestId);

                stream.SetLength(0);
                stream.Write(message.Serialize());
                stream.Seek(0, SeekOrigin.Begin);

                message = ProxyMessage.Deserialize<ActivityHasHeartbeatDetailsRequest>(stream);
                Assert.NotNull(message);
                Assert.Equal(555, message.RequestId);

                // Echo the message via the connection's web server and verify.

                message = EchoToClient(message);
                Assert.NotNull(message);
                Assert.Equal(555, message.RequestId);

                // Echo the message via the associated [cadence-proxy] and verify.

                message = EchoToProxy(message);
                Assert.NotNull(message);
                Assert.Equal(555, message.RequestId);
            }
        }

        [Fact]
        [Trait(TestCategory.CategoryTrait, TestCategory.NeonCadence)]
        public void Test_ActivityHasHeartbeatDetailsReply()
        {
            ActivityHasHeartbeatDetailsReply message;

            using (var stream = new MemoryStream())
            {
                message = new ActivityHasHeartbeatDetailsReply();

                // Empty message.

                stream.SetLength(0);
                stream.Write(message.Serialize());
                stream.Seek(0, SeekOrigin.Begin);

                message = ProxyMessage.Deserialize<ActivityHasHeartbeatDetailsReply>(stream);
                Assert.NotNull(message);
                Assert.Equal(0, message.RequestId);
                Assert.Null(message.Error);
                Assert.False(message.HasDetails);

                // Round-trip

                message.RequestId = 555;
                Assert.Equal(555, message.RequestId);
                message.Error = new CadenceError("MyError");
                Assert.Equal("MyError", message.Error.String);
                message.HasDetails = true;
                Assert.True(message.HasDetails);

                stream.SetLength(0);
                stream.Write(message.Serialize());
                stream.Seek(0, SeekOrigin.Begin);

                message = ProxyMessage.Deserialize<ActivityHasHeartbeatDetailsReply>(stream);
                Assert.NotNull(message);
                Assert.Equal(555, message.RequestId);
                Assert.Equal("MyError", message.Error.String);
                Assert.True(message.HasDetails);

                // Echo the message via the connection's web server and verify.

                message = EchoToClient(message);
                Assert.NotNull(message);
                Assert.Equal(555, message.RequestId);
                Assert.Equal("MyError", message.Error.String);
                Assert.True(message.HasDetails);

                // Echo the message via the associated [cadence-proxy] and verify.

                message = EchoToProxy(message);
                Assert.NotNull(message);
                Assert.Equal(555, message.RequestId);
                Assert.Equal("MyError", message.Error.String);
                Assert.True(message.HasDetails);
            }
        }
    }
}
