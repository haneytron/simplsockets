using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Sockets;
using System.Text;

namespace SimplSockets
{
    /// <summary>
    /// A received message.
    /// </summary>
    public class ReceivedMessage
    {
        internal Socket Socket;
        internal int ThreadId;
        /// <summary>
        /// The connection's GUID.
        /// </summary>
        public Guid Guid { get; set; }
        /// <summary>
        /// The message bytes.
        /// </summary>
        public byte[] Message;
    }
}
