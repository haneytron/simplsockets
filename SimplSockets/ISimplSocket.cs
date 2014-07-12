using System;
using System.Net;

namespace SimplSockets
{
    /// <summary>
    /// Represents a socket.
    /// </summary>
    public interface ISimplSocket : IDisposable
    {
        /// <summary>
        /// Connects to an endpoint. Once this is called, you must call Close before calling Connect or Listen again. This method will not raise an error.
        /// </summary>
        /// <param name="endPoint">The endpoint.</param>
        /// <returns>true if connection is successful, false otherwise.</returns>
        bool Connect(EndPoint endPoint);

        /// <summary>
        /// Begin listening for incoming connections. Once this is called, you must call Close before calling Connect or Listen again.
        /// </summary>
        /// <param name="localEndpoint">The local endpoint to listen on.</param>
        void Listen(EndPoint localEndpoint);

        /// <summary>
        /// Sends a message to the server without waiting for a response (one-way communication).
        /// </summary>
        /// <param name="message">The message to send.</param>
        void Send(byte[] message);

        /// <summary>
        /// Sends a message to the server and receives the response to that message.
        /// </summary>
        /// <param name="message">The message to send.</param>
        /// <returns>The response message.</returns>
        byte[] SendReceive(byte[] message);

        /// <summary>
        /// Sends a message back to the client.
        /// </summary>
        /// <param name="message">The reply message to send.</param>
        /// <param name="receivedMessage">The received message which is being replied to.</param>
        void Reply(byte[] message, SimplSocket.ReceivedMessage receivedMessage);

        /// <summary>
        /// Closes the connection. Once this is called, you can call Connect or Listen again to start a new connection.
        /// </summary>
        void Close();

        /// <summary>
        /// Gets the currently connected client count (when acting as a client, this will return 0 or 1).
        /// </summary>
        int CurrentlyConnectedClientCount { get; }

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
