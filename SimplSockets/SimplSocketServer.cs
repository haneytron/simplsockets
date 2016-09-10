using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
        // Whether or not to use the Nagle algorithm
        private readonly bool _useNagleAlgorithm = false;
        // The linger option
        private readonly LingerOption _lingerOption = new LingerOption(true, 0);

        // Whether or not the socket is currently listening
        private volatile bool _isListening = false;
        private readonly object _isListeningLock = new object();

        // The currently connected clients
        private readonly List<ConnectedClient> _currentlyConnectedClients = null;
        private readonly ReaderWriterLockSlim _currentlyConnectedClientsLock = new ReaderWriterLockSlim();

        // The currently connected client receive queues
        private readonly Dictionary<Socket, BlockingQueue<SocketAsyncEventArgs>> _currentlyConnectedClientsReceiveQueues = null;
        private readonly ReaderWriterLockSlim _currentlyConnectedClientsReceiveQueuesLock = new ReaderWriterLockSlim();

        // Various pools
        private readonly Pool<SocketAsyncEventArgs> _socketAsyncEventArgsSendPool = null;
        private readonly Pool<SocketAsyncEventArgs> _socketAsyncEventArgsReceivePool = null;
        private readonly Pool<SocketAsyncEventArgs> _socketAsyncEventArgsKeepAlivePool = null;
        private readonly Pool<ReceivedMessage> _receivedMessagePool = null;
        private readonly Pool<MessageReceivedArgs> _messageReceivedArgsPool = null;
        private readonly Pool<SocketErrorArgs> _socketErrorArgsPool = null;

        // A completely blind guess at the number of expected connections to this server. 100 sounds good, right? Right.
        private const int PREDICTED_CONNECTION_COUNT = 100;

        /// <summary>
        /// The constructor.
        /// </summary>
        /// <param name="socketFunc">The function that creates a new socket. Use this to specify your socket constructor and initialize settings.</param>
        /// <param name="messageBufferSize">The message buffer size to use for send/receive.</param>
        /// <param name="communicationTimeout">The communication timeout, in milliseconds.</param>
        /// <param name="maxMessageSize">The maximum message size.</param>
        /// <param name="useNagleAlgorithm">Whether or not to use the Nagle algorithm.</param>
        public SimplSocketServer(Func<Socket> socketFunc, int messageBufferSize = 65536, int communicationTimeout = 10000, int maxMessageSize = 10 * 1024 * 1024, bool useNagleAlgorithm = false)
        {
            // Sanitize
            if (socketFunc == null)
            {
                throw new ArgumentNullException("socketFunc");
            }
            if (messageBufferSize < 512)
            {
                throw new ArgumentException("must be >= 512", "messageBufferSize");
            }
            if (communicationTimeout < 5000)
            {
                throw new ArgumentException("must be >= 5000", "communicationTimeout");
            }
            if (maxMessageSize < 1024)
            {
                throw new ArgumentException("must be >= 1024", "maxMessageSize");
            }

            _socketFunc = socketFunc;
            _messageBufferSize = messageBufferSize;
            _communicationTimeout = communicationTimeout;
            _maxMessageSize = maxMessageSize;
            _useNagleAlgorithm = useNagleAlgorithm;

            _clientMultiplexer = new Dictionary<int, MultiplexerData>(PREDICTED_CONNECTION_COUNT);
            _currentlyConnectedClients = new List<ConnectedClient>(PREDICTED_CONNECTION_COUNT);
            _currentlyConnectedClientsReceiveQueues = new Dictionary<Socket, BlockingQueue<SocketAsyncEventArgs>>(PREDICTED_CONNECTION_COUNT);

            _multiplexerDataPool = new Pool<MultiplexerData>(PREDICTED_CONNECTION_COUNT, () => new MultiplexerData { ManualResetEvent = new ManualResetEvent(false) }, multiplexerData =>
            {
                multiplexerData.Message = null;
                multiplexerData.ManualResetEvent.Reset();
            });

            // Create the pools
            _socketAsyncEventArgsSendPool = new Pool<SocketAsyncEventArgs>(PREDICTED_CONNECTION_COUNT, () =>
            {
                var poolItem = new SocketAsyncEventArgs();
                poolItem.Completed += OperationCallback;
                return poolItem;
            });
            _socketAsyncEventArgsReceivePool = new Pool<SocketAsyncEventArgs>(PREDICTED_CONNECTION_COUNT, () =>
            {
                var poolItem = new SocketAsyncEventArgs();
                poolItem.SetBuffer(new byte[messageBufferSize], 0, messageBufferSize);
                poolItem.Completed += OperationCallback;
                return poolItem;
            });
            _socketAsyncEventArgsKeepAlivePool = new Pool<SocketAsyncEventArgs>(PREDICTED_CONNECTION_COUNT, () =>
            {
                var poolItem = new SocketAsyncEventArgs();
                poolItem.SetBuffer(ProtocolHelper.ControlBytesPlaceholder, 0, ProtocolHelper.ControlBytesPlaceholder.Length);
                poolItem.Completed += OperationCallback;
                return poolItem;
            });
            _receivedMessagePool = new Pool<ReceivedMessage>(PREDICTED_CONNECTION_COUNT, () => new ReceivedMessage(), receivedMessage =>
            {
                receivedMessage.Message = null;
                receivedMessage.Socket = null;
            });
            _messageReceivedArgsPool = new Pool<MessageReceivedArgs>(PREDICTED_CONNECTION_COUNT, () => new MessageReceivedArgs(), messageReceivedArgs => { messageReceivedArgs.ReceivedMessage = null; });
            _socketErrorArgsPool = new Pool<SocketErrorArgs>(PREDICTED_CONNECTION_COUNT, () => new SocketErrorArgs(), socketErrorArgs => { socketErrorArgs.Exception = null; });
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

            _socket.Bind(localEndpoint);
            _socket.Listen(PREDICTED_CONNECTION_COUNT);

            // very important to not have buffer for accept, see remarks on 288 byte threshold: https://msdn.microsoft.com/en-us/library/system.net.sockets.socket.acceptasync(v=vs.110).aspx
            var socketAsyncEventArgs = new SocketAsyncEventArgs();
            socketAsyncEventArgs.Completed += OperationCallback;

            // Post accept on the listening socket
            if (!TryUnsafeSocketOperation(_socket, SocketAsyncOperation.Accept, socketAsyncEventArgs))
            {
                lock (_isListeningLock)
                {
                    _isListening = false;
                    throw new Exception("Socket accept failed");
                }
            }

            // Spin up the keep-alive
            Task.Factory.StartNew(KeepAlive);
        }

        /// <summary>
        /// Sends a message directly to a client
        /// </summary>
        /// <param name="guid">Connection GUID of client</param>
        /// <param name="message">Payload to send</param>
        /// <returns>The response message.</returns>
        public byte[] Send(Guid guid, byte[] message)
        {
            if (message == null)
            {
                throw new ArgumentNullException(nameof(message));
            }
            if (message.Length == 0)
            {
                throw new ArgumentException("Argument is empty collection", nameof(message));
            }
            if (guid == Guid.Empty)
            {
                throw new ArgumentNullException(nameof(guid));
            }

            int threadId = Thread.CurrentThread.ManagedThreadId;

            var multiplexerData = EnrollMultiplexer(threadId);

            var messageWithControlBytes = ProtocolHelper.AppendControlBytesToMessage(message, threadId);

            byte[] resp = new byte[0];

            var client = _currentlyConnectedClients.FirstOrDefault(f => f.Guid == guid);
    
            var socketAsyncEventArgs = _socketAsyncEventArgsSendPool.Pop();
            socketAsyncEventArgs.SetBuffer(messageWithControlBytes, 0, messageWithControlBytes.Length);

            if (!TryUnsafeSocketOperation(client.Socket, SocketAsyncOperation.Send, socketAsyncEventArgs))
            {
                throw new Exception("001");
            }

            if (!multiplexerData.ManualResetEvent.WaitOne(_communicationTimeout))
            {
                HandleCommunicationError(_socket, new TimeoutException("The connection timed out before the response message was received"), client.Guid);

                // Unenroll from the multiplexer
                UnenrollMultiplexer(threadId);

                // No signal
                return null;
            }

            resp = multiplexerData.Message;


            UnenrollMultiplexer(threadId);

            return resp;
        }

        private readonly Pool<MultiplexerData> _multiplexerDataPool = null;
        private readonly Dictionary<int, MultiplexerData> _clientMultiplexer = null;
        private readonly ReaderWriterLockSlim _clientMultiplexerLock = new ReaderWriterLockSlim();
        private MultiplexerData EnrollMultiplexer(int threadId)
        {
            var multiplexerData = _multiplexerDataPool.Pop();

            _clientMultiplexerLock.EnterWriteLock();
            try
            {
                _clientMultiplexer.Add(threadId, multiplexerData);
            }
            finally
            {
                _clientMultiplexerLock.ExitWriteLock();
            }

            return multiplexerData;
        }
        private MultiplexerData GetMultiplexerData(int threadId)
        {
            MultiplexerData multiplexerData = null;

            _clientMultiplexerLock.EnterReadLock();
            try
            {
                _clientMultiplexer.TryGetValue(threadId, out multiplexerData);
            }
            finally
            {
                _clientMultiplexerLock.ExitReadLock();
            }

            return multiplexerData;
        }
        private void UnenrollMultiplexer(int threadId)
        {
            var multiplexerData = GetMultiplexerData(threadId);
            if (multiplexerData == null)
            {
                return;
            }

            _clientMultiplexerLock.EnterWriteLock();
            try
            {
                _clientMultiplexer.Remove(threadId);
            }
            finally
            {
                _clientMultiplexerLock.ExitWriteLock();
            }

            _multiplexerDataPool.Push(multiplexerData);
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

            var messageWithControlBytes = ProtocolHelper.AppendControlBytesToMessage(message, threadId);

            List<ConnectedClient> bustedClients = null;

            // Do the send
            _currentlyConnectedClientsLock.EnterReadLock();
            try
            {
                foreach (var client in _currentlyConnectedClients)
                {
                    var socketAsyncEventArgs = _socketAsyncEventArgsSendPool.Pop();
                    socketAsyncEventArgs.SetBuffer(messageWithControlBytes, 0, messageWithControlBytes.Length);

                    // Post send on the listening socket
                    if (!TryUnsafeSocketOperation(client.Socket, SocketAsyncOperation.Send, socketAsyncEventArgs))
                    {
                        // Mark for disconnection
                        if (bustedClients == null)
                        {
                            bustedClients = new List<ConnectedClient>();
                        }

                        bustedClients.Add(client);
                    }
                }
            }
            finally
            {
                _currentlyConnectedClientsLock.ExitReadLock();
            }

            if (bustedClients != null)
            {
                foreach (var client in bustedClients)
                {
                    HandleCommunicationError(client.Socket, new Exception("Broadcast Send failed"), client.Guid );
                }
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

            var messageWithControlBytes = ProtocolHelper.AppendControlBytesToMessage(message, receivedMessage.ThreadId);

            var socketAsyncEventArgs = _socketAsyncEventArgsSendPool.Pop();
            socketAsyncEventArgs.SetBuffer(messageWithControlBytes, 0, messageWithControlBytes.Length);

            // Do the send to the appropriate client
            TryUnsafeSocketOperation(receivedMessage.Socket, SocketAsyncOperation.Send, socketAsyncEventArgs);
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

            // Dump all clients
            var clientList = new List<ConnectedClient>();
            _currentlyConnectedClientsLock.EnterWriteLock();
            try
            {
                clientList = new List<ConnectedClient>(_currentlyConnectedClients);
            }
            finally
            {
                _currentlyConnectedClientsLock.ExitWriteLock();
            }

            foreach (var client in clientList)
            {
                HandleCommunicationError(client.Socket, new Exception("Host is shutting down"), client.Guid);
            }

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
        public event EventHandler<ClientConnectedArgs> ClientConnected;

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

        private void KeepAlive()
        {
            List<ConnectedClient> bustedClients = null;

            while (true)
            {
                Thread.Sleep(1000);

                if (_currentlyConnectedClients.Count == 0)
                {
                    continue;
                }

                bustedClients = null;

                // Do the keep-alive
                _currentlyConnectedClientsLock.EnterReadLock();
                try
                {
                    foreach (var client in _currentlyConnectedClients)
                    {
                        var socketAsyncEventArgs = _socketAsyncEventArgsKeepAlivePool.Pop();

                        // Post send on the socket and confirm that we've heard from the client recently
                        if ((DateTime.UtcNow - client.LastResponse).TotalMilliseconds > _communicationTimeout
                            || !TryUnsafeSocketOperation(client.Socket, SocketAsyncOperation.Send, socketAsyncEventArgs))
                        {
                            // Mark for disconnection
                            if (bustedClients == null)
                            {
                                bustedClients = new List<ConnectedClient>();
                            }
                            
                            bustedClients.Add(client);
                        }
                    }
                }
                finally
                {
                    _currentlyConnectedClientsLock.ExitReadLock();
                }

                if (bustedClients != null)
                {
                    foreach (var client in bustedClients)
                    {
                        HandleCommunicationError(client.Socket, new Exception("Keep alive failed for a connected client"), client.Guid);
                    }
                }
            }
        }

        private void OperationCallback(object sender, SocketAsyncEventArgs socketAsyncEventArgs)
        {
            switch (socketAsyncEventArgs.LastOperation)
            {
                case SocketAsyncOperation.Receive:
                    ReceiveCallback((Socket)sender, socketAsyncEventArgs);
                    break;
                case SocketAsyncOperation.Send:
                    SendCallback((Socket)sender, socketAsyncEventArgs);
                    break;
                case SocketAsyncOperation.Accept:
                    AcceptCallback((Socket)sender, socketAsyncEventArgs);
                    break;
                default:
                    throw new InvalidOperationException("Unknown case called, should program something for this");
            }
        }

        private void AcceptCallback(Socket socket, SocketAsyncEventArgs socketAsyncEventArgs)
        {
            // If our socket is disposed, stop
            if (socketAsyncEventArgs.SocketError == SocketError.OperationAborted)
            {
                return;
            }
            else if (socketAsyncEventArgs.SocketError != SocketError.Success)
            {
                HandleCommunicationError(socket, new Exception("Accept failed, error = " + socketAsyncEventArgs.SocketError), Guid.Empty );
            }

            var handler = socketAsyncEventArgs.AcceptSocket;
            socketAsyncEventArgs.AcceptSocket = null;

            // Post accept on the listening socket
            if (!TryUnsafeSocketOperation(socket, SocketAsyncOperation.Accept, socketAsyncEventArgs))
            {
                throw new Exception("Socket accept failed");
            }

            try
            {
                // Turn on or off Nagle algorithm
                handler.NoDelay = !_useNagleAlgorithm;
                // Set the linger state
                handler.LingerState = _lingerOption;
            }
            catch (SocketException ex)
            {
                HandleCommunicationError(handler, ex, Guid.Empty);
                return;
            }
            catch (ObjectDisposedException)
            {
                // If disposed, handle communication error was already done and we're just catching up on other threads. suppress it.
                return;
            }

            // Enroll in currently connected client sockets
            var connectedClient = new ConnectedClient(handler, Guid.NewGuid());
            _currentlyConnectedClientsLock.EnterWriteLock();
            try
            {
                _currentlyConnectedClients.Add(connectedClient);
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
                clientConnected(this, new ClientConnectedArgs(connectedClient.Guid));
            }

            // Create receive buffer queue for this client
            _currentlyConnectedClientsReceiveQueuesLock.EnterWriteLock();
            try
            {
                _currentlyConnectedClientsReceiveQueues.Add(handler, new BlockingQueue<SocketAsyncEventArgs>());
            }
            finally
            {
                _currentlyConnectedClientsReceiveQueuesLock.ExitWriteLock();
            }

            if (!TryUnsafeSocketOperation(handler, SocketAsyncOperation.Receive, _socketAsyncEventArgsReceivePool.Pop()))
            {
                return;
            }

            ProcessReceivedMessage(connectedClient);
        }

        private void SendCallback(Socket socket, SocketAsyncEventArgs socketAsyncEventArgs)
        {
            // Check for error
            if (socketAsyncEventArgs.SocketError != SocketError.Success)
            {
                HandleCommunicationError(socket, new Exception("Send failed, error = " + socketAsyncEventArgs.SocketError), Guid.Empty);
            }

            if (socketAsyncEventArgs.Buffer.Length == ProtocolHelper.ControlBytesPlaceholder.Length)
            {
                _socketAsyncEventArgsKeepAlivePool.Push(socketAsyncEventArgs);
                return;
            }

            _socketAsyncEventArgsSendPool.Push(socketAsyncEventArgs);
        }

        private void ReceiveCallback(Socket socket, SocketAsyncEventArgs socketAsyncEventArgs)
        {
            // Check for error
            if (socketAsyncEventArgs.SocketError != SocketError.Success)
            {
                HandleCommunicationError(socket, new Exception("Receive failed, error = " + socketAsyncEventArgs.SocketError), _currentlyConnectedClients.FirstOrDefault(f=>f.Socket == socket)?.Guid  );
            }

            // Get the message state
            int bytesRead = socketAsyncEventArgs.BytesTransferred;

            // Read the data
            if (bytesRead > 0)
            {
                // Add to receive queue
                BlockingQueue<SocketAsyncEventArgs> receiveBufferQueue = null;
                _currentlyConnectedClientsReceiveQueuesLock.EnterReadLock();
                try
                {
                    if (!_currentlyConnectedClientsReceiveQueues.TryGetValue(socket, out receiveBufferQueue))
                    {
                        // Peace out!
                        return;
                    }
                }
                finally
                {
                    _currentlyConnectedClientsReceiveQueuesLock.ExitReadLock();
                }

                receiveBufferQueue.Enqueue(socketAsyncEventArgs);
            }
            else
            {
                // 0 bytes means disconnect
                HandleCommunicationError(socket, new Exception("Received 0 bytes (graceful disconnect)"), _currentlyConnectedClients.FirstOrDefault(f => f.Socket == socket)?.Guid);

                _socketAsyncEventArgsReceivePool.Push(socketAsyncEventArgs);
                return;
            }

            socketAsyncEventArgs = _socketAsyncEventArgsReceivePool.Pop();

            // Post a receive to the socket as the client will be continuously receiving messages to be pushed to the queue
            TryUnsafeSocketOperation(socket, SocketAsyncOperation.Receive, socketAsyncEventArgs);
        }

        private void ProcessReceivedMessage(ConnectedClient connectedClient)
        {
            int bytesToRead = -1;
            int threadId = -1;

            int availableTest = 0;
            int controlBytesOffset = 0;
            byte[] protocolBuffer = new byte[ProtocolHelper.ControlBytesPlaceholder.Length];
            byte[] resultBuffer = null;

            var handler = connectedClient.Socket;

            BlockingQueue<SocketAsyncEventArgs> receiveBufferQueue = null;
            _currentlyConnectedClientsReceiveQueuesLock.EnterReadLock();
            try
            {
                if (!_currentlyConnectedClientsReceiveQueues.TryGetValue(handler, out receiveBufferQueue))
                {
                    // Peace out!
                    return;
                }
            }
            finally
            {
                _currentlyConnectedClientsReceiveQueuesLock.ExitReadLock();
            }

            // Loop until socket is done
            while (_isListening)
            {
                // If the socket is disposed, we're done
                try
                {
                    availableTest = handler.Available;
                }
                catch (ObjectDisposedException)
                {
                    // Peace out!
                    return;
                }

                // Get the next buffer from the queue
                var socketAsyncEventArgs = receiveBufferQueue.Dequeue();
                if (socketAsyncEventArgs == null)
                {
                    continue;
                }

                var buffer = socketAsyncEventArgs.Buffer;
                int bytesRead = socketAsyncEventArgs.BytesTransferred;

                int currentOffset = 0;

                while (currentOffset < bytesRead)
                {
                    // Check if we need to get our control byte values
                    if (bytesToRead == -1)
                    {
                        var controlBytesNeeded = ProtocolHelper.ControlBytesPlaceholder.Length - controlBytesOffset;
                        var controlBytesAvailable = bytesRead - currentOffset;

                        var controlBytesToCopy = Math.Min(controlBytesNeeded, controlBytesAvailable);

                        // Copy bytes to control buffer
                        Buffer.BlockCopy(buffer, currentOffset, protocolBuffer, controlBytesOffset, controlBytesToCopy);

                        controlBytesOffset += controlBytesToCopy;
                        currentOffset += controlBytesToCopy;

                        // Check if done
                        if (controlBytesOffset == ProtocolHelper.ControlBytesPlaceholder.Length)
                        {
                            // Parse out control bytes
                            ProtocolHelper.ExtractControlBytes(protocolBuffer, out bytesToRead, out threadId);

                            // Reset control bytes offset
                            controlBytesOffset = 0;

                            // Ensure message is not larger than maximum message size
                            if (bytesToRead > _maxMessageSize)
                            {
                                HandleCommunicationError(handler, new InvalidOperationException(string.Format("message of length {0} exceeds maximum message length of {1}", bytesToRead, _maxMessageSize)), _currentlyConnectedClients.FirstOrDefault(f => f.Socket == _socket)?.Guid);
                                return;
                            }
                        }

                        // Continue the loop
                        continue;
                    }

                    // Have control bytes, get message bytes

                    // SPECIAL CASE: if empty message, skip a bunch of stuff
                    if (bytesToRead != 0)
                    {
                        // Initialize buffer if needed
                        if (resultBuffer == null)
                        {
                            resultBuffer = new byte[bytesToRead];
                        }

                        var bytesAvailable = bytesRead - currentOffset;

                        var bytesToCopy = Math.Min(bytesToRead, bytesAvailable);

                        // Copy bytes to buffer
                        Buffer.BlockCopy(buffer, currentOffset, resultBuffer, resultBuffer.Length - bytesToRead, bytesToCopy);

                        currentOffset += bytesToCopy;
                        bytesToRead -= bytesToCopy;
                    }

                    // Check if we're done
                    if (bytesToRead == 0)
                    {
                        if (resultBuffer != null)
                        {
                            // Done, add to complete received messages
                            CompleteMessage(handler, threadId, resultBuffer, connectedClient.Guid);
                            
                            // Reset message state
                            resultBuffer = null;
                        }

                        bytesToRead = -1;
                        threadId = -1;

                        connectedClient.LastResponse = DateTime.UtcNow;
                    }
                }

                // Push the buffer back onto the pool
                _socketAsyncEventArgsReceivePool.Push(socketAsyncEventArgs);
            }
        }
        private void SignalMultiplexer(int threadId)
        {
            MultiplexerData multiplexerData = null;

            _clientMultiplexerLock.EnterReadLock();
            try
            {
                if (!_clientMultiplexer.TryGetValue(threadId, out multiplexerData))
                {
                    // Nothing to do
                    return;
                }
            }
            finally
            {
                _clientMultiplexerLock.ExitReadLock();
            }

            multiplexerData.ManualResetEvent.Set();
        }
        private void CompleteMessage(Socket handler, int threadId, byte[] message, Guid guid)
        {

            var multiplexerData = GetMultiplexerData(threadId);
            if (multiplexerData != null)
            {
                multiplexerData.Message = message;
                SignalMultiplexer(threadId);
                return;
            }

            var receivedMessage = _receivedMessagePool.Pop();
            receivedMessage.Socket = handler;
            receivedMessage.ThreadId = threadId;
            receivedMessage.Message = message;
            receivedMessage.Guid = guid;

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
        /// Handles an error in socket communication.
        /// </summary>
        /// <param name="socket">The socket.</param>
        /// <param name="ex">The exception that the socket raised.</param>
        private void HandleCommunicationError(Socket socket, Exception ex, Guid? ConnectionGuid)
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

            // Remove receive buffer queue
            _currentlyConnectedClientsReceiveQueuesLock.EnterWriteLock();
            try
            {
                _currentlyConnectedClientsReceiveQueues.Remove(socket);
            }
            finally
            {
                _currentlyConnectedClientsReceiveQueuesLock.ExitWriteLock();
            }

            // Try to unenroll from currently connected client sockets
            _currentlyConnectedClientsLock.EnterWriteLock();
            try
            {
                for (var i = 0; i < _currentlyConnectedClients.Count; i++)
                {
                    var connectedClient = _currentlyConnectedClients[i];
                    if (connectedClient.Socket == socket)
                    {
                        _currentlyConnectedClients.RemoveAt(i);
                        break;
                    }
                }
            }
            finally
            {
                _currentlyConnectedClientsLock.ExitWriteLock();
            }

            // Raise the error event 
            var error = Error;
            if (error != null)
            {
                var socketErrorArgs = _socketErrorArgsPool.Pop();
                socketErrorArgs.Exception = ex;
                socketErrorArgs.ConnectionGuid = ConnectionGuid;
                error(this, socketErrorArgs);
                _socketErrorArgsPool.Push(socketErrorArgs);
            }
        }

        private bool TryUnsafeSocketOperation(Socket socket, SocketAsyncOperation operation, SocketAsyncEventArgs socketAsyncEventArgs)
        {
            try
            {
                bool result = false;
                switch (operation)
                {
                    case SocketAsyncOperation.Accept:
                        result = socket.AcceptAsync(socketAsyncEventArgs);
                        break;
                    case SocketAsyncOperation.Send:
                        result = socket.SendAsync(socketAsyncEventArgs);
                        break;
                    case SocketAsyncOperation.Receive:
                        result = socket.ReceiveAsync(socketAsyncEventArgs);
                        break;
                    default:
                        throw new InvalidOperationException("Unknown case called, should program something for this");
                }

                if (!result)
                {
                    OperationCallback(socket, socketAsyncEventArgs);
                }
            }
            catch (SocketException ex)
            {
                if (operation != SocketAsyncOperation.Accept)
                {
                    HandleCommunicationError(socket, ex, _currentlyConnectedClients.FirstOrDefault(f => f.Socket == socket)?.Guid);
                }
                return false;
            }
            catch (ObjectDisposedException)
            {
                // If disposed, handle communication error was already done and we're just catching up on other threads. suppress it.
                return false;
            }

            return true;
        }

        private class ConnectedClient
        {
            public ConnectedClient(Socket socket, Guid guid)
            {
                if (socket == null) throw new ArgumentNullException("socket");
                if (guid == Guid.Empty) throw new ArgumentNullException("guid");
                Socket = socket;
                Guid = guid;
                LastResponse = DateTime.UtcNow;
            }
            public Guid Guid { get; set; }
            public Socket Socket { get; private set; }

            public DateTime LastResponse { get; set; }
        }
    }
}