using System;

namespace SimplSockets
{
    /// <summary>
    /// Client connected Args
    /// </summary>
    public class ClientConnectedArgs : EventArgs
    {
        /// <summary>
        /// Internal Constructor
        /// </summary>
        /// <param name="guid">Connection's GUID</param>
        public ClientConnectedArgs(Guid guid)
        {
            Guid = guid;
        }

        /// <summary>
        /// The connection's GUID.
        /// </summary>
        public Guid Guid { get; set; }
    }
}