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
    public class SimplSocket : ISimplSocket
    {
        // The function that creates a new socket
        private readonly Func<Socket> _socketFunc = null;
        // The currently used socket
        private Socket _socket = null;
        // The message buffer size to use for send/receive
        private readonly int _messageBufferSize = 0;
        // The maximum connections to allow to use the socket simultaneously
        private readonly int _maximumConnections = 0;
        // The semaphore that enforces the maximum numbers of simultaneous connections
        private readonly Semaphore _maxConnectionsSemaphore = null;
        // Whether or not to use the Nagle algorithm
        private readonly bool _useNagleAlgorithm = false;

        // Whether or not the socket is a server
        private bool _isServer = false;

        // The receive buffer queue
        private readonly Dictionary<int, BlockingQueue<KeyValuePair<byte[], int>>> _receiveBufferQueue = null;
        // The receive buffer queue lock
        private readonly ReaderWriterLockSlim _receiveBufferQueueLock = new ReaderWriterLockSlim();

        // The currently connected clients
        private readonly List<Socket> _currentlyConnectedClients = null;
        // The currently connected clients lock
        private readonly ReaderWriterLockSlim _currentlyConnectedClientsLock = new ReaderWriterLockSlim();
        // The number of currently connected clients
        private int _currentlyConnectedClientCount = 0;
        // Whether or not a connection currently exists
        private volatile bool _isDoingSomething = false;

        // The client multiplexer
        private readonly Dictionary<int, MultiplexerData> _clientMultiplexer = null;
        // The client multiplexer reader writer lock
        private readonly ReaderWriterLockSlim _clientMultiplexerLock = new ReaderWriterLockSlim();
        // The pool of manual reset events
        private readonly Pool<ManualResetEvent> _manualResetEventPool = null;
        // The pool of message states
        private readonly Pool<MessageState> _messageStatePool = null;
        // The pool of buffers
        private readonly Pool<byte[]> _bufferPool = null;
        // The pool of received messages
        private readonly Pool<ReceivedMessage> _receivedMessagePool = null;

        // The control bytes placeholder - the first 4 bytes are little endian message length, the last 4 are thread id
        private static readonly byte[] _controlBytesPlaceholder = new byte[] { 0, 0, 0, 0, 0, 0, 0, 0 };

        /// <summary>
        /// The constructor.
        /// </summary>
        /// <param name="socketFunc">The function that creates a new socket. Use this to specify your socket constructor and initialize settings.</param>
        /// <param name="messageBufferSize">The message buffer size to use for send/receive.</param>
        /// <param name="maximumConnections">The maximum connections to allow to use the socket simultaneously.</param>
        /// <param name="useNagleAlgorithm">Whether or not to use the Nagle algorithm.</param>
        public SimplSocket(Func<Socket> socketFunc, int messageBufferSize, int maximumConnections, bool useNagleAlgorithm)
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
            if (maximumConnections <= 0)
            {
                throw new ArgumentException("must be > 0", "maximumConnections");
            }

            _socketFunc = socketFunc;
            _messageBufferSize = messageBufferSize;
            _maximumConnections = maximumConnections;
            _maxConnectionsSemaphore = new Semaphore(maximumConnections, maximumConnections);
            _useNagleAlgorithm = useNagleAlgorithm;

            _currentlyConnectedClients = new List<Socket>(maximumConnections);

            _receiveBufferQueue = new Dictionary<int, BlockingQueue<KeyValuePair<byte[], int>>>(maximumConnections);

            // Initialize the client multiplexer
            _clientMultiplexer = new Dictionary<int, MultiplexerData>(64);

            // Create the pools
            _messageStatePool = new Pool<MessageState>(maximumConnections, () => new MessageState(), messageState =>
            {
                messageState.Buffer = null;
                messageState.Handler = null;
                messageState.ThreadId = -1;
                messageState.BytesToRead = -1;
            });
            _manualResetEventPool = new Pool<ManualResetEvent>(maximumConnections, () => new ManualResetEvent(false), manualResetEvent => manualResetEvent.Reset());
            _bufferPool = new Pool<byte[]>(maximumConnections, () => new byte[messageBufferSize], null);
            _receivedMessagePool = new Pool<ReceivedMessage>(maximumConnections, () => new ReceivedMessage(), receivedMessage =>
            {
                receivedMessage.Message = null;
                receivedMessage.Socket = null;
            });

            // Populate the pools at 20%
            for (int i = 0; i < maximumConnections / 5; i++)
            {
                _messageStatePool.Push(new MessageState());
                _manualResetEventPool.Push(new ManualResetEvent(false));
                _bufferPool.Push(new byte[messageBufferSize]);
                _receivedMessagePool.Push(new ReceivedMessage());
            }
        }

        /// <summary>
        /// Gets the currently connected client count.
        /// </summary>
        public int CurrentlyConnectedClientCount
        {
            get
            {
                return _currentlyConnectedClientCount;
            }
        }

        /// <summary>
        /// Connects to an endpoint. Once this is called, you must call Close before calling Connect or Listen again. The errorHandler method 
        /// will not be called if the connection fails. Instead this method will return false.
        /// </summary>
        /// <param name="endPoint">The endpoint.</param>
        /// <returns>true if connection is successful, false otherwise.</returns>
        public bool Connect(EndPoint endPoint)
        {
            // Sanitize
            if (_isDoingSomething)
            {
                throw new InvalidOperationException("socket is already in use");
            }
            if (endPoint == null)
            {
                throw new ArgumentNullException("endPoint");
            }

            _isDoingSomething = true;

            // Create socket
            _socket = _socketFunc();
            // Turn on or off Nagle algorithm
            _socket.NoDelay = !_useNagleAlgorithm;

            // Do not proceed until we have room to do so
            _maxConnectionsSemaphore.WaitOne();

            Interlocked.Increment(ref _currentlyConnectedClientCount);

            // Post a connect to the socket synchronously
            try
            {
                _socket.Connect(endPoint);
            }
            catch (SocketException)
            {
                _isDoingSomething = false;
                return false;
            }

            // Get a message state from the pool
            var messageState = _messageStatePool.Pop();
            messageState.Handler = _socket;
            messageState.Buffer = _bufferPool.Pop();

            // Create receive buffer queue for this client
            var receiveBufferQueue = new BlockingQueue<KeyValuePair<byte[], int>>(64);
            _receiveBufferQueueLock.EnterWriteLock();
            try
            {
                _receiveBufferQueue[messageState.Handler.GetHashCode()] = receiveBufferQueue;
            }
            finally
            {
                _receiveBufferQueueLock.ExitWriteLock();
            }

            // Post a receive to the socket as the client will be continuously receiving messages to be pushed to the queue
            _socket.BeginReceive(messageState.Buffer, 0, messageState.Buffer.Length, 0, ReceiveCallback, messageState);

            // Process all incoming messages
            var processMessageState = _messageStatePool.Pop();
            processMessageState.Handler = _socket;

            Task.Factory.StartNew(() => ProcessReceivedMessage(processMessageState, receiveBufferQueue));

            return true;
        }

        /// <summary>
        /// Begin listening for incoming connections. Once this is called, you must call Close before calling Connect or Listen again.
        /// </summary>
        /// <param name="localEndpoint">The local endpoint to listen on.</param>
        public void Listen(EndPoint localEndpoint)
        {
            // Sanitize
            if (_isDoingSomething)
            {
                throw new InvalidOperationException("socket is already in use");
            }
            if (localEndpoint == null)
            {
                throw new ArgumentNullException("localEndpoint");
            }

            _isDoingSomething = true;
            _isServer = true;

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
                // If disposed, handle communication error was already done and we're just catching up on other threads. Supress it.
            }
        }

        /// <summary>
        /// Closes the connection. Once this is called, you can call Connect or Listen again to start a new socket connection.
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
            // No longer doing something
            _isDoingSomething = false;
            _isServer = false;
        }

        /// <summary>
        /// Sends a message to the other end without waiting for a response (one-way communication).
        /// NOTE: when the server calls this method, it will broadcast to all clients.
        /// </summary>
        /// <param name="message">The message to send.</param>
        public void Send(byte[] message)
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
            try
            {
                // Check if client send
                if (!_isServer)
                {
                    _socket.BeginSend(messageWithControlBytes, 0, messageWithControlBytes.Length, 0, SendCallback, _socket);
                    return;
                }

                // Server send
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
                        }
                    }
                }
                finally
                {
                    _currentlyConnectedClientsLock.ExitReadLock();
                }
            }
            catch (SocketException ex)
            {
                HandleCommunicationError(_socket, ex);
            }
            catch (ObjectDisposedException)
            {
                // If disposed, handle communication error was already done and we're just catching up on other threads. Supress it.
            }
        }

        /// <summary>
        /// Sends a message to the server and receives the response to that message.
        /// NOTE: a server cannot call this method, it will result in an exception.
        /// </summary>
        /// <param name="message">The message to send.</param>
        /// <returns>The response message.</returns>
        public byte[] SendReceive(byte[] message)
        {
            // Sanitize
            if (message == null)
            {
                throw new ArgumentNullException("message");
            }
            if (_isServer)
            {
                throw new InvalidOperationException("server cannot call this method");
            }

            // Get the current thread ID
            int threadId = Thread.CurrentThread.ManagedThreadId;

            // Enroll in the multiplexer
            var multiplexerData = EnrollMultiplexer(threadId);

            var messageWithControlBytes = AppendControlBytesToMessage(message, threadId);

            // Do the send
            try
            {
                _socket.BeginSend(messageWithControlBytes, 0, messageWithControlBytes.Length, 0, SendCallback, _socket);
            }
            catch (SocketException ex)
            {
                HandleCommunicationError(_socket, ex);
                return null;
            }
            catch (ObjectDisposedException)
            {
                // If disposed, handle communication error was already done and we're just catching up on other threads. Supress it.
                return null;
            }

            // Wait for our message to go ahead from the receive callback
            multiplexerData.ManualResetEvent.WaitOne();

            // Now get the command string
            var result = multiplexerData.Message;

            // Finally remove the thread from the multiplexer
            UnenrollMultiplexer(threadId);

            return result;
        }

        /// <summary>
        /// Sends a reply message back to the original sender.
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
                HandleCommunicationError(receivedMessage.Socket, ex);
                return;
            }
            catch (ObjectDisposedException)
            {
                // If disposed, handle communication error was already done and we're just catching up on other threads. Supress it.
                return;
            }
        }

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
            Interlocked.Increment(ref _currentlyConnectedClientCount);

            // Get the client handler socket
            Socket handler = null;
            try
            {
                handler = _socket.EndAccept(asyncResult);
            }
            catch (SocketException ex)
            {
                HandleCommunicationError(_socket, ex);
                return;
            }
            catch (ObjectDisposedException)
            {
                // If disposed, handle communication error was already done and we're just catching up on other threads. Supress it.
                return;
            }

            // Turn on or off Nagle algorithm
            handler.NoDelay = !_useNagleAlgorithm;

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
                // If disposed, handle communication error was already done and we're just catching up on other threads. Supress it.
                return;
            }

            // Do not proceed until we have room to do so
            _maxConnectionsSemaphore.WaitOne();

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

            // Get message state
            var messageState = _messageStatePool.Pop();
            messageState.Handler = handler;
            messageState.Buffer = _bufferPool.Pop();

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
                // If disposed, handle communication error was already done and we're just catching up on other threads. Supress it.
                return;
            }

            // Create receive buffer queue for this client
            var receiveBufferQueue = new BlockingQueue<KeyValuePair<byte[], int>>(64);
            _receiveBufferQueueLock.EnterWriteLock();
            try
            {
                _receiveBufferQueue[messageState.Handler.GetHashCode()] = receiveBufferQueue;
            }
            finally
            {
                _receiveBufferQueueLock.ExitWriteLock();
            }

            // Process all incoming messages
            var processMessageState = _messageStatePool.Pop();
            processMessageState.Handler = handler;
            ProcessReceivedMessage(processMessageState, receiveBufferQueue);
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
                return;
            }
            catch (ObjectDisposedException)
            {
                // If disposed, handle communication error was already done and we're just catching up on other threads. Supress it.
                return;
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
                // If disposed, handle communication error was already done and we're just catching up on other threads. Supress it.
                return;
            }

            if (bytesRead > 0)
            {
                // Add buffer to queue
                BlockingQueue<KeyValuePair<byte[], int>> queue = null;
                _receiveBufferQueueLock.EnterReadLock();
                try
                {
                    if (!_receiveBufferQueue.TryGetValue(messageState.Handler.GetHashCode(), out queue))
                    {
                        throw new Exception("FATAL: No receive queue created for current socket");
                    }
                }
                finally
                {
                    _receiveBufferQueueLock.ExitReadLock();
                }

                queue.Enqueue(new KeyValuePair<byte[], int>(messageState.Buffer, bytesRead));

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
                    // If disposed, handle communication error was already done and we're just catching up on other threads. Supress it.
                }
            }
        }

        private void ProcessReceivedMessage(MessageState messageState, BlockingQueue<KeyValuePair<byte[], int>> receiveBufferQueue)
        {
            int controlBytesOffset = 0;
            byte[] protocolBuffer = new byte[_controlBytesPlaceholder.Length];

            // Loop until socket is done
            while (_isDoingSomething)
            {
                // Get the next buffer from the queue
                var receiveBufferEntry = receiveBufferQueue.Dequeue();
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
                        }

                        // Continue the loop
                        continue;
                    }

                    // Have control bytes, get message bytes

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

                    // Check if we're done
                    if (messageState.BytesToRead == 0)
                    {
                        // Done, add to complete received messages
                        CompleteMessage(messageState.Handler, messageState.ThreadId, messageState.Buffer);

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
            // Try and signal multiplexer
            var multiplexerData = GetMultiplexerData(threadId);
            if (multiplexerData != null)
            {
                multiplexerData.Message = message;
                SignalMultiplexer(threadId);
                return;
            }

            // No multiplexer
            var receivedMessage = _receivedMessagePool.Pop();
            receivedMessage.Socket = handler;
            receivedMessage.ThreadId = threadId;
            receivedMessage.Message = message;

            // Fire the event if needed
            var messageReceived = MessageReceived;
            if (messageReceived != null)
            {
                // Create the message received args
                var messageReceivedArgs = new MessageReceivedArgs(receivedMessage);
                // Fire the event
                messageReceived(this, messageReceivedArgs);
            }

            // Place received message back in pool
            _receivedMessagePool.Push(receivedMessage);
        }

        /// <summary>
        /// Handles an error in socket communication.
        /// </summary>
        /// <param name="socket">The socket that raised the exception.</param>
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

            // Release all multiplexer clients by signalling them
            _clientMultiplexerLock.EnterReadLock();
            try
            {
                foreach (var multiplexerData in _clientMultiplexer.Values)
                {
                    multiplexerData.ManualResetEvent.Set();
                }
            }
            finally
            {
                _clientMultiplexerLock.ExitReadLock();
            }

            // Unenroll in currently connected client sockets
            _currentlyConnectedClientsLock.EnterWriteLock();
            try
            {
                _currentlyConnectedClients.Remove(socket);
            }
            finally
            {
                _currentlyConnectedClientsLock.ExitWriteLock();
            }

            // Remove receive queue for this client
            _receiveBufferQueueLock.EnterWriteLock();
            try
            {
                _receiveBufferQueue.Remove(socket.GetHashCode());
            }
            finally
            {
                _receiveBufferQueueLock.ExitWriteLock();
            }

            // Decrement the counter keeping track of the total number of clients connected to the server
            Interlocked.Decrement(ref _currentlyConnectedClientCount);

            // Release one from the max connections semaphore
            _maxConnectionsSemaphore.Release();

            // Raise the error event
            var error = Error;
            if (error != null)
            {
                var socketErrorArgs = new SocketErrorArgs(ex.Message);
                error(this, socketErrorArgs);
            }
        }

        private class MessageState
        {
            public byte[] Buffer = null;
            public Socket Handler = null;
            public int ThreadId = -1;
            public int BytesToRead = -1;
        }

        /// <summary>
        /// A received message.
        /// </summary>
        public class ReceivedMessage
        {
            internal Socket Socket;
            internal int ThreadId;

            /// <summary>
            /// The message bytes.
            /// </summary>
            public byte[] Message;
        }

        private class MultiplexerData
        {
            public byte[] Message { get; set; }
            public ManualResetEvent ManualResetEvent { get; set; }
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

        private MultiplexerData GetMultiplexerData(int threadId)
        {
            MultiplexerData multiplexerData = null;
            _clientMultiplexerLock.EnterReadLock();
            try
            {
                // Get from multiplexer by thread ID
                if (!_clientMultiplexer.TryGetValue(threadId, out multiplexerData))
                {
                    return null;
                }

                return multiplexerData;
            }
            finally
            {
                _clientMultiplexerLock.ExitReadLock();
            }
        }

        private void SignalMultiplexer(int threadId)
        {
            MultiplexerData multiplexerData = null;
            _clientMultiplexerLock.EnterReadLock();
            try
            {
                // Get from multiplexer by thread ID
                if (!_clientMultiplexer.TryGetValue(threadId, out multiplexerData))
                {
                    throw new Exception("FATAL: multiplexer was missing entry for Thread ID " + threadId);
                }

                multiplexerData.ManualResetEvent.Set();
            }
            finally
            {
                _clientMultiplexerLock.ExitReadLock();
            }
        }

        private MultiplexerData EnrollMultiplexer(int threadId)
        {
            var multiplexerData = new MultiplexerData { ManualResetEvent = _manualResetEventPool.Pop() };

            _clientMultiplexerLock.EnterWriteLock();
            try
            {
                // Add manual reset event for current thread
                _clientMultiplexer.Add(threadId, multiplexerData);
            }
            catch
            {
                throw new Exception("FATAL: multiplexer tried to add duplicate entry for Thread ID " + threadId);
            }
            finally
            {
                _clientMultiplexerLock.ExitWriteLock();
            }

            return multiplexerData;
        }

        private void UnenrollMultiplexer(int threadId)
        {
            MultiplexerData multiplexerData = null;
            _clientMultiplexerLock.EnterUpgradeableReadLock();
            try
            {
                // Get from multiplexer by thread ID
                if (!_clientMultiplexer.TryGetValue(threadId, out multiplexerData))
                {
                    throw new Exception("FATAL: multiplexer was missing entry for Thread ID " + threadId);
                }

                _clientMultiplexerLock.EnterWriteLock();
                try
                {
                    // Remove entry
                    _clientMultiplexer.Remove(threadId);
                }
                finally
                {
                    _clientMultiplexerLock.ExitWriteLock();
                }
            }
            finally
            {
                _clientMultiplexerLock.ExitUpgradeableReadLock();
            }

            // Now return objects to pools
            _manualResetEventPool.Push(multiplexerData.ManualResetEvent);
        }
    }
}
