using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace SimplSockets
{
    /// <summary>
    /// Wraps sockets and provides intuitive, extremely efficient, scalable methods for client-server communication.
    /// </summary>
    public class SimplSocketServer : ISimplSocketServer
    {
        // The function that creates a new socket
        private readonly Func<Socket> _socketFunc = null;
        // The currently used socket
        private Socket _socket = null;
        // The message buffer size to use for send/receive
        private readonly int _messageBufferSize = 0;
        // The communication timeout, in milliseconds
        private readonly int _communicationTimeout = 0;
        // The maximum message size
        private readonly int _maxMessageSize = 0;
        // The maximum connections to allow to use the socket simultaneously
        private readonly int _maximumConnections = 0;
        // The semaphore that enforces the maximum numbers of simultaneous connections
        private readonly Semaphore _maxConnectionsSemaphore = null;
        // Whether or not to use the Nagle algorithm
        private readonly bool _useNagleAlgorithm = false;
        // The linger option
        private readonly LingerOption _lingerOption = new LingerOption(true, 0);

        // Whether or not the socket is currently listening
        private volatile bool _isListening = false;
        private readonly object _isListeningLock = new object();

        // The currently connected clients
        private readonly List<Socket> _currentlyConnectedClients = null;
        private readonly ReaderWriterLockSlim _currentlyConnectedClientsLock = new ReaderWriterLockSlim();

        // Various pools
        private readonly Pool<MessageState> _messageStatePool = null;
        private readonly Pool<byte[]> _bufferPool = null;
        private readonly Pool<ReceivedMessage> _receivedMessagePool = null;
        private readonly Pool<MessageReceivedArgs> _messageReceivedArgsPool = null;
        private readonly Pool<SocketErrorArgs> _socketErrorArgsPool = null;

        // The control bytes placeholder - the first 4 bytes are little endian message length, the last 4 are thread id
        private static readonly byte[] _controlBytesPlaceholder = new byte[] { 0, 0, 0, 0, 0, 0, 0, 0 };

        /// <summary>
        /// The constructor.
        /// </summary>
        /// <param name="socketFunc">The function that creates a new socket. Use this to specify your socket constructor and initialize settings.</param>
        /// <param name="messageBufferSize">The message buffer size to use for send/receive.</param>
        /// <param name="communicationTimeout">The communication timeout, in milliseconds.</param>
        /// <param name="maxMessageSize">The maximum message size.</param>
        /// <param name="maximumConnections">The maximum number of connections to allow simultaneously.</param>
        /// <param name="useNagleAlgorithm">Whether or not to use the Nagle algorithm.</param>
        public SimplSocketServer(Func<Socket> socketFunc, int messageBufferSize = 4096, int communicationTimeout = 10000, int maxMessageSize = 100 * 1024 * 1024, int maximumConnections = 50, bool useNagleAlgorithm = false)
        {
            // Sanitize
            if (socketFunc == null)
            {
                throw new ArgumentNullException("socketFunc");
            }
            if (messageBufferSize < 128)
            {
                throw new ArgumentException("must be >= 128", "messageBufferSize");
            }
            if (communicationTimeout < 5000)
            {
                throw new ArgumentException("must be >= 5000", "communicationTimeout");
            }
            if (maxMessageSize < 1024)
            {
                throw new ArgumentException("must be >= 1024", "maxMessageSize");
            }
            if (maximumConnections <= 0)
            {
                throw new ArgumentException("must be > 0", "maximumConnections");
            }

            _socketFunc = socketFunc;
            _messageBufferSize = messageBufferSize;
            _maximumConnections = maximumConnections;
            _maxConnectionsSemaphore = new Semaphore(maximumConnections, maximumConnections);
            _communicationTimeout = communicationTimeout;
            _maxMessageSize = maxMessageSize;
            _useNagleAlgorithm = useNagleAlgorithm;

            _currentlyConnectedClients = new List<Socket>(maximumConnections);

            // Create the pools
            _messageStatePool = new Pool<MessageState>(maximumConnections, () => new MessageState(), messageState =>
            {
                messageState.Buffer = null;
                messageState.ReceiveBufferQueue = null;
                messageState.Handler = null;
                messageState.ThreadId = -1;
                messageState.BytesToRead = -1;
            });
            _bufferPool = new Pool<byte[]>(maximumConnections, () => new byte[messageBufferSize]);
            _receivedMessagePool = new Pool<ReceivedMessage>(maximumConnections, () => new ReceivedMessage(), receivedMessage =>
            {
                receivedMessage.Message = null;
                receivedMessage.Socket = null;
            });
            _messageReceivedArgsPool = new Pool<MessageReceivedArgs>(maximumConnections, () => new MessageReceivedArgs(), messageReceivedArgs => { messageReceivedArgs.ReceivedMessage = null; });
            _socketErrorArgsPool = new Pool<SocketErrorArgs>(maximumConnections, () => new SocketErrorArgs(), socketErrorArgs => { socketErrorArgs.Exception = null; });
        }

        /// <summary>
        /// Begin listening for incoming connections. Once this is called, you must call Close before calling Listen again.
        /// </summary>
        /// <param name="localEndpoint">The local endpoint to listen on.</param>
        public void Listen(EndPoint localEndpoint)
        {
            // Sanitize
            if (localEndpoint == null)
            {
                throw new ArgumentNullException("localEndpoint");
            }

            lock (_isListeningLock)
            {
                if (_isListening)
                {
                    throw new InvalidOperationException("socket is already in use");
                }

                _isListening = true;
            }

            // Create socket
            _socket = _socketFunc();

            try
            {
                _socket.Bind(localEndpoint);
                _socket.Listen(_maximumConnections);

                // Post accept on the listening socket
                _socket.BeginAccept(AcceptCallback, null);
            }
            catch (SocketException ex)
            {
                HandleCommunicationError(_socket, ex);
            }
            catch (ObjectDisposedException)
            {
                // If disposed, handle communication error was already done and we're just catching up on other threads. suppress it.
            }
        }

        /// <summary>
        /// Broadcasts a message to all connected clients without waiting for a response (one-way communication).
        /// </summary>
        /// <param name="message">The message to send.</param>
        public void Broadcast(byte[] message)
        {
            // Sanitize
            if (message == null)
            {
                throw new ArgumentNullException("message");
            }

            // Get the current thread ID
            int threadId = Thread.CurrentThread.ManagedThreadId;

            var messageWithControlBytes = AppendControlBytesToMessage(message, threadId);

            // Do the send
            _currentlyConnectedClientsLock.EnterReadLock();
            try
            {

                foreach (var client in _currentlyConnectedClients)
                {
                    try
                    {
                        client.BeginSend(messageWithControlBytes, 0, messageWithControlBytes.Length, 0, SendCallback, client);
                    }
                    catch
                    {
                        // Swallow it
                        // TODO: queue it up for disconnection?
                    }
                }
            }
            finally
            {
                _currentlyConnectedClientsLock.ExitReadLock();
            }
        }

        /// <summary>
        /// Sends a message back to the client.
        /// </summary>
        /// <param name="message">The reply message to send.</param>
        /// <param name="receivedMessage">The received message which is being replied to.</param>
        public void Reply(byte[] message, ReceivedMessage receivedMessage)
        {
            // Sanitize
            if (message == null)
            {
                throw new ArgumentNullException("message");
            }
            if (receivedMessage.Socket == null)
            {
                throw new ArgumentException("contains corrupted data", "receivedMessageState");
            }

            var messageWithControlBytes = AppendControlBytesToMessage(message, receivedMessage.ThreadId);

            // Do the send to the appropriate client
            try
            {
                receivedMessage.Socket.BeginSend(messageWithControlBytes, 0, messageWithControlBytes.Length, 0, SendCallback, receivedMessage.Socket);
            }
            catch (SocketException ex)
            {
                HandleCommunicationError(_socket, ex);
            }
            catch (ObjectDisposedException)
            {
                // If disposed, handle communication error was already done and we're just catching up on other threads. suppress it.
            }
        }

        /// <summary>
        /// Closes the connection. Once this is called, you can call Listen again.
        /// </summary>
        public void Close()
        {
            // Close the socket
            try
            {
                _socket.Shutdown(SocketShutdown.Both);
            }
            catch
            {
                // Ignore
            }

            _socket.Close();

            // No longer connected
            lock (_isListeningLock)
            {
                _isListening = false;
            }
        }

        /// <summary>
        /// Gets the currently connected client count.
        /// </summary>
        public int CurrentlyConnectedClientCount
        {
            get
            {
                return _currentlyConnectedClients.Count;
            }
        }

        /// <summary>
        /// An event that is fired when a client successfully connects to the server. Hook into this to do something when a connection succeeds.
        /// </summary>
        public event EventHandler ClientConnected;

        /// <summary>
        /// An event that is fired whenever a message is received. Hook into this to process messages and potentially call Reply to send a response.
        /// </summary>
        public event EventHandler<MessageReceivedArgs> MessageReceived;

        /// <summary>
        /// An event that is fired whenever a socket communication error occurs. Hook into this to do something when communication errors happen.
        /// </summary>
        public event EventHandler<SocketErrorArgs> Error;

        /// <summary>
        /// Disposes the instance and frees unmanaged resources.
        /// </summary>
        public void Dispose()
        {
            // Close/dispose the socket
            _socket.Close();
        }

        private byte[] AppendControlBytesToMessage(byte[] message, int threadId)
        {
            // Create room for the control bytes
            var messageWithControlBytes = new byte[_controlBytesPlaceholder.Length + message.Length];
            Buffer.BlockCopy(message, 0, messageWithControlBytes, _controlBytesPlaceholder.Length, message.Length);
            // Set the control bytes on the message
            SetControlBytes(messageWithControlBytes, message.Length, threadId);
            return messageWithControlBytes;
        }

        private void AcceptCallback(IAsyncResult asyncResult)
        {
            // Get the client handler socket
            Socket handler = null;
            try
            {
                handler = _socket.EndAccept(asyncResult);
            }
            catch (SocketException ex)
            {
                HandleCommunicationError(handler, ex);
                return;
            }
            catch (ObjectDisposedException)
            {
                // If disposed, handle communication error was already done and we're just catching up on other threads. suppress it.
                return;
            }

            // Turn on or off Nagle algorithm
            handler.NoDelay = !_useNagleAlgorithm;
            // Set the linger state
            handler.LingerState = _lingerOption;

            // Post accept on the listening socket
            try
            {
                _socket.BeginAccept(AcceptCallback, null);
            }
            catch (SocketException ex)
            {
                HandleCommunicationError(_socket, ex);
                return;
            }
            catch (ObjectDisposedException)
            {
                // If disposed, handle communication error was already done and we're just catching up on other threads. suppress it.
                return;
            }

            // Do not proceed until we have room to do so
            if (!_maxConnectionsSemaphore.WaitOne(_communicationTimeout))
            {
                handler.Close();
            }

            // Enroll in currently connected client sockets
            _currentlyConnectedClientsLock.EnterWriteLock();
            try
            {
                _currentlyConnectedClients.Add(handler);
            }
            finally
            {
                _currentlyConnectedClientsLock.ExitWriteLock();
            }

            // Fire the event if needed
            var clientConnected = ClientConnected;
            if (clientConnected != null)
            {
                // Fire the event 
                clientConnected(this, EventArgs.Empty);
            }

            // Get message state
            var messageState = _messageStatePool.Pop();
            messageState.Handler = handler;
            messageState.Buffer = _bufferPool.Pop();
            // Create receive buffer queue for this client
            messageState.ReceiveBufferQueue = new BlockingQueue<KeyValuePair<byte[], int>>(_maximumConnections * 10);

            try
            {
                handler.BeginReceive(messageState.Buffer, 0, messageState.Buffer.Length, 0, ReceiveCallback, messageState);
            }
            catch (SocketException ex)
            {
                HandleCommunicationError(handler, ex);
                return;
            }
            catch (ObjectDisposedException)
            {
                // If disposed, handle communication error was already done and we're just catching up on other threads. suppress it.
                return;
            }

            // Spin up the keep-alive
            KeepAlive(handler);

            // Process all incoming messages
            var processMessageState = _messageStatePool.Pop();
            processMessageState.ReceiveBufferQueue = messageState.ReceiveBufferQueue;
            processMessageState.Handler = handler;

            ProcessReceivedMessage(processMessageState);
        }

        private void KeepAlive(Socket handler)
        {
            // Do the keep-alive
            try
            {
                var waitHandle = handler.BeginSend(_controlBytesPlaceholder, 0, _controlBytesPlaceholder.Length, 0, KeepAliveCallback, handler).AsyncWaitHandle;
                if (!waitHandle.WaitOne(_communicationTimeout))
                {
                    HandleCommunicationTimeout(handler);
                }
                waitHandle.Close();
            }
            catch (SocketException ex)
            {
                HandleCommunicationError(handler, ex);
            }
            catch (ObjectDisposedException)
            {
                // If disposed, handle communication error was already done and we're just catching up on other threads. suppress it.
            }
        }

        private void KeepAliveCallback(IAsyncResult asyncResult)
        {
            SendCallback(asyncResult);

            Thread.Sleep(1000);

            KeepAlive((Socket)asyncResult.AsyncState);
        }

        private void SendCallback(IAsyncResult asyncResult)
        {
            // Get the socket to complete on
            Socket socket = (Socket)asyncResult.AsyncState;

            // Complete the send
            try
            {
                socket.EndSend(asyncResult);
            }
            catch (SocketException ex)
            {
                HandleCommunicationError(socket, ex);
            }
            catch (ObjectDisposedException)
            {
                // If disposed, handle communication error was already done and we're just catching up on other threads. suppress it.
            }
        }

        private void ReceiveCallback(IAsyncResult asyncResult)
        {
            // Get the message state and buffer
            var messageState = (MessageState)asyncResult.AsyncState;
            int bytesRead = 0;

            // Read the data
            try
            {
                bytesRead = messageState.Handler.EndReceive(asyncResult);
            }
            catch (SocketException ex)
            {
                HandleCommunicationError(messageState.Handler, ex);
                return;
            }
            catch (ObjectDisposedException)
            {
                // If disposed, handle communication error was already done and we're just catching up on other threads. suppress it.
                return;
            }

            if (bytesRead > 0)
            {
                // Add buffer to queue
                messageState.ReceiveBufferQueue.Enqueue(new KeyValuePair<byte[], int>(messageState.Buffer, bytesRead));

                // Post receive on the handler socket
                messageState.Buffer = _bufferPool.Pop();
                try
                {
                    messageState.Handler.BeginReceive(messageState.Buffer, 0, messageState.Buffer.Length, 0, ReceiveCallback, messageState);
                }
                catch (SocketException ex)
                {
                    HandleCommunicationError(messageState.Handler, ex);
                }
                catch (ObjectDisposedException)
                {
                    // If disposed, handle communication error was already done and we're just catching up on other threads. suppress it.
                }
            }
        }

        private void ProcessReceivedMessage(MessageState messageState)
        {
            int availableTest = 0;
            int controlBytesOffset = 0;
            byte[] protocolBuffer = new byte[_controlBytesPlaceholder.Length];

            // Loop until socket is done
            while (_isListening)
            {
                // If the socket is disposed, we're done
                try
                {
                    availableTest = messageState.Handler.Available;
                }
                catch (ObjectDisposedException)
                {
                    // Peace out!
                    _messageStatePool.Push(messageState);
                    return;
                }

                // Get the next buffer from the queue
                var receiveBufferEntry = messageState.ReceiveBufferQueue.Dequeue();
                var buffer = receiveBufferEntry.Key;
                int bytesRead = receiveBufferEntry.Value;

                int currentOffset = 0;

                while (currentOffset < bytesRead)
                {
                    // Check if we need to get our control byte values
                    if (messageState.BytesToRead == -1)
                    {
                        var controlBytesNeeded = _controlBytesPlaceholder.Length - controlBytesOffset;
                        var controlBytesAvailable = bytesRead - currentOffset;

                        var controlBytesToCopy = Math.Min(controlBytesNeeded, controlBytesAvailable);

                        // Copy bytes to control buffer
                        Buffer.BlockCopy(buffer, currentOffset, protocolBuffer, controlBytesOffset, controlBytesToCopy);

                        controlBytesOffset += controlBytesToCopy;
                        currentOffset += controlBytesToCopy;

                        // Check if done
                        if (controlBytesOffset == _controlBytesPlaceholder.Length)
                        {
                            // Parse out control bytes
                            ExtractControlBytes(protocolBuffer, out messageState.BytesToRead, out messageState.ThreadId);

                            // Reset control bytes offset
                            controlBytesOffset = 0;

                            // Ensure message is not larger than maximum message size
                            if (messageState.BytesToRead > _maxMessageSize)
                            {
                                HandleCommunicationError(messageState.Handler, new InvalidOperationException(string.Format("message of length {0} exceeds maximum message length of {1}", messageState.BytesToRead, _maxMessageSize)));
                                return;
                            }
                        }

                        // Continue the loop
                        continue;
                    }

                    // Have control bytes, get message bytes

                    // SPECIAL CASE: if empty message, skip a bunch of stuff
                    if (messageState.BytesToRead != 0)
                    {
                        // Initialize buffer if needed
                        if (messageState.Buffer == null)
                        {
                            messageState.Buffer = new byte[messageState.BytesToRead];
                        }

                        var bytesAvailable = bytesRead - currentOffset;

                        var bytesToCopy = Math.Min(messageState.BytesToRead, bytesAvailable);

                        // Copy bytes to buffer
                        Buffer.BlockCopy(buffer, currentOffset, messageState.Buffer, messageState.Buffer.Length - messageState.BytesToRead, bytesToCopy);

                        currentOffset += bytesToCopy;
                        messageState.BytesToRead -= bytesToCopy;
                    }

                    // Check if we're done
                    if (messageState.BytesToRead == 0)
                    {
                        if (messageState.Buffer != null)
                        {
                            // Done, add to complete received messages
                            CompleteMessage(messageState.Handler, messageState.ThreadId, messageState.Buffer);
                        }

                        // Reset message state
                        messageState.Buffer = null;
                        messageState.BytesToRead = -1;
                        messageState.ThreadId = -1;
                    }
                }

                // Push the buffer back onto the pool
                _bufferPool.Push(buffer);
            }
        }

        private void CompleteMessage(Socket handler, int threadId, byte[] message)
        {
            var receivedMessage = _receivedMessagePool.Pop();
            receivedMessage.Socket = handler;
            receivedMessage.ThreadId = threadId;
            receivedMessage.Message = message;

            // Fire the event if needed 
            var messageReceived = MessageReceived;
            if (messageReceived != null)
            {
                // Create the message received args 
                var messageReceivedArgs = _messageReceivedArgsPool.Pop();
                messageReceivedArgs.ReceivedMessage = receivedMessage;
                // Fire the event 
                messageReceived(this, messageReceivedArgs);
                // Back in the pool
                _messageReceivedArgsPool.Push(messageReceivedArgs);
            }

            // Place received message back in pool
            _receivedMessagePool.Push(receivedMessage);
        }

        /// <summary>
        /// Handles a timeout in socket communication.
        /// </summary>
        /// <param name="socket">The socket that has timed out.</param>
        private void HandleCommunicationTimeout(Socket socket)
        {
            HandleCommunicationError(socket, new TimeoutException(string.Format("The timeout ({0}ms) expired prior to the socket operation completing", _communicationTimeout)));
        }

        /// <summary>
        /// Handles an error in socket communication.
        /// </summary>
        /// <param name="socket">The socket.</param>
        /// <param name="ex">The exception that the socket raised.</param>
        private void HandleCommunicationError(Socket socket, Exception ex)
        {
            lock (socket)
            {
                // Close the socket
                try
                {
                    socket.Shutdown(SocketShutdown.Both);
                }
                catch (SocketException)
                {
                    // Socket was not able to be shutdown, likely because it was never opened
                }
                catch (ObjectDisposedException)
                {
                    // Socket was already closed/disposed, so return out to prevent raising the Error event multiple times
                    // This is most likely to happen when an error occurs during heavily multithreaded use
                    return;
                }

                // Close / dispose the socket
                socket.Close();
            }

            // Try to unenroll from currently connected client sockets
            _currentlyConnectedClientsLock.EnterWriteLock();
            var shouldRelease = false;
            try
            {
                shouldRelease = _currentlyConnectedClients.Remove(socket);
            }
            finally
            {
                _currentlyConnectedClientsLock.ExitWriteLock();
            }

            // Release one from the max connections semaphore if needed
            if (shouldRelease)
            {
                _maxConnectionsSemaphore.Release();
            }

            // Raise the error event 
            var error = Error;
            if (error != null)
            {
                var socketErrorArgs = _socketErrorArgsPool.Pop();
                socketErrorArgs.Exception = ex;
                error(this, socketErrorArgs);
                _socketErrorArgsPool.Push(socketErrorArgs);
            }
        }

        private class MessageState
        {
            public byte[] Buffer = null;
            public BlockingQueue<KeyValuePair<byte[], int>> ReceiveBufferQueue = null;
            public Socket Handler = null;
            public int ThreadId = -1;
            public int BytesToRead = -1;
        }

        private static void SetControlBytes(byte[] buffer, int length, int threadId)
        {
            // Set little endian message length
            buffer[0] = (byte)length;
            buffer[1] = (byte)((length >> 8) & 0xFF);
            buffer[2] = (byte)((length >> 16) & 0xFF);
            buffer[3] = (byte)((length >> 24) & 0xFF);
            // Set little endian thread id
            buffer[4] = (byte)threadId;
            buffer[5] = (byte)((threadId >> 8) & 0xFF);
            buffer[6] = (byte)((threadId >> 16) & 0xFF);
            buffer[7] = (byte)((threadId >> 24) & 0xFF);
        }

        private static void ExtractControlBytes(byte[] buffer, out int messageLength, out int threadId)
        {
            messageLength = (buffer[3] << 24) | (buffer[2] << 16) | (buffer[1] << 8) | buffer[0];
            threadId = (buffer[7] << 24) | (buffer[6] << 16) | (buffer[5] << 8) | buffer[4];
        }
    }
}
