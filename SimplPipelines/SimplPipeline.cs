using System;
using System.Buffers;
using System.Buffers.Binary;
using System.IO.Pipelines;
using System.Threading;
using System.Threading.Tasks;

namespace SimplPipelines
{
    public abstract class SimplPipeline : IDisposable
    {
        private readonly SemaphoreSlim _singleWriter = new SemaphoreSlim(1);

        private IDuplexPipe _pipe;

        protected SimplPipeline(IDuplexPipe pipe)
            => _pipe = pipe;

        public void Dispose() => Close();
        public void Close(Exception ex = null)
        {
            var pipe = _pipe;
            _pipe = null;

            if (pipe != null)
            {
                // burn the pipe to the ground
                try { pipe.Input.Complete(ex); } catch { }
                try { pipe.Input.CancelPendingRead(); } catch { }
                try { pipe.Output.Complete(ex); } catch { }
                try { pipe.Output.CancelPendingFlush(); } catch { }
                if (pipe is IDisposable d) try { d.Dispose(); } catch { }
            }
            try { _singleWriter.Dispose(); } catch { }
        }

        void WriteHeader(PipeWriter writer, int length, int messageId)
        {
            // write the control bytes placeholder; the first 4 bytes are little
            // endian message length, the last 4 are message id (was: thread id)
            var span = writer.GetSpan(8);
            BinaryPrimitives.WriteInt32LittleEndian(span, length);
            BinaryPrimitives.WriteInt32LittleEndian(span.Slice(4), messageId);
            writer.Advance(8);
        }

        protected ValueTask WriteAsync(IMemoryOwner<byte> memory, int messageId)
        {
            async ValueTask Awaited(IMemoryOwner<byte> mmemory, ValueTask write)
            {
                using (mmemory)
                {
                    await write;
                }
            }
            try
            {
                var result = WriteAsync(memory.Memory, messageId);
                if (result.IsCompletedSuccessfully) return default;
                var final = Awaited(memory, result);
                memory = null; // prevent dispose
                return final;
            }
            finally
            {
                using (memory) { }
            }
        }
        /// <summary>
        /// Note: it is assumed that the calling subclass has dealt with synchronization
        /// </summary>
        protected ValueTask WriteAsync(ReadOnlyMemory<byte> payload, int messageId)
        {
            async ValueTask AwaitFlushAndRelease(ValueTask<FlushResult> flush)
            {
                try { await flush; }
                finally { _singleWriter.Release(); }
            }
            // try to get the conch; if not, switch to async
            if (!_singleWriter.Wait(0)) return WriteAsyncSlowPath(payload, messageId);
            bool release = true;
            try
            {
                var writer = _pipe?.Output ?? throw new ObjectDisposedException(ToString());
                WriteHeader(writer, payload.Length, messageId);
                var write = writer.WriteAsync(payload); // includes a flush
                if (write.IsCompletedSuccessfully) return default; // sync fast path
                release = false;
                return AwaitFlushAndRelease(write);
            }
            finally
            {
                if (release) _singleWriter.Release();
            }
        }
        async ValueTask WriteAsyncSlowPath(ReadOnlyMemory<byte> payload, int messageId)
        {
            await _singleWriter.WaitAsync();
            try
            {
                var writer = _pipe?.Output ?? throw new ObjectDisposedException(ToString());
                WriteHeader(writer, payload.Length, messageId);
                await writer.WriteAsync(payload); // includes a flush
            }
            finally
            {
                _singleWriter.Release();
            }
        }

        protected abstract ValueTask OnReceiveAsync(ReadOnlySequence<byte> payload, int messageId);

        static int ParseFrameHeader(ReadOnlySpan<byte> input, out int messageId)
        {
            var length = BinaryPrimitives.ReadInt32LittleEndian(input);
            messageId = BinaryPrimitives.ReadInt32LittleEndian(input.Slice(4));
            return length;
        }
        private bool TryParseFrame(ref ReadOnlySequence<byte> input,
            out ReadOnlySequence<byte> payload, out int messageId)
        {
            if (input.Length < 8)
            {   // not enough data for the header
                payload = default;
                messageId = default;
                return false;
            }

            int length;
            if (input.First.Length >= 8)
            {
                length = ParseFrameHeader(input.First.Span, out messageId);
            }
            else
            {
                Span<byte> local = stackalloc byte[8];
                input.Slice(0, 8).CopyTo(local);
                length = ParseFrameHeader(local, out messageId);
            }

            // do we have the "length" bytes?
            if (input.Length < length + 8)
            {
                payload = default;
                return false;
            }

            // success!
            payload = input.Slice(8, length);
            input = input.Slice(payload.End);
            return true;
        }


        protected async Task StartReceiveLoopAsync(CancellationToken cancellationToken = default)
        {
            var reader = _pipe?.Input ?? throw new ObjectDisposedException(ToString());
            try
            {
                await OnStartReceiveLoopAsync();
                bool makingProgress = false;
                while (!cancellationToken.IsCancellationRequested)
                {
                    if (!(makingProgress && reader.TryRead(out var readResult)))
                        readResult = await reader.ReadAsync(cancellationToken);
                    if (readResult.IsCanceled) break;

                    var buffer = readResult.Buffer;

                    // handle as many frames from the data as we can
                    // (note: an alternative strategy is handle one frame
                    // and release via AdvanceTo as soon as possible)
                    makingProgress = false;
                    while (TryParseFrame(ref buffer, out var payload, out var messageId))
                    {
                        makingProgress = true;
                        await OnReceiveAsync(payload, messageId);
                    }

                    // record that we comsumed up to the (now updated) buffer.Start,
                    // and tried to look at everything - hence buffer.End
                    reader.AdvanceTo(buffer.Start, buffer.End);

                    // exit the loop electively, or because we've consumed everything
                    // that we can usefully consume
                    if (!makingProgress && readResult.IsCompleted) break;
                }
                try { reader.Complete(); } catch { }
            }
            catch (Exception ex)
            {
                try { reader.Complete(ex); } catch { }
            }
            finally
            {
                try { await OnEndReceiveLoopAsync(); } catch { }
            }
        }

        protected virtual ValueTask OnStartReceiveLoopAsync() => default;
        protected virtual ValueTask OnEndReceiveLoopAsync() => default;
    }
}
