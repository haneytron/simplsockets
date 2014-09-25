using System;

namespace SimplSockets
{
    /// <summary>
    /// Message received args.
    /// </summary>
    public class MessageReceivedArgs : EventArgs
    {
        /// <summary>
        /// Internal constructor.
        /// </summary>
        internal MessageReceivedArgs() { }

        /// <summary>
        /// The received message.
        /// </summary>
        public ReceivedMessage ReceivedMessage { get; internal set; }
    }
}

