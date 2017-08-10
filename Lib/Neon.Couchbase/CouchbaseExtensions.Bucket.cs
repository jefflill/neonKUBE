﻿//-----------------------------------------------------------------------------
// FILE:	    CouchbaseExtensions.Bucket.cs
// CONTRIBUTOR: Jeff Lill
// COPYRIGHT:	Copyright (c) 2016-2017 by NeonForge, LLC.  All rights reserved.

using System;
using System.Collections.Generic;
using System.Diagnostics.Contracts;
using System.Threading;
using System.Threading.Tasks;

using Couchbase;
using Couchbase.Authentication;
using Couchbase.Configuration.Client;
using Couchbase.Core;
using Couchbase.IO;
using Couchbase.N1QL;

using Neon.Common;
using Neon.Data;
using Neon.Retry;

namespace Couchbase
{
    public static partial class CouchbaseExtensions
    {
        // Implementation Notes:
        // ---------------------
        // The VerifySuccess() methods are used to examine the server responses
        // to determine whether a transient error has occurred and throw a
        // TransientException so that an upstack IRetryPolicy can handle things.
        //
        // There are family of edge cases around document mutations that make this
        // more complicated.  Here's one scenario:
        //
        //      1. A document is inserted with an IRetryPolicy with ReplicateTo > 0.
        //
        //      2. The document makes it to one cluster node but is not replicated
        //         in time to the other nodes before the operation times out.
        //
        //      3. Operation timeouts are considered transient, so the policy
        //         retries it.
        //
        //      4. Because the document did make it to a node, the retried insert
        //         fails because the key already exists and in a simple world,
        //         this would be reported back to the application as an exception
        //         (which would be really confusing).
        //
        // Similar situations will occur with remove as well as replace/upsert with CAS.
        //
        // I don't have a lot of experience with Couchbase yet, but I'll bet that
        // this issue is limited to operations where ReplicateTo and/or PersistTo
        // are greather than zero.  I don't think there's a transparent way to
        // handle these situations, so I'm going to avoid considering operation
        // timeouts as transient when there are replication/persistence constraints.

        /// <summary>
        /// Generates a globally unique document key.
        /// </summary>
        /// <param name="bucket">The bucket.</param>
        /// <returns>A <see cref="Guid"/> formatted as a string.</returns>
        public static string GenKey(this IBucket bucket)
        {
            return Convert.ToBase64String(Guid.NewGuid().ToByteArray());
        }

        /// <summary>
        /// Determines whether a Couchbase response status code should be considered
        /// a transient error.
        /// </summary>
        /// <param name="status">The status code.</param>
        /// <param name="replicateOrPersist">Indicates whether the operation has replication or persistance constraints.</param>
        /// <returns><c>true</c> for a transient error.</returns>
        private static bool IsTransientStatus(ResponseStatus status, bool replicateOrPersist)
        {
            switch (status)
            {
                case ResponseStatus.OperationTimeout:

                    return !replicateOrPersist;

                case ResponseStatus.Busy:
                case ResponseStatus.NodeUnavailable:
                case ResponseStatus.TemporaryFailure:
                case ResponseStatus.TransportFailure:

                    return true;

                default:

                    return false;
            }
        }

        /// <summary>
        /// Throws an exception if an operation was not successful.
        /// </summary>
        /// <param name="result">The operation result.</param>
        /// <param name="replicateOrPersist">Indicates whether the operation has replication or persistance constraints.</param>
        private static void VerifySuccess(IOperationResult result, bool replicateOrPersist)
        {
            if (result.Success)
            {
                return;
            }

            if (result.ShouldRetry())
            {
                throw new TransientException(result.Message, result.Exception);
            }

            if (IsTransientStatus(result.Status, replicateOrPersist))
            {
                throw new TransientException($"Couchbase response status: {result.Status}", result.Exception);
            }

            result.EnsureSuccess();
        }

        /// <summary>
        /// Throws an exception if an operation was not successful.
        /// </summary>
        /// <typeparam name="T">The result type.</typeparam>
        /// <param name="result">The operation result.</param>
        /// <param name="replicateOrPersist">Indicates whether the operation has replication or persistance constraints.</param>
        private static void VerifySuccess<T>(IOperationResult<T> result, bool replicateOrPersist)
        {
            if (result.Success)
            {
                return;
            }

            if (result.ShouldRetry())
            {
                throw new TransientException(result.Message, result.Exception);
            }

            if (IsTransientStatus(result.Status, replicateOrPersist))
            {
                throw new TransientException($"Couchbase response status: {result.Status}", result.Exception);
            }

            result.EnsureSuccess();
        }

        /// <summary>
        /// Throws an exception if a document operation was not successful.
        /// </summary>
        /// <typeparam name="T">The document content type.</typeparam>
        /// <param name="result">The operation result.</param>
        /// <param name="replicateOrPersist">Indicates whether the operation has replication or persistance constraints.</param>
        private static void VerifySuccess<T>(IDocumentResult<T> result, bool replicateOrPersist)
        {
            if (result.Success)
            {
                return;
            }

            if (result.ShouldRetry())
            {
                throw new TransientException(result.Message, result.Exception);
            }

            if (IsTransientStatus(result.Status, replicateOrPersist))
            {
                throw new TransientException($"Couchbase response status: {result.Status}", result.Exception);
            }

            result.EnsureSuccess();
        }

        /// <summary>
        /// Throws an exception if a query operation was not successful.
        /// </summary>
        /// <typeparam name="T">The document content type.</typeparam>
        /// <param name="result">The operation result.</param>
        private static void VerifySuccess<T>(IQueryResult<T> result)
        {
            if (result.Success)
            {
                return;
            }

            if (result.ShouldRetry())
            {
                throw new TransientException(result.Message, result.Exception);
            }

            result.EnsureSuccess();
        }

        /// <summary>
        /// Appends a byte array to a key, throwing an exception on failures.
        /// </summary>
        /// <param name="bucket">The bucket.</param>
        /// <param name="key">The key.</param>
        /// <param name="value">The value.</param>
        /// <returns>The operation result.</returns>
        public static async Task<IOperationResult<byte[]>> AppendSafeAsync(this IBucket bucket, string key, byte[] value)
        {
            var result = await bucket.AppendAsync(key, value);

            VerifySuccess<byte[]>(result, replicateOrPersist: false);

            return result;
        }

        /// <summary>
        /// Appends a string to a key, throwing an exception on failures.
        /// </summary>
        /// <param name="bucket">The bucket.</param>
        /// <param name="key">The key.</param>
        /// <param name="value">The value.</param>
        /// <returns>The operation result.</returns>
        public static async Task<IOperationResult<string>> AppendAsync(this IBucket bucket, string key, string value)
        {
            var result = await bucket.AppendAsync(key, value);

            VerifySuccess<string>(result, replicateOrPersist: false);

            return result;
        }

        /// <summary>
        /// Decrements the value of a key by one.  If the key doesn't exist, it will be
        /// created and initialized to <paramref name="initial"/>.  This method will throw
        /// an exception on failures.
        /// </summary>
        /// <param name="bucket">The bucket.</param>
        /// <param name="key">The key.</param>
        /// <param name="delta">The amount to decrement by (defaults to <b>1</b>).</param>
        /// <param name="initial">The initial value to use if the key doesn't already exist (defaults to <b>1</b>).</param>
        /// <param name="expiration">The expiration TTL (defaults to none).</param>
        /// <returns>The operation result.</returns>
        public static async Task<IOperationResult<ulong>> DecrementSafeAsync(this IBucket bucket, string key, ulong delta = 1, ulong initial = 1, TimeSpan expiration = default(TimeSpan))
        {
            IOperationResult<ulong> result;

            if (expiration > TimeSpan.Zero)
            {
                result = await bucket.DecrementAsync(key, delta, initial, expiration);
            }
            else
            {
                result = await bucket.DecrementAsync(key, delta, initial);
            }

            VerifySuccess<ulong>(result, replicateOrPersist: false);

            return result;
        }

        /// <summary>
        /// Checks for the existance of a key, throwing an exception on failures.
        /// </summary>
        /// <param name="bucket">The bucket.</param>
        /// <param name="key">The key.</param>
        /// <returns><c>true</c> if the key exists.</returns>
        public static async Task<bool> ExistsSafeAsync(IBucket bucket, string key)
        {
            // This doesn't actually return a testable result but we'll still
            // implement the "safe" version to be consistent.

            return await bucket.ExistsAsync(key);
        }

        /// <summary>
        /// Attempts to retrieve a key value, returning <c>null</c> if it doesn't exist rather
        /// than throwing an exception.
        /// </summary>
        /// <typeparam name="T">The value type.</typeparam>
        /// <param name="bucket">The bucket.</param>
        /// <param name="key">The key.</param>
        /// <returns>The value or <c>null</c>.</returns>
        public static async Task<T> FindSafeAsync<T>(this IBucket bucket, string key)
            where T : class
        {
            var result = await bucket.GetAsync<T>(key);

            if (result.Exception is DocumentDoesNotExistException)
            {
                return null;
            }

            VerifySuccess<T>(result, replicateOrPersist: false);

            return result.Value;
        }

        /// <summary>
        /// Attemps to retrieve a document, returning <c>null</c> if it doesn't exist rather
        /// than throwing an exception.
        /// </summary>
        /// <typeparam name="T">The value type.</typeparam>
        /// <param name="bucket">The bucket.</param>
        /// <param name="key">The key.</param>
        /// <returns>The value or <c>null</c>.</returns>
        public static async Task<IDocument<T>> FindDocumentSafeAsync<T>(this IBucket bucket, string key)
            where T : class
        {
            var result = await bucket.GetDocumentAsync<T>(key);

            if (result.Exception is DocumentDoesNotExistException)
            {
                return null;
            }

            VerifySuccess<T>(result, replicateOrPersist: false);

            return result.Document;
        }

        /// <summary>
        /// Gets a key and locks it for a specified time period.
        /// </summary>
        /// <typeparam name="T">The document content type.</typeparam>
        /// <param name="bucket">The bucket.</param>
        /// <param name="key">The key.</param>
        /// <param name="expiration">The interval after which the document will be locked.  This defaults to 15 seconds and the maximum supported by the server is 30 seconds.</param>
        /// <returns>The operation result.</returns>
        public static async Task<IOperationResult<T>> GetAndLockSafeAsync<T>(this IBucket bucket, string key, TimeSpan expiration = default(TimeSpan))
        {
            if (expiration <= TimeSpan.Zero)
            {
                expiration = TimeSpan.FromSeconds(15);
            }

            var result = await bucket.GetAndLockAsync<T>(key, expiration);

            VerifySuccess<T>(result, replicateOrPersist: false);

            return result;
        }

        /// <summary>
        /// Gets a key and updates its expiry with a new value.
        /// </summary>
        /// <typeparam name="T">The document content type.</typeparam>
        /// <param name="bucket">The bucket.</param>
        /// <param name="key">The key.</param>
        /// <param name="expiration">The optional new expiry timespan.</param>
        /// <returns>The value.</returns>
        public static async Task<T> GetAndTouchSafeAsync<T>(this IBucket bucket, string key, TimeSpan expiration)
        {
            var result = await bucket.GetAndTouchAsync<T>(key, expiration);

            VerifySuccess<T>(result, replicateOrPersist: false);

            return result.Value;
        }

        /// <summary>
        /// Gets a document and updates its expiry with a new value.
        /// </summary>
        /// <typeparam name="T">The document content type.</typeparam>
        /// <param name="bucket">The bucket.</param>
        /// <param name="key">The key.</param>
        /// <param name="expiration">The optional new expiry timespan.</param>
        /// <returns>The document.</returns>
        public static async Task<Document<T>> GetAndTouchDocumentSafeAsync<T>(this IBucket bucket, string key, TimeSpan expiration)
        {
            var result = await bucket.GetAndTouchDocumentAsync<T>(key, expiration);

            VerifySuccess<T>(result, replicateOrPersist: false);

            return result.Document;
        }

        /// <summary>
        /// Gets a key value from the database, throwing an exception if the key does not exist
        /// or there was another error.  
        /// </summary>
        /// <typeparam name="T">The value type.</typeparam>
        /// <param name="bucket">The bucket.</param>
        /// <param name="key">The key.</param>
        /// <returns>The value.</returns>
        public static async Task<T> GetSafeAsync<T>(this IBucket bucket, string key)
        {
            var result = await bucket.GetAsync<T>(key);

            VerifySuccess<T>(result, replicateOrPersist: false);

            return result.Value;
        }

        /// <summary>
        /// Gets a document, throwing an exception if the document does not exist or there
        /// was another error.
        /// </summary>
        /// <typeparam name="T">The document content type.</typeparam>
        /// <param name="bucket">The bucket.</param>
        /// <param name="keys">The key.</param>
        /// <param name="expiration">The optional new expiry timespan.</param>
        /// <returns>The document.</returns>
        public static async Task<Document<T>> GetDocumentSafeAsync<T>(this IBucket bucket, string keys, TimeSpan expiration)
        {
            var result = await bucket.GetDocumentAsync<T>(keys);

            VerifySuccess<T>(result, replicateOrPersist: false);

            return result.Document;
        }

        /// <summary>
        /// Gets a set of documents, throwing an exception if any document does not exist or there
        /// was another error.
        /// </summary>
        /// <typeparam name="T">The document content type.</typeparam>
        /// <param name="bucket">The bucket.</param>
        /// <param name="keys">The keys.</param>
        /// <returns>The documents.</returns>
        public static async Task<IDocument<T>[]> GetDocumentSafeAsync<T>(this IBucket bucket, IEnumerable<string> keys)
        {
            var results = await bucket.GetDocumentsAsync<T>(keys);

            foreach (var result in results)
            {
                VerifySuccess<T>(result, replicateOrPersist: false);
            }

            var documents = new IDocument<T>[results.Length];

            for (int i = 0; i < results.Length; i++)
            {
                documents[i] = results[i].Document;
            }

            return documents;
        }

        /// <summary>
        /// Gets a key value from a Couchbase replica node, throwing an exception if the key does
        /// not exist or there was another error.
        /// </summary>
        /// <typeparam name="T">The value type.</typeparam>
        /// <param name="bucket">The bucket.</param>
        /// <param name="key">The key.</param>
        /// <returns>The value.</returns>
        public static async Task<T> GetFromReplicaSafeAsync<T>(this IBucket bucket, string key)
        {
            var result = await bucket.GetFromReplicaAsync<T>(key);

            VerifySuccess<T>(result, replicateOrPersist: false);

            return result.Value;
        }

        /// <summary>
        /// Increments the value of a key by one.  If the key doesn't exist, it will be
        /// created and initialized to <paramref name="initial"/>.
        /// </summary>
        /// <param name="bucket">The bucket.</param>
        /// <param name="key">The key.</param>
        /// <param name="delta">The amount to increment by (defaults to <b>1</b>).</param>
        /// <param name="initial">The initial value to use if the key doesn't already exist (defaults to <b>1</b>).</param>
        /// <param name="expiration">The expiration TTL (defaults to none).</param>
        /// <returns>The operation result.</returns>
        public static async Task<IOperationResult<ulong>> IncrementSafeAsync(this IBucket bucket, string key, ulong delta = 1, ulong initial = 1, TimeSpan expiration = default(TimeSpan))
        {
            IOperationResult<ulong> result;

            if (expiration > TimeSpan.Zero)
            {
                result = await bucket.IncrementAsync(key, delta, initial, expiration);
            }
            else
            {
                result = await bucket.IncrementAsync(key, delta, initial);
            }

            VerifySuccess<ulong>(result, replicateOrPersist: false);

            return result;
        }

        /// <summary>
        /// Inserts a key, throwing an exception if the key already exists or there
        /// was another error.
        /// </summary>
        /// <typeparam name="T">The value type.</typeparam>
        /// <param name="bucket">The bucket.</param>
        /// <param name="key">The key.</param>
        /// <param name="value">The value.</param>
        /// <param name="replicateTo">Optional replication factor (defaults to <see cref="ReplicateTo.Zero"/>).</param>
        /// <param name="persistTo">Optional persistance factor (defaults to <see cref="PersistTo.Zero"/>).</param>
        /// <returns>The tracking <see cref="Task"/>.</returns>
        public static async Task InsertSafeAsync<T>(this IBucket bucket, string key, T value, ReplicateTo replicateTo = ReplicateTo.Zero, PersistTo persistTo = PersistTo.Zero)
        {
            var result = await bucket.InsertAsync<T>(key, value, replicateTo, persistTo);

            VerifySuccess<T>(result, replicateOrPersist: replicateTo != ReplicateTo.Zero || persistTo != PersistTo.Zero);
        }

        /// <summary>
        /// Inserts a key with an expiration TTL, throwing an exception if the key already exists or there
        /// was another error.  Note that 30 seconds is the maximum expiration TTL supported by the
        /// server.
        /// </summary>
        /// <typeparam name="T">The value type.</typeparam>
        /// <param name="bucket">The bucket.</param>
        /// <param name="key">The key.</param>
        /// <param name="value">The value.</param>
        /// <param name="expiration">The expiration TTL.</param>
        /// <param name="replicateTo">Optional replication factor (defaults to <see cref="ReplicateTo.Zero"/>).</param>
        /// <param name="persistTo">Optional persistance factor (defaults to <see cref="PersistTo.Zero"/>).</param>
        /// <returns>The tracking <see cref="Task"/>.</returns>
        public static async Task InsertSafeAsync<T>(this IBucket bucket, string key, T value, TimeSpan expiration, ReplicateTo replicateTo = ReplicateTo.Zero, PersistTo persistTo = PersistTo.Zero)
        {
            var result = await bucket.InsertAsync<T>(key, value, expiration, replicateTo, persistTo);

            VerifySuccess<T>(result, replicateOrPersist: replicateTo != ReplicateTo.Zero || persistTo != PersistTo.Zero);
        }

        /// <summary>
        /// Inserts a document, throwing an exception if the document already exists or there
        /// was another error.
        /// </summary>
        /// <typeparam name="T">The document content type.</typeparam>
        /// <param name="bucket">The bucket.</param>
        /// <param name="document">The document.</param>
        /// <param name="replicateTo">Optional replication factor (defaults to <see cref="ReplicateTo.Zero"/>).</param>
        /// <param name="persistTo">Optional persistance factor (defaults to <see cref="PersistTo.Zero"/>).</param>
        /// <returns>The tracking <see cref="Task"/>.</returns>
        public static async Task InsertSafeAsync<T>(this IBucket bucket, IDocument<T> document, ReplicateTo replicateTo = ReplicateTo.Zero, PersistTo persistTo = PersistTo.Zero)
        {
            var result = await bucket.InsertAsync<T>(document, replicateTo, persistTo);

            VerifySuccess<T>(result, replicateOrPersist: replicateTo != ReplicateTo.Zero || persistTo != PersistTo.Zero);
        }

        /// <summary>
        /// Inserts multiple documents, throwing an exception if any of the documents already exists or there
        /// was another error.
        /// </summary>
        /// <typeparam name="T">The document content type.</typeparam>
        /// <param name="bucket">The bucket.</param>
        /// <param name="documents">The documents.</param>
        /// <param name="replicateTo">Optional replication factor (defaults to <see cref="ReplicateTo.Zero"/>).</param>
        /// <param name="persistTo">Optional persistance factor (defaults to <see cref="PersistTo.Zero"/>).</param>
        /// <returns>The tracking <see cref="Task"/>.</returns>
        public static async Task InsertSafeAsync<T>(this IBucket bucket, List<IDocument<T>> documents, ReplicateTo replicateTo = ReplicateTo.Zero, PersistTo persistTo = PersistTo.Zero)
        {
            var results = await bucket.InsertAsync<T>(documents, replicateTo, persistTo);

            foreach (var result in results)
            {
                VerifySuccess<T>(result, replicateOrPersist: replicateTo != ReplicateTo.Zero || persistTo != PersistTo.Zero);
            }
        }

        /// <summary>
        /// Inserts an entity, throwing an exception if the entity already exists or there
        /// was another error.
        /// </summary>
        /// <typeparam name="TEntity">The entity type.</typeparam>
        /// <param name="bucket">The bucket.</param>
        /// <param name="entity">The entity.</param>
        /// <param name="replicateTo">Optional replication factor (defaults to <see cref="ReplicateTo.Zero"/>).</param>
        /// <param name="persistTo">Optional persistance factor (defaults to <see cref="PersistTo.Zero"/>).</param>
        /// <returns>The tracking <see cref="Task"/>.</returns>
        public static async Task InsertSafeAsync<TEntity>(this IBucket bucket, TEntity entity, ReplicateTo replicateTo = ReplicateTo.Zero, PersistTo persistTo = PersistTo.Zero)
            where TEntity: class, IEntity
        {
            Covenant.Requires<ArgumentNullException>(entity != null);

            var result = await bucket.InsertAsync<TEntity>(entity.GetKey(), entity, replicateTo, persistTo);

            VerifySuccess<TEntity>(result, replicateOrPersist: replicateTo != ReplicateTo.Zero || persistTo != PersistTo.Zero);
        }

        /// <summary>
        /// Inserts an entity with an expiration TTL, throwing an exception if the key already exists or there
        /// was another error.  Note that 30 seconds is the maximum expiration TTL supported by the
        /// server.
        /// </summary>
        /// <typeparam name="TEntity">The entity type.</typeparam>
        /// <param name="bucket">The bucket.</param>
        /// <param name="entity">The entity.</param>
        /// <param name="expiration">The expiration TTL.</param>
        /// <param name="replicateTo">Optional replication factor (defaults to <see cref="ReplicateTo.Zero"/>).</param>
        /// <param name="persistTo">Optional persistance factor (defaults to <see cref="PersistTo.Zero"/>).</param>
        /// <returns>The tracking <see cref="Task"/>.</returns>
        public static async Task InsertSafeAsync<TEntity>(this IBucket bucket, TEntity entity, TimeSpan expiration, ReplicateTo replicateTo = ReplicateTo.Zero, PersistTo persistTo = PersistTo.Zero)
            where TEntity: class, IEntity
        {
            Covenant.Requires<ArgumentNullException>(entity != null);

            var result = await bucket.InsertAsync<TEntity>(entity.GetKey(), entity, expiration, replicateTo, persistTo);

            VerifySuccess<TEntity>(result, replicateOrPersist: replicateTo != ReplicateTo.Zero || persistTo != PersistTo.Zero);
        }

        /// <summary>
        /// Executes a query request, throwing an exception if there were any errors.
        /// </summary>
        /// <typeparam name="T">The result type.</typeparam>
        /// <param name="bucket">The bucket.</param>
        /// <param name="queryRequest">The query request.</param>
        /// <param name="cancellationToken">The optional cancellation token.</param>
        /// <returns>The list of results.</returns>
        public static async Task<List<T>> QuerySafeAsync<T>(this IBucket bucket, IQueryRequest queryRequest, CancellationToken cancellationToken = default(CancellationToken))
        {
            var result = await bucket.QueryAsync<T>(queryRequest, cancellationToken);

            VerifySuccess<T>(result);

            return result.Rows;
        }

        /// <summary>
        /// Executes a N1QL string query, throwing an exception if there were any errors.
        /// </summary>
        /// <typeparam name="T">The result type.</typeparam>
        /// <param name="bucket">The bucket.</param>
        /// <param name="query">The N1QL query.</param>
        /// <param name="cancellationToken">The optional cancellation token.</param>
        /// <returns>The list of results.</returns>
        public static async Task<List<T>> QuerySafeAsync<T>(this IBucket bucket, string query, CancellationToken cancellationToken = default(CancellationToken))
        {
            return await QuerySafeAsync<T>(bucket, new QueryRequest(query), cancellationToken);
        }

        /// <summary>
        /// Removes a document throwning an exception if there were any errors.
        /// </summary>
        /// <param name="bucket">The bucket.</param>
        /// <param name="document">The document to be deleted.</param>
        /// <param name="replicateTo">Optional replication factor (defaults to <see cref="ReplicateTo.Zero"/>).</param>
        /// <param name="persistTo">Optional persistance factor (defaults to <see cref="PersistTo.Zero"/>).</param>
        /// <returns>The tracking <see cref="Task"/>.</returns>
        public static async Task RemoveSafeAsync(this IBucket bucket, IDocument<Task> document, ReplicateTo replicateTo = ReplicateTo.Zero, PersistTo persistTo = PersistTo.Zero)
        {
            var result = await bucket.RemoveAsync(document, replicateTo, persistTo);

            VerifySuccess(result, replicateOrPersist: replicateTo != ReplicateTo.Zero || persistTo != PersistTo.Zero);
        }

        /// <summary>
        /// Removes multiple documents, throwing an exception if there were any errors.
        /// </summary>
        /// <param name="bucket">The bucket.</param>
        /// <param name="documents">The document to be deleted.</param>
        /// <param name="replicateTo">Optional replication factor (defaults to <see cref="ReplicateTo.Zero"/>).</param>
        /// <param name="persistTo">Optional persistance factor (defaults to <see cref="PersistTo.Zero"/>).</param>
        /// <returns>The tracking <see cref="Task"/>.</returns>
        public static async Task RemoveSafeAsync(this IBucket bucket, List<IDocument<Task>> documents, ReplicateTo replicateTo = ReplicateTo.Zero, PersistTo persistTo = PersistTo.Zero)
        {
            var results = await bucket.RemoveAsync(documents, replicateTo, persistTo);

            foreach (var result in results)
            {
                VerifySuccess(result, replicateOrPersist: replicateTo != ReplicateTo.Zero || persistTo != PersistTo.Zero);
            }
        }

        /// <summary>
        /// Removes a key, throwning an exception if there were any errors.
        /// </summary>
        /// <param name="bucket">The bucket.</param>
        /// <param name="key">The key to be deleted.</param>
        /// <param name="replicateTo">Optional replication factor (defaults to <see cref="ReplicateTo.Zero"/>).</param>
        /// <param name="persistTo">Optional persistance factor (defaults to <see cref="PersistTo.Zero"/>).</param>
        /// <returns>The tracking <see cref="Task"/>.</returns>
        public static async Task RemoveSafeAsync(this IBucket bucket, string key, ReplicateTo replicateTo = ReplicateTo.Zero, PersistTo persistTo = PersistTo.Zero)
        {
            var result = await bucket.RemoveAsync(key, replicateTo, persistTo);

            VerifySuccess(result, replicateOrPersist: replicateTo != ReplicateTo.Zero || persistTo != PersistTo.Zero);
        }

        /// <summary>
        /// Removes an entity, throwning an exception if there were any errors.
        /// </summary>
        /// <param name="bucket">The bucket.</param>
        /// <param name="entity">The entity to be deleted.</param>
        /// <param name="replicateTo">Optional replication factor (defaults to <see cref="ReplicateTo.Zero"/>).</param>
        /// <param name="persistTo">Optional persistance factor (defaults to <see cref="PersistTo.Zero"/>).</param>
        /// <returns>The tracking <see cref="Task"/>.</returns>
        public static async Task RemoveSafeAsync(this IBucket bucket, IEntity entity, ReplicateTo replicateTo = ReplicateTo.Zero, PersistTo persistTo = PersistTo.Zero)
        {
            Covenant.Requires<ArgumentNullException>(entity != null);

            var result = await bucket.RemoveAsync(entity.GetKey(), replicateTo, persistTo);

            VerifySuccess(result, replicateOrPersist: replicateTo != ReplicateTo.Zero || persistTo != PersistTo.Zero);
        }

        /// <summary>
        /// Replaces an existing document, throwing an exception if there were any errors.
        /// </summary>
        /// <typeparam name="T">The document content type.</typeparam>
        /// <param name="bucket">The bucket.</param>
        /// <param name="document">The replacement document.</param>
        /// <param name="replicateTo">Optional replication factor (defaults to <see cref="ReplicateTo.Zero"/>).</param>
        /// <param name="persistTo">Optional persistance factor (defaults to <see cref="PersistTo.Zero"/>).</param>
        /// <returns>The tracking <see cref="Task"/>.</returns>
        public static async Task ReplaceSafeAsync<T>(this IBucket bucket, IDocument<T> document, ReplicateTo replicateTo = ReplicateTo.Zero, PersistTo persistTo = PersistTo.Zero)
        {
            var result = await bucket.ReplaceAsync<T>(document, replicateTo, persistTo);

            VerifySuccess(result, replicateOrPersist: replicateTo != ReplicateTo.Zero || persistTo != PersistTo.Zero);
        }

        /// <summary>
        /// Replaces multiple documents, throwing an exception if there were any errors.
        /// </summary>
        /// <typeparam name="T">The document content type.</typeparam>
        /// <param name="bucket">The bucket.</param>
        /// <param name="documents">The replacement documents.</param>
        /// <param name="replicateTo">Optional replication factor (defaults to <see cref="ReplicateTo.Zero"/>).</param>
        /// <param name="persistTo">Optional persistance factor (defaults to <see cref="PersistTo.Zero"/>).</param>
        /// <returns>The tracking <see cref="Task"/>.</returns>
        public static async Task ReplaceSafeAsync<T>(this IBucket bucket, List<IDocument<T>> documents, ReplicateTo replicateTo = ReplicateTo.Zero, PersistTo persistTo = PersistTo.Zero)
        {
            var results            = await bucket.ReplaceAsync<T>(documents, replicateTo, persistTo);
            var replicateOrPersist = replicateTo != ReplicateTo.Zero || persistTo != PersistTo.Zero;

            foreach (var result in results)
            {
                VerifySuccess(result, replicateOrPersist);
            }
        }

        /// <summary>
        /// Replaces a key value, throwing an exception if there were any errors.
        /// </summary>
        /// <typeparam name="T">The value type.</typeparam>
        /// <param name="bucket">The bucket.</param>
        /// <param name="key">The key.</param>
        /// <param name="value">The replacement value.</param>
        /// <param name="replicateTo">Optional replication factor (defaults to <see cref="ReplicateTo.Zero"/>).</param>
        /// <param name="persistTo">Optional persistance factor (defaults to <see cref="PersistTo.Zero"/>).</param>
        /// <returns>The tracking <see cref="Task"/>.</returns>
        public static async Task ReplaceSafeAsync<T>(this IBucket bucket, string key, T value, ReplicateTo replicateTo = ReplicateTo.Zero, PersistTo persistTo = PersistTo.Zero)
        {
            var result = await bucket.ReplaceAsync<T>(key, value, replicateTo, persistTo);

            VerifySuccess(result, replicateOrPersist: replicateTo != ReplicateTo.Zero || persistTo != PersistTo.Zero);
        }

        /// <summary>
        /// Replaces a key value, optionally specifying a CAS value and throwing an exception
        /// if there were any errors.
        /// </summary>
        /// <typeparam name="T">The value type.</typeparam>
        /// <param name="bucket">The bucket.</param>
        /// <param name="key">The key.</param>
        /// <param name="value">The replacement value.</param>
        /// <param name="cas">The optional CAS value.</param>
        /// <param name="expiration">Optional expiration TTL.</param>
        /// <param name="replicateTo">Optional replication factor (defaults to <see cref="ReplicateTo.Zero"/>).</param>
        /// <param name="persistTo">Optional persistance factor (defaults to <see cref="PersistTo.Zero"/>).</param>
        /// <returns>The tracking <see cref="Task"/>.</returns>
        public static async Task ReplaceSafeAsync<T>(this IBucket bucket, string key, T value, ulong? cas = null, TimeSpan? expiration = null, ReplicateTo replicateTo = ReplicateTo.Zero, PersistTo persistTo = PersistTo.Zero)
        {
            IOperationResult<T> result;
            var                 replicateOrPersist = replicateTo != ReplicateTo.Zero || persistTo != PersistTo.Zero;

            if (cas.HasValue && expiration.HasValue)
            {
                result = await bucket.ReplaceAsync<T>(key, value, cas.Value, expiration.Value, replicateTo, persistTo);
            }
            else if (cas.HasValue)
            {
                result = await bucket.ReplaceAsync<T>(key, value, cas.Value, replicateTo, persistTo);
            }
            else if (expiration.HasValue)
            {
                // $todo(jeff.lill):
                //
                // There doesn't appear to be a way to do this in one API call because
                // there isn't an override that doesn't include a CAS parameter.  Research
                // whether it's possible to pass something like 0 or -1 as the CAS to
                // disable CAS behavior.

                var result1 = await bucket.ReplaceAsync<T>(key, value, replicateTo, persistTo);

                VerifySuccess<T>(result1, replicateOrPersist);

                var result2 = await bucket.TouchAsync(key, expiration.Value);

                VerifySuccess(result2, replicateOrPersist);
                return;
            }
            else
            {
                result = await bucket.ReplaceAsync<T>(key, value, replicateTo, persistTo);
            }

            VerifySuccess<T>(result, replicateOrPersist);
        }

        /// <summary>
        /// Replaces an entity, throwing an exception if there were any errors.
        /// </summary>
        /// <typeparam name="TEntity">The entity type.</typeparam>
        /// <param name="bucket">The bucket.</param>
        /// <param name="entity">The replacement entity.</param>
        /// <param name="replicateTo">Optional replication factor (defaults to <see cref="ReplicateTo.Zero"/>).</param>
        /// <param name="persistTo">Optional persistance factor (defaults to <see cref="PersistTo.Zero"/>).</param>
        /// <returns>The tracking <see cref="Task"/>.</returns>
        public static async Task ReplaceSafeAsync<TEntity>(this IBucket bucket, TEntity entity, ReplicateTo replicateTo = ReplicateTo.Zero, PersistTo persistTo = PersistTo.Zero)
            where TEntity : class, IEntity
        {
            Covenant.Requires<ArgumentNullException>(entity != null);

            var result = await bucket.ReplaceAsync<TEntity>(entity.GetKey(), entity, replicateTo, persistTo);

            VerifySuccess(result, replicateOrPersist: replicateTo != ReplicateTo.Zero || persistTo != PersistTo.Zero);
        }

        /// <summary>
        /// Replaces an entity, optionally specifying a CAS value and throwing an exception
        /// if there were any errors.
        /// </summary>
        /// <typeparam name="TEntity">The entity type.</typeparam>
        /// <param name="bucket">The bucket.</param>
        /// <param name="entity">The replacement entity.</param>
        /// <param name="cas">The optional CAS value.</param>
        /// <param name="expiration">Optional expiration TTL.</param>
        /// <param name="replicateTo">Optional replication factor (defaults to <see cref="ReplicateTo.Zero"/>).</param>
        /// <param name="persistTo">Optional persistance factor (defaults to <see cref="PersistTo.Zero"/>).</param>
        /// <returns>The tracking <see cref="Task"/>.</returns>
        public static async Task ReplaceSafeAsync<TEntity>(this IBucket bucket, TEntity entity, ulong? cas = null, TimeSpan? expiration = null, ReplicateTo replicateTo = ReplicateTo.Zero, PersistTo persistTo = PersistTo.Zero)
            where TEntity : class, IEntity
        {
            Covenant.Requires<ArgumentNullException>(entity != null);

            IOperationResult<TEntity> result;

            var replicateOrPersist = replicateTo != ReplicateTo.Zero || persistTo != PersistTo.Zero;
            var key                = entity.GetKey();

            if (cas.HasValue && expiration.HasValue)
            {
                result = await bucket.ReplaceAsync<TEntity>(key, entity, cas.Value, expiration.Value, replicateTo, persistTo);
            }
            else if (cas.HasValue)
            {
                result = await bucket.ReplaceAsync<TEntity>(key, entity, cas.Value, replicateTo, persistTo);
            }
            else if (expiration.HasValue)
            {
                // $todo(jeff.lill):
                //
                // There doesn't appear to be a way to do this in one API call because
                // there isn't an override that doesn't include a CAS parameter.  Research
                // whether it's possible to pass something like 0 or -1 as the CAS to
                // disable CAS behavior.

                var result1 = await bucket.ReplaceAsync<TEntity>(key, entity, replicateTo, persistTo);

                VerifySuccess<TEntity>(result1, replicateOrPersist);

                var result2 = await bucket.TouchAsync(key, expiration.Value);

                VerifySuccess(result2, replicateOrPersist);
                return;
            }
            else
            {
                result = await bucket.ReplaceAsync<TEntity>(key, entity, replicateTo, persistTo);
            }

            VerifySuccess<TEntity>(result, replicateOrPersist);
        }

        /// <summary>
        /// Touches a key and updates its expiry, throwing an exception if there were errors.
        /// </summary>
        /// <param name="bucket">The bucket.</param>
        /// <param name="key">The key.</param>
        /// <param name="expiration"></param>
        /// <returns>The tracking <see cref="Task"/>.</returns>
        public static async Task TouchSafeAsync(this IBucket bucket, string key, TimeSpan expiration)
        {
            var result = await bucket.TouchAsync(key, expiration);

            VerifySuccess(result, replicateOrPersist: false);
        }

        /// <summary>
        /// Touches an entity and updates its expiry, throwing an exception if there were errors.
        /// </summary>
        /// <param name="bucket">The bucket.</param>
        /// <param name="entity">The entity.</param>
        /// <param name="expiration"></param>
        /// <returns>The tracking <see cref="Task"/>.</returns>
        public static async Task TouchSafeAsync(this IBucket bucket, IEntity entity, TimeSpan expiration)
        {
            Covenant.Requires<ArgumentNullException>(entity != null);

            var result = await bucket.TouchAsync(entity.GetKey(), expiration);

            VerifySuccess(result, replicateOrPersist: false);
        }

        /// <summary>
        /// Unlocks a key, throwing an exception if there were errors.
        /// </summary>
        /// <param name="bucket">The bucket.</param>
        /// <param name="key">The key.</param>
        /// <param name="cas">The CAS value.</param>
        /// <returns>The tracking <see cref="Task"/>.</returns>
        public static async Task UnlockSafeAsync(this IBucket bucket, string key, ulong cas)
        {
            var result = await bucket.UnlockAsync(key, cas);

            VerifySuccess(result, replicateOrPersist: false);
        }

        /// <summary>
        /// Unlocks an entity, throwing an exception if there were errors.
        /// </summary>
        /// <param name="bucket">The bucket.</param>
        /// <param name="entity">The entity.</param>
        /// <param name="cas">The CAS value.</param>
        /// <returns>The tracking <see cref="Task"/>.</returns>
        public static async Task UnlockSafeAsync(this IBucket bucket, IEntity entity, ulong cas)
        {
            Covenant.Requires<ArgumentNullException>(entity != null);

            var result = await bucket.UnlockAsync(entity.GetKey(), cas);

            VerifySuccess(result, replicateOrPersist: false);
        }

        /// <summary>
        /// Inserts or updates a document, throwing an exception if there are errors.
        /// </summary>
        /// <typeparam name="T">The document content type.</typeparam>
        /// <param name="bucket">The bucket.</param>
        /// <param name="document">The document.</param>
        /// <param name="replicateTo">Optional replication factor (defaults to <see cref="ReplicateTo.Zero"/>).</param>
        /// <param name="persistTo">Optional persistance factor (defaults to <see cref="PersistTo.Zero"/>).</param>
        /// <returns>The tracking <see cref="Task"/>.</returns>
        public static async Task UpsertSafeAsync<T>(this IBucket bucket, IDocument<T> document, ReplicateTo replicateTo = ReplicateTo.Zero, PersistTo persistTo = PersistTo.Zero)
        {
            var result = await bucket.UpsertAsync<T>(document, replicateTo, persistTo);

            VerifySuccess(result, replicateOrPersist: replicateTo != ReplicateTo.Zero || persistTo != PersistTo.Zero);
        }

        /// <summary>
        /// Inserts or updates a key, throwing an exception if there are errors.
        /// </summary>
        /// <typeparam name="T">The value type.</typeparam>
        /// <param name="bucket">The bucket.</param>
        /// <param name="key">The key.</param>
        /// <param name="value">The value.</param>
        /// <param name="replicateTo">Optional replication factor (defaults to <see cref="ReplicateTo.Zero"/>).</param>
        /// <param name="persistTo">Optional persistance factor (defaults to <see cref="PersistTo.Zero"/>).</param>
        /// <returns>The tracking <see cref="Task"/>.</returns>
        public static async Task UpsertSafeAsync<T>(this IBucket bucket, string key, T value, ReplicateTo replicateTo = ReplicateTo.Zero, PersistTo persistTo = PersistTo.Zero)
        {
            var result = await bucket.UpsertAsync<T>(key, value, replicateTo, persistTo);

            VerifySuccess(result, replicateOrPersist: replicateTo != ReplicateTo.Zero || persistTo != PersistTo.Zero);
        }

        /// <summary>
        /// Inserts or updates an entity, throwing an exception if there are errors.
        /// </summary>
        /// <typeparam name="TEntity">The entity type.</typeparam>
        /// <param name="bucket">The bucket.</param>
        /// <param name="entity">The entity.</param>
        /// <param name="replicateTo">Optional replication factor (defaults to <see cref="ReplicateTo.Zero"/>).</param>
        /// <param name="persistTo">Optional persistance factor (defaults to <see cref="PersistTo.Zero"/>).</param>
        /// <returns>The tracking <see cref="Task"/>.</returns>
        public static async Task UpsertSafeAsync<TEntity>(this IBucket bucket, TEntity entity, ReplicateTo replicateTo = ReplicateTo.Zero, PersistTo persistTo = PersistTo.Zero)
            where TEntity: class, IEntity
        {
            Covenant.Requires<ArgumentNullException>(entity != null);

            var result = await bucket.UpsertAsync<TEntity>(entity.GetKey(), entity, replicateTo, persistTo);

            VerifySuccess(result, replicateOrPersist: replicateTo != ReplicateTo.Zero || persistTo != PersistTo.Zero);
        }

        /// <summary>
        /// Inserts or updates a key using a CAS, throwing an exception if there are errors.
        /// </summary>
        /// <typeparam name="T">The value type.</typeparam>
        /// <param name="bucket">The bucket.</param>
        /// <param name="key">The key.</param>
        /// <param name="value">The value.</param>
        /// <param name="cas">The CAS.</param>
        /// <param name="replicateTo">Optional replication factor (defaults to <see cref="ReplicateTo.Zero"/>).</param>
        /// <param name="persistTo">Optional persistance factor (defaults to <see cref="PersistTo.Zero"/>).</param>
        /// <returns>The tracking <see cref="Task"/>.</returns>
        public static async Task UpsertSafeAsync<T>(this IBucket bucket, string key, T value, ulong cas, ReplicateTo replicateTo = ReplicateTo.Zero, PersistTo persistTo = PersistTo.Zero)
        {
            // $todo(jeff.lill):
            //
            // Not so sure about setting [uint.MaxValue] as the expiration here.

            var result = await bucket.UpsertAsync<T>(key, value, cas, uint.MaxValue, replicateTo, persistTo);

            VerifySuccess(result, replicateOrPersist: replicateTo != ReplicateTo.Zero || persistTo != PersistTo.Zero);
        }

        /// <summary>
        /// Inserts or updates an entity using a CAS, throwing an exception if there are errors.
        /// </summary>
        /// <typeparam name="TEntity">The entity type.</typeparam>
        /// <param name="bucket">The bucket.</param>
        /// <param name="entity">The entity.</param>
        /// <param name="cas">The CAS.</param>
        /// <param name="replicateTo">Optional replication factor (defaults to <see cref="ReplicateTo.Zero"/>).</param>
        /// <param name="persistTo">Optional persistance factor (defaults to <see cref="PersistTo.Zero"/>).</param>
        /// <returns>The tracking <see cref="Task"/>.</returns>
        public static async Task UpsertSafeAsync<TEntity>(this IBucket bucket, TEntity entity, ulong cas, ReplicateTo replicateTo = ReplicateTo.Zero, PersistTo persistTo = PersistTo.Zero)
            where TEntity : class, IEntity
        {
            // $todo(jeff.lill):
            //
            // Not so sure about setting [uint.MaxValue] as the expiration here.

            var result = await bucket.UpsertAsync<TEntity>(entity.GetKey(), entity, cas, uint.MaxValue, replicateTo, persistTo);

            VerifySuccess(result, replicateOrPersist: replicateTo != ReplicateTo.Zero || persistTo != PersistTo.Zero);
        }

        /// <summary>
        /// Inserts or updates a key setting an expiration, throwing an exception if there are errors.
        /// </summary>
        /// <typeparam name="T">The value type.</typeparam>
        /// <param name="bucket">The bucket.</param>
        /// <param name="key">The key.</param>
        /// <param name="value">The value.</param>
        /// <param name="expiration">The expiration.</param>
        /// <param name="replicateTo">Optional replication factor (defaults to <see cref="ReplicateTo.Zero"/>).</param>
        /// <param name="persistTo">Optional persistance factor (defaults to <see cref="PersistTo.Zero"/>).</param>
        /// <returns>The tracking <see cref="Task"/>.</returns>
        public static async Task UpsertSafeAsync<T>(this IBucket bucket, string key, T value, TimeSpan expiration, ReplicateTo replicateTo = ReplicateTo.Zero, PersistTo persistTo = PersistTo.Zero)
        {
            // $todo(jeff.lill):
            //
            // Not so sure about setting [uint.MaxValue] as the expiration here.

            var result = await bucket.UpsertAsync<T>(key, value, expiration, replicateTo, persistTo);

            VerifySuccess(result, replicateOrPersist: replicateTo != ReplicateTo.Zero || persistTo != PersistTo.Zero);
        }

        /// <summary>
        /// Inserts or updates an entity setting an expiration, throwing an exception if there are errors.
        /// </summary>
        /// <typeparam name="TEntity">The entity type.</typeparam>
        /// <param name="bucket">The bucket.</param>
        /// <param name="entity">The entity.</param>
        /// <param name="expiration">The expiration.</param>
        /// <param name="replicateTo">Optional replication factor (defaults to <see cref="ReplicateTo.Zero"/>).</param>
        /// <param name="persistTo">Optional persistance factor (defaults to <see cref="PersistTo.Zero"/>).</param>
        /// <returns>The tracking <see cref="Task"/>.</returns>
        public static async Task UpsertSafeAsync<TEntity>(this IBucket bucket, TEntity entity, TimeSpan expiration, ReplicateTo replicateTo = ReplicateTo.Zero, PersistTo persistTo = PersistTo.Zero)
            where TEntity : class, IEntity
        {
            Covenant.Requires<ArgumentNullException>(entity != null);

            // $todo(jeff.lill):
            //
            // Not so sure about setting [uint.MaxValue] as the expiration here.

            var result = await bucket.UpsertAsync<TEntity>(entity.GetKey(), entity, expiration, replicateTo, persistTo);

            VerifySuccess(result, replicateOrPersist: replicateTo != ReplicateTo.Zero || persistTo != PersistTo.Zero);
        }

        /// <summary>
        /// Inserts or updates a key using a CAS and setting an expiration, throwing an exception if there are errors.
        /// </summary>
        /// <typeparam name="T">The value type.</typeparam>
        /// <param name="bucket">The bucket.</param>
        /// <param name="key">The key.</param>
        /// <param name="value">The value.</param>
        /// <param name="cas">The CAS.</param>
        /// <param name="expiration">The expiration.</param>
        /// <param name="replicateTo">Optional replication factor (defaults to <see cref="ReplicateTo.Zero"/>).</param>
        /// <param name="persistTo">Optional persistance factor (defaults to <see cref="PersistTo.Zero"/>).</param>
        /// <returns>The tracking <see cref="Task"/>.</returns>
        public static async Task UpsertSafeAsync<T>(this IBucket bucket, string key, T value, ulong cas, TimeSpan expiration, ReplicateTo replicateTo = ReplicateTo.Zero, PersistTo persistTo = PersistTo.Zero)
        {
            var result = await bucket.UpsertAsync<T>(key, value, cas, expiration, replicateTo, persistTo);

            VerifySuccess(result, replicateOrPersist: replicateTo != ReplicateTo.Zero || persistTo != PersistTo.Zero);
        }

        /// <summary>
        /// Inserts or updates an entity using a CAS and setting an expiration, throwing an exception if there are errors.
        /// </summary>
        /// <typeparam name="TEntity">The entity type.</typeparam>
        /// <param name="bucket">The bucket.</param>
        /// <param name="entity">The entity.</param>
        /// <param name="cas">The CAS.</param>
        /// <param name="expiration">The expiration.</param>
        /// <param name="replicateTo">Optional replication factor (defaults to <see cref="ReplicateTo.Zero"/>).</param>
        /// <param name="persistTo">Optional persistance factor (defaults to <see cref="PersistTo.Zero"/>).</param>
        /// <returns>The tracking <see cref="Task"/>.</returns>
        public static async Task UpsertSafeAsync<TEntity>(this IBucket bucket, TEntity entity, ulong cas, TimeSpan expiration, ReplicateTo replicateTo = ReplicateTo.Zero, PersistTo persistTo = PersistTo.Zero)
            where TEntity : class, IEntity
        {
            var result = await bucket.UpsertAsync<TEntity>(entity.GetKey(), entity, cas, expiration, replicateTo, persistTo);

            VerifySuccess(result, replicateOrPersist: replicateTo != ReplicateTo.Zero || persistTo != PersistTo.Zero);
        }
    }
}
