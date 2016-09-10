using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Runtime.Caching;
using System.Text.RegularExpressions;
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

        // The send buffer queue
        private readonly BlockingQueue<SocketAsyncEventArgs> _sendBufferQueue = null;
        // The receive buffer queue
        private readonly BlockingQueue<SocketAsyncEventArgs> _receiveBufferQueue = null;
        // The send buffer manual reset event
        private readonly ManualResetEvent _sendBufferReset = new ManualResetEvent(false);

        // Whether or not a connection currently exists
        private volatile bool _isConnected = false;
        private readonly object _isConnectedLock = new object();

        // The client multiplexer
        private readonly Dictionary<int, MultiplexerData> _clientMultiplexer = null;
        private readonly ReaderWriterLockSlim _clientMultiplexerLock = new ReaderWriterLockSlim();

        // Various pools
        private readonly Pool<MultiplexerData> _multiplexerDataPool = null;
        private readonly Pool<SocketAsyncEventArgs> _socketAsyncEventArgsSendPool = null;
        private readonly Pool<SocketAsyncEventArgs> _socketAsyncEventArgsReceivePool = null;
        private readonly Pool<SocketAsyncEventArgs> _socketAsyncEventArgsKeepAlivePool = null;
        private readonly Pool<ReceivedMessage> _receivedMessagePool = null;
        private readonly Pool<MessageReceivedArgs> _messageReceivedArgsPool = null;
        private readonly Pool<SocketErrorArgs> _socketErrorArgsPool = null;

        // The date time of the last response
        private DateTime _lastResponse = DateTime.UtcNow;

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
        public SimplSocketClient(Func<Socket> socketFunc, int messageBufferSize = 65536, int communicationTimeout = 10000, int maxMessageSize = 10 * 1024 * 1024, bool useNagleAlgorithm = false)
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

            _sendBufferQueue = new BlockingQueue<SocketAsyncEventArgs>();
            _receiveBufferQueue = new BlockingQueue<SocketAsyncEventArgs>();

            // Initialize the client multiplexer
            _clientMultiplexer = new Dictionary<int, MultiplexerData>(PREDICTED_THREAD_COUNT);

            // Create the pools
            _multiplexerDataPool = new Pool<MultiplexerData>(PREDICTED_THREAD_COUNT, () => new MultiplexerData { ManualResetEvent = new ManualResetEvent(false) }, multiplexerData => 
            {
                multiplexerData.Message = null;
                multiplexerData.ManualResetEvent.Reset();
            });
            _socketAsyncEventArgsSendPool = new Pool<SocketAsyncEventArgs>(PREDICTED_THREAD_COUNT, () =>
            {
                var poolItem = new SocketAsyncEventArgs();
                poolItem.Completed += OperationCallback;
                return poolItem;
            });
            _socketAsyncEventArgsReceivePool = new Pool<SocketAsyncEventArgs>(PREDICTED_THREAD_COUNT, () =>
            {
                var poolItem = new SocketAsyncEventArgs();
                poolItem.SetBuffer(new byte[messageBufferSize], 0, messageBufferSize);
                poolItem.Completed += OperationCallback;
                return poolItem;
            });
            _socketAsyncEventArgsKeepAlivePool = new Pool<SocketAsyncEventArgs>(PREDICTED_THREAD_COUNT, () =>
            {
                var poolItem = new SocketAsyncEventArgs();
                poolItem.SetBuffer(ProtocolHelper.ControlBytesPlaceholder, 0, ProtocolHelper.ControlBytesPlaceholder.Length);
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

                // Post a receive to the socket as the client will be continuously receiving messages to be pushed to the queue
                if (!TryUnsafeSocketOperation(_socket, SocketAsyncOperation.Receive, _socketAsyncEventArgsReceivePool.Pop()))
                {
                    return false;
                }
                
                // Spin up the keep-alive
                Task.Factory.StartNew(() => KeepAlive(_socket));

                // Process all queued sends on a separate thread
                Task.Factory.StartNew(() => ProcessSendQueue(_socket));

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

            var messageWithControlBytes = ProtocolHelper.AppendControlBytesToMessage(message, threadId);
            var socketAsyncEventArgs = _socketAsyncEventArgsSendPool.Pop();
            socketAsyncEventArgs.SetBuffer(messageWithControlBytes, 0, messageWithControlBytes.Length);

            _sendBufferQueue.Enqueue(socketAsyncEventArgs);
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

            var messageWithControlBytes = ProtocolHelper.AppendControlBytesToMessage(message, threadId);

            var socketAsyncEventArgs = _socketAsyncEventArgsSendPool.Pop();
            socketAsyncEventArgs.SetBuffer(messageWithControlBytes, 0, messageWithControlBytes.Length);

            // Prioritize sends that have receives to the front of the queue
            _sendBufferQueue.EnqueueFront(socketAsyncEventArgs);

            // Wait for our message to go ahead from the receive callback, or until the timeout is reached
            if (!multiplexerData.ManualResetEvent.WaitOne(_communicationTimeout))
            {
                HandleCommunicationError(_socket, new TimeoutException("The connection timed out before the response message was received"));

                // Unenroll from the multiplexer
                UnenrollMultiplexer(threadId);

                // No signal
                return null;
            }

            // Now get the command string
            var result = multiplexerData.Message;

            // Unenroll from the multiplexer
            UnenrollMultiplexer(threadId);

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

        private void KeepAlive(Socket socket)
        {
            _lastResponse = DateTime.UtcNow;

            while (_isConnected)
            {
                Thread.Sleep(1000);

                // Do the keep-alive
                var socketAsyncEventArgs = _socketAsyncEventArgsKeepAlivePool.Pop();

                _sendBufferQueue.Enqueue(socketAsyncEventArgs);

                // Confirm that we've heard from the server recently
                if ((DateTime.UtcNow - _lastResponse).TotalMilliseconds > _communicationTimeout)
                {
                    HandleCommunicationError(socket, new Exception("Keep alive timed out"));
                    return;
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
                default:
                    throw new InvalidOperationException("Unknown case called, should program something for this");
            }
        }

        private void SendCallback(Socket socket, SocketAsyncEventArgs socketAsyncEventArgs)
        {
            // Check for error
            if (socketAsyncEventArgs.SocketError != SocketError.Success)
            {
                HandleCommunicationError(socket, new Exception("Send failed, error = " + socketAsyncEventArgs.SocketError));
            }

            _sendBufferReset.Set();

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
                HandleCommunicationError(socket, new Exception("Receive failed, error = " + socketAsyncEventArgs.SocketError));
            }

            // Get the message state
            int bytesRead = socketAsyncEventArgs.BytesTransferred;

            // Read the data
            if (bytesRead > 0)
            {
                // Add to receive queue
                _receiveBufferQueue.Enqueue(socketAsyncEventArgs);
            }
            else
            {
                // 0 bytes means disconnect
                HandleCommunicationError(socket, new Exception("Received 0 bytes (graceful disconnect)"));

                _socketAsyncEventArgsReceivePool.Push(socketAsyncEventArgs);
                return;
            }

            socketAsyncEventArgs = _socketAsyncEventArgsReceivePool.Pop();

            // Post a receive to the socket as the client will be continuously receiving messages to be pushed to the queue
            TryUnsafeSocketOperation(socket, SocketAsyncOperation.Receive, socketAsyncEventArgs);
        }

        private void ProcessSendQueue(Socket handler)
        {
            while (_isConnected)
            {
                // Get the next buffer from the queue
                var socketAsyncEventArgs = _sendBufferQueue.Dequeue();
                if (socketAsyncEventArgs == null)
                {
                    continue;
                }

                _sendBufferReset.Reset();

                // Do the send
                TryUnsafeSocketOperation(_socket, SocketAsyncOperation.Send, socketAsyncEventArgs);

                if (!_sendBufferReset.WaitOne(_communicationTimeout))
                {
                    HandleCommunicationError(_socket, new TimeoutException("The connection timed out before the send acknowledgement was received"));
                    return;
                }
            }
        }

        private void ProcessReceivedMessage(Socket handler)
        {
            int bytesToRead = -1;
            int threadId = -1;

            int controlBytesOffset = 0;
            byte[] protocolBuffer = new byte[ProtocolHelper.ControlBytesPlaceholder.Length];
            byte[] resultBuffer = null;

            // Loop until socket is done
            while (_isConnected)
            {
                // Get the next buffer from the queue
                var socketAsyncEventArgs = _receiveBufferQueue.Dequeue();
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

                            // Reset message state
                            resultBuffer = null;
                        }
                        
                        bytesToRead = -1;
                        threadId = -1;

                        _lastResponse = DateTime.UtcNow;
                    }
                }

                _socketAsyncEventArgsReceivePool.Push(socketAsyncEventArgs);
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
        /// Sends a message back to the server.
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
                HandleCommunicationError(socket, ex);
                return false;
            }
            catch (ObjectDisposedException)
            {
                // If disposed, handle communication error was already done and we're just catching up on other threads. suppress it.
                return false;
            }

            return true;
        }
    }
}
