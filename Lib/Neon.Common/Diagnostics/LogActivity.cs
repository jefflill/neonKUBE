﻿//-----------------------------------------------------------------------------
// FILE:	    LogActivity.cs
// CONTRIBUTOR: Jeff Lill
// COPYRIGHT:	Copyright (c) 2005-2020 by neonFORGE, LLC.  All rights reserved.
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
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Neon.Diagnostics
{
    /// <summary>
    /// Used to help log correlate lower-level operations with a higher-level activity.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This structure is intended to help operators and developers correlate lower-level
    /// operations with higher level activities that may span multiple systems and services.
    /// </para>
    /// <para>
    /// The essential idea is to associate a globally unique ID with a high-level activity
    /// and then include this ID along with events logged by the various systems and
    /// services that participate in the activity.  Ultimately, the logged events with
    /// the activity IDs will make it to Elasticsearch or some other log database where
    /// activaty events potentially spanning many systems can be correlated and analyzed.
    /// </para>
    /// <para>
    /// An activity ID is simply a globally unique ID string.  IDs generated by this class
    /// are a currently stringified <see cref="Guid"/> but IDs may take other forms so,
    /// don't depend on this.
    /// </para>
    /// <para>
    /// In general, activity IDs are passed from service to service via the HTTP <b>X-Request-ID</b>
    /// request header (defined by <see cref="HttpHeader"/>).
    /// </para>
    /// <para>
    /// To use this type, call the static <see cref="Create(INeonLogger)"/> method to create a new activity
    /// or <see cref="From(string, INeonLogger)"/> to associate an instance with an existing activity.
    /// Then use the various logging methods to emit log events what will include the activity ID.
    /// </para>
    /// </remarks>
    public struct LogActivity
    {
        //---------------------------------------------------------------------
        // Static members

        /// <summary>
        /// Names the HTTP header used to hold the activity ID used to correlate 
        /// operation requests with a higher-level activity.
        /// </summary>
        public const string HttpHeader = "X-Request-ID";

        /// <summary>
        /// Creates a log activity with a new globally unique ID.
        /// </summary>
        /// <param name="log">The optional associated <see cref="INeonLogger"/>.</param>
        /// <returns>The created <see cref="LogActivity"/>.</returns>
        public static LogActivity Create(INeonLogger log = null)
        {
            return new LogActivity()
            {
                Id  = Guid.NewGuid().ToString("d"),
                log = log
            };
        }

        /// <summary>
        /// Creates a log activity with the ID specified.
        /// </summary>
        /// <param name="activityId">The activity ID or <c>null</c>,</param>
        /// <param name="log">The optional associated <see cref="INeonLogger"/>.</param>
        /// <returns>The created <see cref="LogActivity"/>.</returns>
        public static LogActivity From(string activityId, INeonLogger log = null)
        {
            return new LogActivity()
            {
                Id  = !string.IsNullOrWhiteSpace(activityId) ? activityId : null,
                log = log
            };
        }

        //---------------------------------------------------------------------
        // Instance members

        private INeonLogger    log;

        /// <summary>
        /// Returns the activity ID.
        /// </summary>
        public string Id { get; private set; }

        /// <summary>
        /// Logs a <b>debug</b> message.
        /// </summary>
        /// <param name="message">The object that will be serialized into the message.</param>
        public void Debug(object message)
        {
            if (log != null)
            {
                log.LogDebug(message, Id);
            }
        }

        /// <summary>
        /// Logs an <b>info</b> message.
        /// </summary>
        /// <param name="message">The object that will be serialized into the message.</param>
        public void Info(object message)
        {
            if (log != null)
            {
                log.LogInfo(message, Id);
            }
        }

        /// <summary>
        /// Logs a <b>warn</b> message.
        /// </summary>
        /// <param name="message">The object that will be serialized into the message.</param>
        public void Warn(object message)
        {
            if (log != null)
            {
                log.LogWarn(message, Id);
            }
        }

        /// <summary>
        /// Logs an <b>error</b> message.
        /// </summary>
        /// <param name="message">The object that will be serialized into the message.</param>
        public void Error(object message)
        {
            if (log != null)
            {
                log.LogError(message, Id);
            }
        }

        /// <summary>
        /// Logs a <b>critical</b> message.
        /// </summary>
        /// <param name="message">The object that will be serialized into the message.</param>
        public void Critical(object message)
        {
            if (log != null)
            {
                log.LogCritical(message, Id);
            }
        }

        /// <summary>
        /// Logs a <b>debug</b> message along with exception information.
        /// </summary>
        /// <param name="message">The object that will be serialized into the message.</param>
        /// <param name="e">The exception.</param>
        public void Debug(object message, Exception e)
        {
            if (log != null)
            {
                log.LogCritical(message, e, Id);
            }
        }

        /// <summary>
        /// Logs an <b>info</b> message along with exception information.
        /// </summary>
        /// <param name="message">The object that will be serialized into the message.</param>
        /// <param name="e">The exception.</param>
        public void Info(object message, Exception e)
        {
            if (log != null)
            {
                log.LogInfo(message, e, Id);
            }
        }

        /// <summary>
        /// Logs a <b>warn</b> message along with exception information.
        /// </summary>
        /// <param name="message">The object that will be serialized into the message.</param>
        /// <param name="e">The exception.</param>
        public void Warn(object message, Exception e)
        {
            if (log != null)
            {
                log.LogWarn(message, e, Id);
            }
        }

        /// <summary>
        /// Logs an <b>error</b> message along with exception information.
        /// </summary>
        /// <param name="message">The object that will be serialized into the message.</param>
        /// <param name="e">The exception.</param>
        public void Error(object message, Exception e)
        {
            if (log != null)
            {
                log.LogError(message, e, Id);
            }
        }

        /// <summary>
        /// Logs a <b>critical</b> message along with exception information.
        /// </summary>
        /// <param name="message">The object that will be serialized into the message.</param>
        /// <param name="e">The exception.</param>
        public void Critical(object message, Exception e)
        {
            if (log != null)
            {
                log.LogCritical(message, e, Id);
            }
        }
    }
}
