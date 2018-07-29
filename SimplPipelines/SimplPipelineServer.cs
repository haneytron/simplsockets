using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.IO.Pipelines;
using System.Threading;
using System.Threading.Tasks;

namespace SimplPipelines
{
    public abstract class SimplPipelineServer : IDisposable
    {
        public int ClientCount => _clients.Count;
        readonly ConcurrentDictionary<Client, Client> _clients = new ConcurrentDictionary<Client, Client>();
        public Task RunClientAsync(IDuplexPipe pipe, CancellationToken cancellationToken = default)
            => new Client(pipe, this).RunAsync(cancellationToken);
        protected virtual ValueTask OnReceiveAsync(IMemoryOwner<byte> message) => default;
        protected virtual ValueTask<IMemoryOwner<byte>> OnReceiveForReplyAsync(IMemoryOwner<byte> message)
        {
            using (message)
            {
                return new ValueTask<IMemoryOwner<byte>>(MemoryOwner.Empty<byte>());
            }
        }
        public async ValueTask<int> BroadcastAsync(IMemoryOwner<byte> message)
        {
            using (message)
            {
                return await BroadcastAsync(message.Memory);
            }
        }
        public async ValueTask<int> BroadcastAsync(ReadOnlyMemory<byte> message)
        {
            int count = 0;
            foreach (var client in _clients)
            {
                try
                {
                    await client.Key.SendAsync(message);
                    count++;
                }
                catch { } // ignore failures on specific clients
            }
            return count;
        }

        private class Client : SimplPipeline
        {
            public Task RunAsync(CancellationToken cancellationToken)
                => StartReceiveLoopAsync(cancellationToken);

            private readonly SimplPipelineServer _server;
            public Client(IDuplexPipe pipe, SimplPipelineServer server) : base(pipe)
                => _server = server;

            public ValueTask SendAsync(ReadOnlyMemory<byte> message) => WriteAsync(message, 0);

            protected sealed override ValueTask OnReceiveAsync(ReadOnlySequence<byte> payload, int messageId)
            {
                // DF will hate me for this, but... it won't be awaited, so : don't create the task
                async void AwaitedResponse(
                    ValueTask<IMemoryOwner<byte>> ppendingResponse,
                    int mmessageId, IMemoryOwner<byte> rrequest)
                {
                    try
                    {
                        using (rrequest)
                        {
                            var response = await ppendingResponse;
                            await WriteAsync(response, mmessageId);
                        }
                    }
                    catch { } // nom nom nom
                }
                void DisposeOnCompletion(ValueTask task, ref IMemoryOwner<byte> message)
                {
                    task.AsTask().ContinueWith((t, s) => ((IMemoryOwner<byte>)s)?.Dispose(), message);
                    message = null; // caller no longer owns it, logically; don't wipe on exit
                }
                var msg = payload.Lease();
                try
                {
                    if (messageId == 0)
                    {
                        var pendingAction = _server.OnReceiveAsync(msg);
                        if (!pendingAction.IsCompletedSuccessfully)
                            DisposeOnCompletion(pendingAction, ref msg);
                    }
                    else
                    {
                        var pendingResponse = _server.OnReceiveForReplyAsync(msg);
                        if (pendingResponse.IsCompletedSuccessfully)
                        {
                            var pendingWrite = WriteAsync(pendingResponse.Result, messageId);
                            if (!pendingWrite.IsCompletedSuccessfully)
                                DisposeOnCompletion(pendingWrite, ref msg);
                        }
                        else
                        {
                            AwaitedResponse(pendingResponse, messageId, msg);
                            msg = null;
                        }
                    }
                }
                finally
                {   // might have been wiped if we went async
                    msg?.Dispose();
                }
                return default;
            }

            protected override ValueTask OnStartReceiveLoopAsync()
            {
                _server.AddClient(this);
                return default;
            }
            protected override ValueTask OnEndReceiveLoopAsync()
            {
                _server.RemoveClient(this);
                return default;
            }
        }

        bool _disposed;
        public void Dispose()
        {
            _disposed = true;
            foreach(var client in _clients)
            {
                client.Key.Dispose();
            }
            _clients.Clear();
        }

        private void AddClient(Client client)
        {
            if (_disposed) throw new ObjectDisposedException(ToString());
            _clients.TryAdd(client, client);
        }

        private void RemoveClient(Client client) => _clients.TryRemove(client, out _);
    }
}
