using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Runtime.Caching;
using System.Threading;
using System.Threading.Tasks;

namespace SimplSockets
{
    /// <summary>
    /// Wraps sockets and provides intuitive, extremely efficient, scalable methods for client-server communication.
    /// </summary>
    public class SimplSocketClient : ISimplSocketClient
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

        // The receive buffer queue
        private readonly BlockingQueue<SocketAsyncEventArgs> _receiveBufferQueue = null;

        // Whether or not a connection currently exists
        private volatile bool _isConnected = false;
        private readonly object _isConnectedLock = new object();

        // The client multiplexer
        private readonly Dictionary<int, MultiplexerData> _clientMultiplexer = null;
        private readonly ReaderWriterLockSlim _clientMultiplexerLock = new ReaderWriterLockSlim();

        // Various pools
        private readonly Pool<MultiplexerData> _multiplexerDataPool = null;
        private readonly Pool<SocketAsyncEventArgs> _socketAsyncEventArgsPool = null;
        private readonly Pool<ReceivedMessage> _receivedMessagePool = null;
        private readonly Pool<MessageReceivedArgs> _messageReceivedArgsPool = null;
        private readonly Pool<SocketErrorArgs> _socketErrorArgsPool = null;

        // The control bytes placeholder - the first 4 bytes are little endian message length, the last 4 are thread id
        private static readonly byte[] _controlBytesPlaceholder = new byte[] { 0, 0, 0, 0, 0, 0, 0, 0 };

        // A completely blind guess at the number of expected threads on the multiplexer. 100 sounds good, right? Right.
        private const int PREDICTED_THREAD_COUNT = 100;

        /// <summary>
        /// The constructor.
        /// </summary>
        /// <param name="socketFunc">The function that creates a new socket. Use this to specify your socket constructor and initialize settings.</param>
        /// <param name="messageBufferSize">The message buffer size to use for send/receive.</param>
        /// <param name="communicationTimeout">The communication timeout, in milliseconds.</param>
        /// <param name="maxMessageSize">The maximum message size.</param>
        /// <param name="useNagleAlgorithm">Whether or not to use the Nagle algorithm.</param>
        public SimplSocketClient(Func<Socket> socketFunc, int messageBufferSize = 4096, int communicationTimeout = 10000, int maxMessageSize = 100 * 1024 * 1024, bool useNagleAlgorithm = false)
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

            _receiveBufferQueue = new BlockingQueue<SocketAsyncEventArgs>(PREDICTED_THREAD_COUNT);

            // Initialize the client multiplexer
            _clientMultiplexer = new Dictionary<int, MultiplexerData>(PREDICTED_THREAD_COUNT);

            // Create the pools
            _multiplexerDataPool = new Pool<MultiplexerData>(PREDICTED_THREAD_COUNT, () => new MultiplexerData { ManualResetEvent = new ManualResetEvent(false) }, multiplexerData => 
            {
                multiplexerData.Message = null;
                multiplexerData.ManualResetEvent.Reset();
            });
            _socketAsyncEventArgsPool = new Pool<SocketAsyncEventArgs>(PREDICTED_THREAD_COUNT, () =>
            {
                var poolItem = new SocketAsyncEventArgs();
                poolItem.SetBuffer(new byte[messageBufferSize], 0, messageBufferSize);
                poolItem.Completed += OperationCallback;
                return poolItem;
            });
            _receivedMessagePool = new Pool<ReceivedMessage>(PREDICTED_THREAD_COUNT, () => new ReceivedMessage(), receivedMessage =>
            {
                receivedMessage.Message = null;
                receivedMessage.Socket = null;
            });
            _messageReceivedArgsPool = new Pool<MessageReceivedArgs>(PREDICTED_THREAD_COUNT, () => new MessageReceivedArgs(), messageReceivedArgs => { messageReceivedArgs.ReceivedMessage = null; });
            _socketErrorArgsPool = new Pool<SocketErrorArgs>(PREDICTED_THREAD_COUNT, () => new SocketErrorArgs(), socketErrorArgs => { socketErrorArgs.Exception = null; });
        }

        /// <summary>
        /// Connects to an endpoint. Once this is called, you must call Close before calling Connect again.
        /// </summary>
        /// <param name="endPoint">The endpoint.</param>
        /// <returns>true if connection is successful, false otherwise.</returns>
        public bool Connect(EndPoint endPoint)
        {
            // Sanitize
            if (endPoint == null)
            {
                throw new ArgumentNullException("endPoint");
            }

            lock (_isConnectedLock)
            {
                if (_isConnected)
                {
                    throw new InvalidOperationException("socket is already in use");
                }

                // Create socket
                _socket = _socketFunc();
                // Turn on or off Nagle algorithm
                _socket.NoDelay = !_useNagleAlgorithm;
                // Set the linger state
                _socket.LingerState = _lingerOption;

                // Post a connect to the socket synchronously
                try
                {
                    _socket.Connect(endPoint);
                }
                catch (SocketException ex)
                {
                    HandleCommunicationError(_socket, ex);
                    return false;
                }
                catch (ObjectDisposedException)
                {
                    // If disposed, handle communication error was already done and we're just catching up on other threads. suppress it.
                    return false;
                }

                var socketAsyncEventArgs = _socketAsyncEventArgsPool.Pop();

                // Post a receive to the socket as the client will be continuously receiving messages to be pushed to the queue
                try
                {
                    if (!_socket.ReceiveAsync(socketAsyncEventArgs))
                    {
                        OperationCallback(_socket, socketAsyncEventArgs);
                    }
                }
                catch (SocketException ex)
                {
                    HandleCommunicationError(_socket, ex);
                    return false;
                }
                catch (ObjectDisposedException)
                {
                    // If disposed, handle communication error was already done and we're just catching up on other threads. suppress it.
                    return false;
                }

                // Spin up the keep-alive
                Task.Factory.StartNew(() => KeepAlive(_socket));

                // Process all incoming messages on a separate thread
                Task.Factory.StartNew(() => ProcessReceivedMessage(_socket));

                _isConnected = true;
            }

            return true;
        }

        /// <summary>
        /// Sends a message to the server without waiting for a response (one-way communication).
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

            var socketAsyncEventArgs = _socketAsyncEventArgsPool.Pop();

            // Copy message over
            if (messageWithControlBytes.Length > socketAsyncEventArgs.Buffer.Length)
            {
                socketAsyncEventArgs.SetBuffer(messageWithControlBytes, 0, messageWithControlBytes.Length);
            }
            else
            {
                Buffer.BlockCopy(messageWithControlBytes, 0, socketAsyncEventArgs.Buffer, 0, messageWithControlBytes.Length);
                socketAsyncEventArgs.SetBuffer(0, messageWithControlBytes.Length);
            }

            // Do the send
            try
            {
                if (!_socket.SendAsync(socketAsyncEventArgs))
                {
                    OperationCallback(_socket, socketAsyncEventArgs);
                }
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
        /// Sends a message to the server and then waits for the response to that message.
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

            // Get the current thread ID
            int threadId = Thread.CurrentThread.ManagedThreadId;

            // Enroll in the multiplexer
            var multiplexerData = EnrollMultiplexer(threadId);

            var messageWithControlBytes = AppendControlBytesToMessage(message, threadId);

            var socketAsyncEventArgs = _socketAsyncEventArgsPool.Pop();

            // Copy message over
            if (messageWithControlBytes.Length > socketAsyncEventArgs.Buffer.Length)
            {
                socketAsyncEventArgs.SetBuffer(messageWithControlBytes, 0, messageWithControlBytes.Length);
            }
            else
            {
                Buffer.BlockCopy(messageWithControlBytes, 0, socketAsyncEventArgs.Buffer, 0, messageWithControlBytes.Length);
                socketAsyncEventArgs.SetBuffer(0, messageWithControlBytes.Length);
            }

            // Do the send
            try
            {
                if (!_socket.SendAsync(socketAsyncEventArgs))
                {
                    OperationCallback(_socket, socketAsyncEventArgs);
                }
            }
            catch (SocketException ex)
            {
                HandleCommunicationError(_socket, ex);
                return null;
            }
            catch (ObjectDisposedException)
            {
                // If disposed, handle communication error was already done and we're just catching up on other threads. suppress it.
                return null;
            }

            // Wait for our message to go ahead from the receive callback, or until the timeout is reached
            if (!multiplexerData.ManualResetEvent.WaitOne(_communicationTimeout))
            {
                HandleCommunicationError(_socket, new TimeoutException("The connection timed out before the response message was received"));

                // Reset the multiplexer
                multiplexerData.ManualResetEvent.Reset();

                // No signal
                return null;
            }

            // Reset the multiplexer
            multiplexerData.ManualResetEvent.Reset();

            // Now get the command string
            var result = multiplexerData.Message;

            return result;
        }

        /// <summary>
        /// Closes the connection. Once this is called, you can call Connect again to start a new client connection.
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
            _isConnected = false;
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

        private void KeepAlive(Socket socket)
        {
            socket.SendTimeout = _communicationTimeout;

            while (true)
            {
                // Do the keep-alive
                try
                {
                    socket.Send(_controlBytesPlaceholder, 0, _controlBytesPlaceholder.Length, 0);
                }
                catch (SocketException)
                {
                    var remoteIpEndPoint = socket.RemoteEndPoint as IPEndPoint;
                    var ipAddress = remoteIpEndPoint != null ? remoteIpEndPoint.Address.ToString() : "<IP could not be resolved>";

                    HandleCommunicationError(socket, new Exception(string.Format("Keep Alive send to {0} failed", ipAddress)));
                    break;
                }
                catch (ObjectDisposedException)
                {
                    // If disposed, handle communication error was already done and we're just catching up on other threads. suppress it.
                    break;
                }

                Thread.Sleep(1000);
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
                default:
                    throw new InvalidOperationException("Unknown case called, should program something for this");
            }
        }

        private void SendCallback(Socket socket, SocketAsyncEventArgs socketAsyncEventArgs)
        {
            // Check for error
            if (socketAsyncEventArgs.SocketError != SocketError.Success)
            {
                var remoteIpEndPoint = socket.RemoteEndPoint as IPEndPoint;
                var ipAddress = remoteIpEndPoint != null ? remoteIpEndPoint.Address.ToString() : "<IP could not be resolved>";

                HandleCommunicationError(socket, new Exception(string.Format("Send to {0} failed", ipAddress)));
            }

            _socketAsyncEventArgsPool.Push(socketAsyncEventArgs);
        }

        private void ReceiveCallback(Socket socket, SocketAsyncEventArgs socketAsyncEventArgs)
        {
            // Check for error
            if (socketAsyncEventArgs.SocketError != SocketError.Success)
            {
                var remoteIpEndPoint = socket.RemoteEndPoint as IPEndPoint;
                var ipAddress = remoteIpEndPoint != null ? remoteIpEndPoint.Address.ToString() : "<IP could not be resolved>";

                HandleCommunicationError(socket, new Exception(string.Format("Receive from {0} failed", ipAddress)));
            }

            // Get the message state
            int bytesRead = socketAsyncEventArgs.BytesTransferred;

            // Read the data
            if (bytesRead > 0)
            {
                // Add to receive queue
                _receiveBufferQueue.Enqueue(socketAsyncEventArgs);

                socketAsyncEventArgs = _socketAsyncEventArgsPool.Pop();

                // Post a receive to the socket as the client will be continuously receiving messages to be pushed to the queue
                try
                {
                    if (!_socket.ReceiveAsync(socketAsyncEventArgs))
                    {
                        OperationCallback(_socket, socketAsyncEventArgs);
                    }
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
        }

        private void ProcessReceivedMessage(Socket handler)
        {
            int bytesToRead = -1;
            int threadId = -1;

            int controlBytesOffset = 0;
            byte[] protocolBuffer = new byte[_controlBytesPlaceholder.Length];
            byte[] resultBuffer = null;

            // Loop until socket is done
            while (_isConnected)
            {
                // Get the next buffer from the queue
                var socketAsyncEventArgs = _receiveBufferQueue.Dequeue();
                var buffer = socketAsyncEventArgs.Buffer;
                int bytesRead = socketAsyncEventArgs.BytesTransferred;

                int currentOffset = 0;

                while (currentOffset < bytesRead)
                {
                    // Check if we need to get our control byte values
                    if (bytesToRead == -1)
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
                            ExtractControlBytes(protocolBuffer, out bytesToRead, out threadId);

                            // Reset control bytes offset
                            controlBytesOffset = 0;

                            // Ensure message is not larger than maximum message size
                            if (bytesToRead > _maxMessageSize)
                            {
                                HandleCommunicationError(handler, new InvalidOperationException(string.Format("message of length {0} exceeds maximum message length of {1}", bytesToRead, _maxMessageSize)));
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
                            CompleteMessage(handler, threadId, resultBuffer);
                        }

                        // Reset message state
                        resultBuffer = null;
                        bytesToRead = -1;
                        threadId = -1;
                    }
                }

                _socketAsyncEventArgsPool.Push(socketAsyncEventArgs);
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

            // No longer connected
            _isConnected = false;

            // Clear receive queue for this client
            _receiveBufferQueue.Clear();

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

        private MultiplexerData EnrollMultiplexer(int threadId)
        {
            MultiplexerData multiplexerData = null;

            _clientMultiplexerLock.EnterReadLock();
            try
            {
                if (_clientMultiplexer.TryGetValue(threadId, out multiplexerData))
                {
                    return multiplexerData;
                }
            }
            finally
            {
                _clientMultiplexerLock.ExitReadLock();
            }

            multiplexerData = _multiplexerDataPool.Pop();

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
    }
}
