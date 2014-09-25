using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;

namespace SimplSockets
{
    /// <summary>
    /// Contains multiplexer data.
    /// </summary>
    internal class MultiplexerData
    {
        public byte[] Message { get; set; }
        public ManualResetEvent ManualResetEvent { get; set; }
    }
}
