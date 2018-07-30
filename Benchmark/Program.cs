using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Running;
using DemoServer;
using SimplPipelines;
using SimplSockets;
using System;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;

namespace Benchmark
{
    static class Program
    {
        static void Main(string[] args)
        {
            var summary = BenchmarkRunner.Run<Benchmarks>();
            Console.WriteLine(summary);
        }
    }
    // note: MemoryDiagnoser here won't work well on CoreJob, due to
    // the GC not making total memory usage available
    [ClrJob, CoreJob, MemoryDiagnoser, WarmupCount(2), IterationCount(10)]
    public class Benchmarks
    {
        static readonly EndPoint
            s1 = new IPEndPoint(IPAddress.Loopback, 6000),
            s2 = new IPEndPoint(IPAddress.Loopback, 6001);

        byte[] _data;
        IDisposable _socketServer, _pipeServer;
        [GlobalSetup]
        public void Setup()
        {
            var socketServer = new SimplSocketServer(CreateSocket);
            socketServer.Listen(s1);
            _socketServer = socketServer;
            var pipeServer = SimplPipelineSocketServer.For<ReverseServer>();
            pipeServer.Listen(s2);
            _pipeServer = pipeServer;

            _data = new byte[1024];
        }
        static readonly Func<Socket> CreateSocket = () => new Socket(
            AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp)
            { NoDelay = true };
        void Dispose<T>(ref T field) where T : class, IDisposable
        {
            if (field != null) try { field.Dispose(); } catch { }
        }
        [GlobalCleanup]
        public void TearDown()
        {
            Dispose(ref _socketServer);
            Dispose(ref _pipeServer);

            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced);
            GC.WaitForPendingFinalizers();
            CheckForLeaks();
        }

        static void CheckForLeaks()
        {
            int leaks = MemoryOwner.LeakCount<byte>();
            if (leaks != 0) throw new InvalidOperationException($"Failed to dispose {leaks} byte-leases");
        }
        
        const int Ops = 1000;
        long AssertResult(long result)
        {
            int expected = _data.Length * Ops;
            if (result != expected) throw new InvalidOperationException(
                $"Data error: expected {expected}, got {result}");
            CheckForLeaks();
            return result;
        }
        
        //[Benchmark(OperationsPerInvoke = Ops)]
        public long c1_s1()
        {
            long x = 0;
            using (var client = new SimplSocketClient(CreateSocket))
            {
                client.Connect(s1);
                for (int i = 0; i < Ops; i++)
                {
                    var response = client.SendReceive(_data);
                    x += response.Length;
                }
            }
            return AssertResult(x);
        }
        //[Benchmark(OperationsPerInvoke = Ops)]
        public long c1_s2()
        {
            long x = 0;
            using (var client = new SimplSocketClient(CreateSocket))
            {
                client.Connect(s2);
                for (int i = 0; i < Ops; i++)
                {
                    var response = client.SendReceive(_data);
                    x += response.Length;
                }
            }
            return AssertResult(x);
        }
        //[Benchmark(OperationsPerInvoke = Ops)]
        public async Task<long> c2_s1()
        {
            long x = 0;
            using (var client = await SimplPipelineClient.ConnectAsync(s1))
            {
                for (int i = 0; i < Ops; i++)
                {
                    using (var response = await client.SendReceiveAsync(_data))
                    {
                        x += response.Memory.Length;
                    }
                }
            }
            return AssertResult(x);
        }
        [Benchmark(OperationsPerInvoke = Ops)]
        public async Task<long> c2_s2()
        {
            long x = 0;
            using (var client = await SimplPipelineClient.ConnectAsync(s2))
            {
                for (int i = 0; i < Ops; i++)
                {
                    using (var response = await client.SendReceiveAsync(_data))
                    {
                        x += response.Memory.Length;
                    }
                }
            }
            return AssertResult(x);
        }
    }
}
