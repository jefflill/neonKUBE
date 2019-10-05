﻿//-----------------------------------------------------------------------------
// FILE:	    EntityListWrapper.cs
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
using System.Collections;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics.Contracts;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;

using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json.Serialization;

using Neon.Common;
using Neon.Couchbase.DynamicData;

// NOTE:
//
// This implementation is quite clean.  One thing developers should avoid is
// having more that one instance of a given item in a list.  This doesn't
// really much much sense anyway from a database perspective.

namespace Neon.Couchbase.DynamicData.Internal
{
    /// <summary>
    /// <b>Platform use only:</b> Used by <see cref="IDynamicEntity"/> implementations 
    /// to wrap an <see cref="IList"/> of entities around a <see cref="JArray"/>.
    /// </summary>
    /// <typeparam name="TEntity">The list item type (implementing <see cref="IDynamicEntity"/>).</typeparam>
    /// <remarks>
    /// <note>
    /// This class is intended for use only by classes generated by the 
    /// <b>entity-gen</b> build tool.
    /// </note>
    /// </remarks>
    /// <threadsafety instance="false"/>
    public class EntityListWrapper<TEntity> : IList<TEntity>, ICollection<TEntity>, INotifyCollectionChanged
        where TEntity : class, IDynamicEntity, new()
    {
        private const string DetachedError = "The underlying [JArray] has been detached.";

        private IDynamicEntity                         parentEntity;
        private IDynamicEntityContext                  context;
        private JArray                          jArray;
        private ObservableCollection<TEntity>   list;
        private EventHandler<EventArgs>         itemChangedHandler;

        /// <summary>
        /// Constructor.
        /// </summary>
        /// <param name="parentEntity">The <see cref="IDynamicEntity"/> that owns this list.</param>
        /// <param name="context">The <see cref="IDynamicEntityContext"/> or <c>null</c>.</param>
        /// <param name="jArray">The underlying <see cref="jArray"/>.</param>
        /// <param name="items">The initial items or <c>null</c> to initialize from <paramref name="jArray"/>.</param>
        public EntityListWrapper(IDynamicEntity parentEntity, IDynamicEntityContext context, JArray jArray, IEnumerable<TEntity> items)
        {
            Covenant.Requires<ArgumentNullException>(parentEntity != null, nameof(parentEntity));
            Covenant.Requires<ArgumentNullException>(jArray != null, nameof(jArray));

            this.parentEntity       = parentEntity;
            this.context            = context;
            this.jArray             = jArray;
            this.itemChangedHandler = new EventHandler<EventArgs>(OnItemChanged);
            this.list               = new ObservableCollection<TEntity>();

            if (items != null)
            {
                Covenant.Assert(jArray.Count == 0);

                foreach (var item in items)
                {
                    Add(item);
                }
            }
            else
            {
                foreach (var jToken in jArray)
                {
                    if (jToken.Type == JTokenType.Object)
                    {
                        var item = DynamicEntity.Create<TEntity>((JObject)jToken, context);

                        item.Changed += itemChangedHandler;   // We need to bubble up nested change events
                        list.Add(item);
                    }
                    else
                    {
                        list.Add(null); // Ignore anything that's not an object.
                    }
                }
            }

            // We're going to listen to our own collection changed event to
            // bubble them up.

            list.CollectionChanged +=
                (s, a) =>
                {
                    CollectionChanged?.Invoke(this, a);
                    parentEntity._OnChanged();
                };
        }

        /// <summary>
        /// Returns <c>true</c> if the list is currently attached to a <see cref="JArray"/>.
        /// </summary>
        internal bool IsAttached
        {
            get { return jArray != null; }
        }

        /// <summary>
        /// Detaches any event listeners from the underlying items and then
        /// disassociates the array.
        /// </summary>
        internal void Detach()
        {
            foreach (var item in list)
            {
                if (item != null)
                {
                    item.Changed -= itemChangedHandler;
                }
            }

            list.Clear();

            if (jArray != null)
            {
                jArray = null;
            }
        }

        /// <summary>
        /// Converts a list item into the equivalent <see cref="JToken"/>.
        /// </summary>
        private JToken ToToken(TEntity value)
        {
            return value?.JObject;
        }

        /// <summary>
        /// Called when any of the list item's <see cref="IDynamicEntity.Changed"/> event is
        /// raised.  This method will bubble the notifications to the parent entity.
        /// </summary>
        /// <param name="sender">The sender.</param>
        /// <param name="args">The event arguments.</param>
        private void OnItemChanged(object sender, EventArgs args)
        {
            parentEntity._OnChanged();
        }

        //---------------------------------------------------------------------
        // INotifyCollectionChanged implementation.

        /// <summary>
        /// Raised when the list changes.
        /// </summary>
        public event NotifyCollectionChangedEventHandler CollectionChanged;

        //---------------------------------------------------------------------
        // IList and ICollection implementations

        /// <summary>
        /// Searches the list for a specific item.
        /// </summary>
        /// <param name="item">The item to be located.</param>
        /// <returns>The index of the first item that matches the index, if found; or -1 otherwise.</returns>
        /// <exception cref="NotImplementedException">Thrown always.</exception>
        public int IndexOf(TEntity item)
        {
            return list.IndexOf(item);
        }

        /// <summary>
        /// Returns the number of items in the list.
        /// </summary>
        /// <exception cref="InvalidOperationException">Thrown if the array has been detached.</exception>
        public int Count
        {
            get
            {
                Covenant.Requires<InvalidOperationException>(jArray != null, DetachedError);
                return list.Count;
            }
        }

        /// <summary>
        /// Indicates whether the list is read-only.  This always returns <c>false.</c>
        /// </summary>
        /// <exception cref="InvalidOperationException">Thrown if the array has been detached.</exception>
        public bool IsReadOnly
        {
            get
            {
                Covenant.Requires<InvalidOperationException>(jArray != null, DetachedError);
                return false;
            }
        }

        /// <summary>
        /// Accesses the item at an index.
        /// </summary>
        /// <param name="index">The index.</param>
        /// <returns>The element at the index.</returns>
        /// <exception cref="InvalidOperationException">Thrown if the array has been detached.</exception>
        public TEntity this[int index]
        {
            get
            {
                Covenant.Requires<InvalidOperationException>(jArray != null, DetachedError);
                return list[index];
            }

            set
            {
                Covenant.Requires<InvalidOperationException>(jArray != null, DetachedError);

                // Detach the old item.

                if (list[index] != null)
                {
                    list[index].Changed -= itemChangedHandler;
                }

                // Attach the new item.

                if (value != null)
                {
                    value.Changed += itemChangedHandler;
                }

                jArray[index] = ToToken(value);
                list[index]   = value;
            }
        }

        /// <summary>
        /// Inserts an item at a specified index.
        /// </summary>
        /// <param name="index">The index.</param>
        /// <param name="item">The item</param>
        /// <exception cref="InvalidOperationException">Thrown if the array has been detached.</exception>
        public void Insert(int index, TEntity item)
        {
            Covenant.Requires<InvalidOperationException>(jArray != null, DetachedError);

            if (item != null)
            {
                item.Changed += itemChangedHandler;
            }

            jArray.Insert(index, ToToken(item));
            list.Insert(index, item);
        }

        /// <summary>
        /// Removes the item at a specified index.
        /// </summary>
        /// <param name="index">The index.</param>
        /// <exception cref="InvalidOperationException">Thrown if the array has been detached.</exception>
        public void RemoveAt(int index)
        {
            Covenant.Requires<InvalidOperationException>(jArray != null, DetachedError);

            var item = list[index];

            if (item != null)
            {
                item.Changed -= itemChangedHandler;
            }

            jArray.RemoveAt(index);
            list.RemoveAt(index);
        }

        /// <summary>
        /// Appends an item to the list.
        /// </summary>
        /// <param name="item">The item.</param>
        /// <exception cref="InvalidOperationException">Thrown if the array has been detached.</exception>
        public void Add(TEntity item)
        {
            Covenant.Requires<InvalidOperationException>(jArray != null, DetachedError);

            if (item != null)
            {
                item.Changed += itemChangedHandler;
            }

            jArray.Add(ToToken(item));
            list.Add(item);
        }

        /// <summary>
        /// Removes all items from the list.
        /// </summary>
        /// <exception cref="InvalidOperationException">Thrown if the array has been detached.</exception>
        public void Clear()
        {
            Covenant.Requires<InvalidOperationException>(jArray != null, DetachedError);

            jArray.Clear();

            // We need to detach all items before clearing the list.

            foreach (var item in list)
            {
                if (item != null)
                {
                    item.Changed -= itemChangedHandler;
                }
            }

            list.Clear();
        }

        /// <summary>
        /// Determines whether the list contains a specific item.
        /// </summary>
        /// <param name="item">The item.</param>
        /// <returns><c>true</c> if the item exists.</returns>
        public bool Contains(TEntity item)
        {
            return list.Contains(item);
        }

        /// <summary>
        /// Copies the list items to an array. 
        /// </summary>
        /// <param name="array">The destination array.</param>
        /// <param name="arrayIndex">The destination starting index.</param>
        /// <exception cref="InvalidOperationException">Thrown if the array has been detached.</exception>
        public void CopyTo(TEntity[] array, int arrayIndex)
        {
            Covenant.Requires<ArgumentNullException>(array != null, nameof(array));
            Covenant.Requires<InvalidOperationException>(jArray != null, DetachedError);

            list.CopyTo(array, arrayIndex);
        }

        /// <summary>
        /// Removes the first occurance of a specific item from the list.
        /// </summary>
        /// <param name="item">The item to be removed.</param>
        /// <returns><c>true</c> if the item was present and was removed.</returns>
        public bool Remove(TEntity item)
        {
            var index = IndexOf(item);

            if (index == -1)
            {
                return false;
            }

            RemoveAt(index);
            return true;
        }

        /// <summary>
        /// Returns a generic enumerator over the list items.
        /// </summary>
        /// <returns></returns>
        /// <exception cref="InvalidOperationException">Thrown if the array has been detached.</exception>
        public IEnumerator<TEntity> GetEnumerator()
        {
            Covenant.Requires<InvalidOperationException>(jArray != null, DetachedError);

            return ((IEnumerable<TEntity>)list).GetEnumerator();
        }

        /// <summary>
        /// Returns an enumerator over the list items.
        /// </summary>
        /// <returns></returns>
        /// <exception cref="InvalidOperationException">Thrown if the array has been detached.</exception>
        IEnumerator IEnumerable.GetEnumerator()
        {
            Covenant.Requires<InvalidOperationException>(jArray != null, DetachedError);

            return list.GetEnumerator();
        }
    }
}
