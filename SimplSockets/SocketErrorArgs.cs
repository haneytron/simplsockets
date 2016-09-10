using System;

namespace SimplSockets
{
    /// <summary>
    /// Socket error args.
    /// </summary>
    public class SocketErrorArgs : EventArgs
    {
        /// <summary>
        /// Internal constructor.
        /// </summary>
        internal SocketErrorArgs() { }

        /// <summary>
        /// The exception.
        /// </summary>
        public Exception Exception { get; internal set; }

        /// <summary>
        /// The Connection's GUID
        /// </summary>
        public Guid? ConnectionGuid { get; set; }
    }
}

