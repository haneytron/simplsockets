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
        private readonly List<T> _queue = null;
        private volatile int _queueIndex = 0;
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
            _queue = new List<T>(poolCount);
            _newItemMethod = newItemMethod;
            _resetItemMethod = resetItemMethod;

            // Create new items
            for (int i = 0; i < poolCount; i++)
            {
                var item = _newItemMethod();
                if (_resetItemMethod != null) _resetItemMethod(item);
                _queue.Add(item);
            }
        }

        /// <summary>
        /// Pushes an item into the pool for later re-use, and resets it state if a reset method was provided to the constructor.
        /// </summary>
        /// <param name="item">The item.</param>
        public void Push(T item)
        {
            // Limit queue size
            if (_queueIndex == 0)
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
                _queueIndex--;
                _queue[_queueIndex] = item;
            }
        }

        /// <summary>
        /// Pops an item out of the pool for use.
        /// </summary>
        /// <returns>An item.</returns>
        public T Pop()
        {
            if (_queueIndex != _queue.Count)
            {
                lock (_queue)
                {
                    // Double lock check
                    if (_queueIndex != _queue.Count)
                    {
                        var item = _queue[_queueIndex];
                        _queue[_queueIndex] = null;
                        _queueIndex++;
                        return item;
                    }
                }
            }

            return _newItemMethod();
        }
    }
}
