using System;
using System.Collections.Generic;
using System.Threading;

namespace SimplSockets
{
    /// <summary>
    /// A queue that wraps a regular generic queue but when empty will block Dequeue threads until an item is available or 1000 ms passes.
    /// This class is thread safe.
    /// </summary>
    /// <typeparam name="T">The type of the object contained in the queue.</typeparam>
    internal sealed class BlockingQueue<T>
    {
        // The underlying queue
        private readonly LinkedList<T> _queue = new LinkedList<T>();
        // The semaphore used for blocking
        private readonly Semaphore _semaphore = new Semaphore(0, Int32.MaxValue);

        /// <summary>
        /// Enqueues an item.
        /// </summary>
        /// <param name="item">An item.</param>
        public void Enqueue(T item)
        {
            lock (_queue)
            {
                _queue.AddLast(item);
                _semaphore.Release();
            }
        }

        /// <summary>
        /// Enqueues an item to the front of the queue.
        /// </summary>
        /// <param name="item">An item.</param>
        public void EnqueueFront(T item)
        {
            lock (_queue)
            {
                _queue.AddFirst(item);
                _semaphore.Release();
            }
        }

        /// <summary>
        /// Dequeues an item. Will block if the queue is empty until an item becomes available or 1000 ms passes.
        /// </summary>
        /// <returns>An item.</returns>
        public T Dequeue()
        {
            if (!_semaphore.WaitOne(1000))
            {
                return default(T);
            }

            lock (_queue)
            {
                if (_queue.Count == 0) return default(T);
                var firstNode = _queue.First;
                _queue.RemoveFirst();
                return firstNode.Value;
            }
        }

        /// <summary>
        /// Clears the queue.
        /// </summary>
        public void Clear()
        {
            lock (_queue)
            {
                _queue.Clear();
            }
        }
    }
}
