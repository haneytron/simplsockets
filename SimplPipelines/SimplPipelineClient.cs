using Pipelines.Sockets.Unofficial;
using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO.Pipelines;
using System.Net;
using System.Threading.Tasks;

namespace SimplPipelines
{
    public class SimplPipelineClient : SimplPipeline
    {
        public SimplPipelineClient(IDuplexPipe pipe) : base(pipe)
            => StartReceiveLoopAsync().ContinueWith( // fire and forget
                t => GC.KeepAlive(t.Exception), TaskContinuationOptions.OnlyOnFaulted);

        public static async Task<SimplPipelineClient> ConnectAsync(EndPoint endpoint)
            => new SimplPipelineClient(await SocketConnection.ConnectAsync(endpoint));

        private readonly Dictionary<int, TaskCompletionSource<IMemoryOwner<byte>>> _awaitingResponses
            = new Dictionary<int, TaskCompletionSource<IMemoryOwner<byte>>>();

        private int _nextMessageId;
        public ValueTask SendAsync(ReadOnlyMemory<byte> message)
            => WriteAsync(message, 0);

        public async Task<IMemoryOwner<byte>> SendReceiveAsync(IMemoryOwner<byte> message)
        {
            using (message)
            {
                return await SendReceiveAsync(message.Memory);
            }
        }
        public Task<IMemoryOwner<byte>> SendReceiveAsync(ReadOnlyMemory<byte> message)
        {
            async Task<IMemoryOwner<byte>> Awaited(ValueTask pendingWrite, Task<IMemoryOwner<byte>> response)
            {
                await pendingWrite;
                return await response;
            }

            var tcs = new TaskCompletionSource<IMemoryOwner<byte>>();
            int messageId;
            lock (_awaitingResponses)
            {
                do
                {
                    messageId = ++_nextMessageId;
                } while (messageId == 0 || _awaitingResponses.ContainsKey(messageId));
                _awaitingResponses.Add(messageId, tcs);
            }
            var write = WriteAsync(message, messageId);
            return write.IsCompletedSuccessfully ? tcs.Task : Awaited(write, tcs.Task);
        }

        protected override ValueTask OnReceiveAsync(ReadOnlySequence<byte> payload, int messageId)
        {
            if (messageId != 0)
            {
                // request/response
                TaskCompletionSource<IMemoryOwner<byte>> tcs;
                lock (_awaitingResponses)
                {
                    if (_awaitingResponses.TryGetValue(messageId, out tcs))
                    {
                        _awaitingResponses.Remove(messageId);
                    }
                    else
                    {   // didn't find a twin, but... meh
                        tcs = null;
                        messageId = 0; // treat as MessageReceived
                    }
                }
                if(tcs != null)
                {
                    IMemoryOwner<byte> lease = null;
                    try
                    {   // only if we successfully hand it over
                        // to the TCS is it considered "not our
                        // problem anymore" - otherwise: we need
                        // to dispose
                        lease = payload.Lease();
                        if (tcs.TrySetResult(lease))
                            lease = null;
                    } finally
                    {
                        if(lease != null)
                            try { lease.Dispose(); } catch { }
                    }                    
                }
                
            }
            if (messageId == 0)
            {
                // unsolicited
                MessageReceived?.Invoke(payload.Lease());
            }
            return default;
        }
        public event Action<IMemoryOwner<byte>> MessageReceived;
    }
}
