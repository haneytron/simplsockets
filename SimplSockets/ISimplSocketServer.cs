using System;
using System.Net;

namespace SimplSockets
{
    /// <summary>
    /// Represents a server socket.
    /// </summary>
    public interface ISimplSocketServer : IDisposable
    {
        /// <summary>
        /// Begin listening for incoming connections. Once this is called, you must call Close before calling Listen again.
        /// </summary>
        /// <param name="localEndpoint">The local endpoint to listen on.</param>
        void Listen(EndPoint localEndpoint);

        /// <summary>
        /// Broadcasts a message to all connected clients without waiting for a response (one-way communication).
        /// </summary>
        /// <param name="message">The message to send.</param>
        void Broadcast(byte[] message);

        /// <summary>
        /// Sends a message back to the client.
        /// </summary>
        /// <param name="message">The reply message to send.</param>
        /// <param name="receivedMessage">The received message which is being replied to.</param>
        void Reply(byte[] message, ReceivedMessage receivedMessage);

        /// <summary>
        /// Closes the connection. Once this is called, you can call Listen again.
        /// </summary>
        void Close();

        /// <summary>
        /// Gets the currently connected client count.
        /// </summary>
        int CurrentlyConnectedClientCount { get; }

        /// <summary>
        /// An event that is fired when a client successfully connects to the server. Hook into this to do something when a connection succeeds.
        /// </summary>
        event EventHandler<ClientConnectedArgs> ClientConnected;

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
