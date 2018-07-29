using Pipelines.Sockets.Unofficial;
using SimplPipelines;
using SimplSockets;
using System;
using System.Buffers;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;

namespace DemoServer
{
    class Program
    {
        static Task Main(string[] args)
        {
            string option;
            TryAgain:
            if (args == null || args.Length == 0)
            {   
                Console.WriteLine("1: run server via SimplPipelines");
                Console.WriteLine("2: run server via SimplSockets");
                option = Console.ReadLine();
            }
            else
            {
                option = args[0];
                args = null;
            }
            switch(option)
            {
                case "1": return RunViaPipelines();
                case "2": return RunViaSockets();
                default: goto TryAgain;
                    
            }
        }
        static async Task RunViaPipelines()
        {
            using (var socket = SimplPipelineSocketServer.For<ReverseServer>())
            {
                socket.Listen(new IPEndPoint(IPAddress.Loopback, 5000));
                await Console.Out.WriteLineAsync(
                    "Server running; type 'q' to exit, anything else to broadcast");

                string line;
                while ((line = await Console.In.ReadLineAsync()) != null)
                {
                    if (line == "q") break;

                    int clientCount, len;
                    using (var leased = line.Encode())
                    {
                        var memory = leased.Memory;
                        len = memory.Length;
                        clientCount = await socket.Server.BroadcastAsync(memory);
                    }
                    await Console.Out.WriteLineAsync(
                        $"Broadcast {len} bytes to {clientCount} clients");
                }
            }
        }
        static async Task RunViaSockets()
        {
            using (var server = new SimplSocketServer(
                () => new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp)
                { NoDelay = true }))
            {
                server.MessageReceived += (s, e) =>
                {
                    var blob = e.ReceivedMessage.Message;
                    ReverseServer.Reverse(blob);
                    server.Reply(blob, e.ReceivedMessage);
                };

                server.Listen(new IPEndPoint(IPAddress.Loopback, 5000));
                await Console.Out.WriteLineAsync(
                    "Server running; type 'q' to exit, anything else to broadcast");

                string line;
                while ((line = await Console.In.ReadLineAsync()) != null)
                {
                    if (line == "q") break;

                    var blob = Encoding.UTF8.GetBytes(line);
                    server.Broadcast(blob);
                    
                    await Console.Out.WriteLineAsync(
                        $"Broadcast {blob.Length} bytes to {server.CurrentlyConnectedClientCount} clients");
                }
            }
        }
    }

    public class SimplPipelineSocketServer : SocketServer
    {
        public SimplPipelineServer Server { get; }

        public SimplPipelineSocketServer(SimplPipelineServer server)
            => Server = server ?? throw new ArgumentNullException(nameof(server));

        protected override Task OnClientConnectedAsync(in ClientConnection client)
            => Server.RunClientAsync(client.Transport);

        public static SimplPipelineSocketServer For<T>() where T : SimplPipelineServer, new()
            => new SimplPipelineSocketServer(new T());
        protected override void Dispose(bool disposing)
        {
            if (disposing) Server.Dispose();
        }
    }
    public class ReverseServer : SimplPipelineServer
    {
        protected override ValueTask<IMemoryOwner<byte>> OnReceiveForReplyAsync(IMemoryOwner<byte> message)
        {
            // since the "message" outlives the response write,
            // we can just overwrite the existing value, yay!
            var memory = message.Memory;
            Reverse(memory.Span);
            return new ValueTask<IMemoryOwner<byte>>(message);
        }

        internal static unsafe void Reverse(Span<byte> span)
        {
            // yes, I know this only works for ASCII (multi-byte code-points,
            // and multi-codepoint grapheme clusters)
            fixed (byte* spanPtr = &MemoryMarshal.GetReference(span))
            {
                byte* from = spanPtr, to = spanPtr + span.Length;
                int count = span.Length / 2;
                while (count-- != 0)
                {
                    var tmp = *--to;
                    *to = *from;
                    *from++ = tmp;
                }
            }
        }
    }
}
