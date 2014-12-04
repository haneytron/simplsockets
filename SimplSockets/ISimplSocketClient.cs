using System;
using System.Net;

namespace SimplSockets
{
    /// <summary>
    /// Represents a client socket.
    /// </summary>
    public interface ISimplSocketClient : IDisposable
    {
        /// <summary>
        /// Connects to an endpoint. Once this is called, you must call Close before calling Connect again.
        /// </summary>
        /// <param name="endPoint">The endpoint.</param>
        /// <returns>true if connection is successful, false otherwise.</returns>
        bool Connect(EndPoint endPoint);

        /// <summary>
        /// Sends a message to the server without waiting for a response (one-way communication).
        /// </summary>
        /// <param name="message">The message to send.</param>
        void Send(byte[] message);

        /// <summary>
        /// Sends a message to the server and then waits for the response to that message.
        /// </summary>
        /// <param name="message">The message to send.</param>
        /// <returns>The response message.</returns>
        byte[] SendReceive(byte[] message);

        /// <summary>
        /// Closes the connection. Once this is called, you can call Connect again to start a new client connection.
        /// </summary>
        void Close();

        /// <summary>
        /// An event that is fired whenever a message is received. Hook into this to process messages and potentially call Reply to send a response.
        /// </summary>
        event EventHandler<MessageReceivedArgs> MessageReceived;

        /// <summary>
        /// An event that is fired whenever a socket communication error occurs. Hook into this to do something when communication errors happen.
        /// </summary>
        event EventHandler<SocketErrorArgs> Error;
    }
}
