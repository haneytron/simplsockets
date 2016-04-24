using System;
using System.Collections.Generic;

namespace SimplSockets
{
    /// <summary>
    /// A pool of objects that can be reused to manage memory efficiently.
    /// </summary>
    /// <typeparam name="T">The type of object that is pooled.</typeparam>
    internal sealed class Pool<T> where T : class
    {
        // The queue that holds the items
        private readonly Queue<T> _queue = null;
        // The initial pool count
        private readonly int _initialPoolCount = 0;
        // The method that creates a new item
        private readonly Func<T> _newItemMethod = null;
        // The method that resets an item's state
        private readonly Action<T> _resetItemMethod = null;

        /// <summary>
        /// The constructor.
        /// </summary>
        /// <param name="poolCount">The count of items in the pool.</param>
        /// <param name="newItemMethod">The method that creates a new item.</param>
        /// <param name="resetItemMethod">The method that resets an item's state. Optional.</param>
        public Pool(int poolCount, Func<T> newItemMethod, Action<T> resetItemMethod = null)
        {
            _queue = new Queue<T>(poolCount);
            _initialPoolCount = poolCount;
            _newItemMethod = newItemMethod;
            _resetItemMethod = resetItemMethod;

            // Create new items
            for (int i = 0; i < poolCount; i++)
            {
                Push(newItemMethod());
            }
        }

        /// <summary>
        /// Pushes an item into the pool for later re-use, and resets it state if a reset method was provided to the constructor.
        /// </summary>
        /// <param name="item">The item.</param>
        public void Push(T item)
        {
            // Limit queue size
            if (_queue.Count > _initialPoolCount)
            {
                // Dispose if applicable
                var disposable = item as IDisposable;
                if (disposable != null) disposable.Dispose();
                return;
            }

            if (_resetItemMethod != null)
            {
                _resetItemMethod(item);
            }

            lock (_queue)
            {
                _queue.Enqueue(item);
            }
        }

        /// <summary>
        /// Pops an item out of the pool for use.
        /// </summary>
        /// <returns>An item.</returns>
        public T Pop()
        {
            if (_queue.Count > 0)
            {
                lock (_queue)
                {
                    // Double lock check
                    if (_queue.Count > 0)
                    {
                        return _queue.Dequeue();
                    }
                }
            }

            return _newItemMethod();
        }
    }
}
