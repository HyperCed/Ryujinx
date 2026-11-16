using Ryujinx.Graphics.Gpu.Memory;
using System;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;

namespace Ryujinx.Graphics.Gpu.Engine.GPFifo
{
    /// <summary>
    /// Represents a GPU General Purpose FIFO device.
    /// </summary>
    public sealed class GPFifoDevice : IDisposable
    {
        /// <summary>
        /// Indicates if the command buffer has pre-fetch enabled.
        /// </summary>
        private enum CommandBufferType
        {
            Prefetch,
            NoPrefetch,
        }

        /// <summary>
        /// Command buffer data.
        /// </summary>
        private struct CommandBuffer
        {
            /// <summary>
            /// Processor used to process the command buffer. Contains channel state.
            /// </summary>
            public GPFifoProcessor Processor;

            /// <summary>
            /// The type of the command buffer.
            /// </summary>
            public CommandBufferType Type;

            /// <summary>
            /// Fetched data.
            /// </summary>
            public int[] Words;

            /// <summary>
            /// The GPFIFO entry address (used in <see cref="CommandBufferType.NoPrefetch"/> mode).
            /// </summary>
            public ulong EntryAddress;

            /// <summary>
            /// The count of entries inside this GPFIFO entry.
            /// </summary>
            public uint EntryCount;

            // Switch 2: the last entry of a submission, and where to resume after a suspension.
            public bool EndOfSubmission;

            public int ResumeIndex;

            /// <summary>
            /// Get the entries for the command buffer from memory.
            /// </summary>
            /// <param name="memoryManager">The memory manager used to fetch the data</param>
            /// <param name="flush">If true, flushes potential GPU written data before reading the command buffer</param>
            /// <returns>The fetched data</returns>
            private readonly ReadOnlySpan<int> GetWords(MemoryManager memoryManager, bool flush)
            {
                return MemoryMarshal.Cast<byte, int>(memoryManager.GetSpan(EntryAddress, (int)EntryCount * 4, flush));
            }

            /// <summary>
            /// Prefetch the command buffer.
            /// </summary>
            /// <param name="memoryManager">The memory manager used to fetch the data</param>
            public void Prefetch(MemoryManager memoryManager)
            {
                Words = GetWords(memoryManager, true).ToArray();
            }

            /// <summary>
            /// Fetch the command buffer.
            /// </summary>
            /// <param name="memoryManager">The memory manager used to fetch the data</param>
            /// <param name="flush">If true, flushes potential GPU written data before reading the command buffer</param>
            /// <returns>The command buffer words</returns>
            public readonly ReadOnlySpan<int> Fetch(MemoryManager memoryManager, bool flush)
            {
                return Words ?? GetWords(memoryManager, flush);
            }
        }

        private readonly ConcurrentQueue<CommandBuffer> _commandBufferQueue;

        private GPFifoProcessor _prevChannelProcessor;

        private readonly bool _ibEnable;
        private readonly GpuContext _context;
        private readonly AutoResetEvent _event;

        private bool _interrupt;
        private int _flushSkips;

        /// <summary>
        /// Creates a new instance of the GPU General Purpose FIFO device.
        /// </summary>
        /// <param name="context">GPU context that the GPFIFO belongs to</param>
        internal GPFifoDevice(GpuContext context)
        {
            _commandBufferQueue = new ConcurrentQueue<CommandBuffer>();
            _ibEnable = true;
            _context = context;
            _event = new AutoResetEvent(false);
        }

        /// <summary>
        /// Signal the FIFO that there are new entries to process.
        /// </summary>
        public void SignalNewEntries()
        {
            _event.Set();
        }

        /// <summary>
        /// Push a GPFIFO entry in the form of a prefetched command buffer.
        /// It is intended to be used by nvservices to handle special cases.
        /// </summary>
        /// <param name="processor">Processor used to process <paramref name="commandBuffer"/></param>
        /// <param name="commandBuffer">The command buffer containing the prefetched commands</param>
        internal void PushHostCommandBuffer(GPFifoProcessor processor, int[] commandBuffer)
        {
            _commandBufferQueue.Enqueue(new CommandBuffer
            {
                Processor = processor,
                Type = CommandBufferType.Prefetch,
                Words = commandBuffer,
                EntryAddress = ulong.MaxValue,
                EntryCount = (uint)commandBuffer.Length,
            });
        }

        /// <summary>
        /// Create a CommandBuffer from a GPFIFO entry.
        /// </summary>
        /// <param name="processor">Processor used to process the command buffer pointed to by <paramref name="entry"/></param>
        /// <param name="entry">The GPFIFO entry</param>
        /// <returns>A new CommandBuffer based on the GPFIFO entry</returns>
        private static CommandBuffer CreateCommandBuffer(GPFifoProcessor processor, GPEntry entry)
        {
            CommandBufferType type = CommandBufferType.Prefetch;

            if (entry.Entry1Sync == Entry1Sync.Wait)
            {
                type = CommandBufferType.NoPrefetch;
            }

            ulong startAddress = ((ulong)entry.Entry0Get << 2) | ((ulong)entry.Entry1GetHi << 32);

            return new CommandBuffer
            {
                Processor = processor,
                Type = type,
                Words = null,
                EntryAddress = startAddress,
                EntryCount = (uint)entry.Entry1Length,
            };
        }

        /// <summary>
        /// Pushes GPFIFO entries.
        /// </summary>
        /// <param name="processor">Processor used to process the command buffers pointed to by <paramref name="entries"/></param>
        /// <param name="entries">GPFIFO entries</param>
        internal void PushEntries(GPFifoProcessor processor, ReadOnlySpan<ulong> entries)
        {
            bool beforeBarrier = true;

            for (int index = 0; index < entries.Length; index++)
            {
                ulong entry = entries[index];

                CommandBuffer commandBuffer = CreateCommandBuffer(processor, Unsafe.As<ulong, GPEntry>(ref entry));

                commandBuffer.EndOfSubmission = index == entries.Length - 1;

                // Switch 2 command buffers are gated by semaphores the CPU releases after writing them,
                // so they are read when they run, never at submission.
                if (beforeBarrier && commandBuffer.Type == CommandBufferType.Prefetch && !_context.Nx2Ampere)
                {
                    commandBuffer.Prefetch(processor.MemoryManager);
                }

                if (commandBuffer.Type == CommandBufferType.NoPrefetch)
                {
                    beforeBarrier = false;
                }

                _commandBufferQueue.Enqueue(commandBuffer);
            }
        }

        /// <summary>
        /// Waits until commands are pushed to the FIFO.
        /// </summary>
        /// <returns>True if commands were received, false if wait timed out</returns>
        public bool WaitForCommands()
        {
            if (_nx2DeferredOrder.Count != 0)
            {
                // Suspended channels are released by the guest CPU writing memory: poll them.
                if (_commandBufferQueue.IsEmpty)
                {
                    _event.WaitOne(1);
                }

                return true;
            }

            return !_commandBufferQueue.IsEmpty || (_event.WaitOne(8) && !_commandBufferQueue.IsEmpty);
        }

        /// <summary>
        /// Processes commands pushed to the FIFO.
        /// </summary>
        public void DispatchCalls()
        {
            // Use this opportunity to also dispose any pending channels that were closed.
            _context.RunDeferredActions();

            if (_context.Nx2Ampere)
            {
                Nx2DispatchCalls();

                _interrupt = false;

                return;
            }

            // Process command buffers.
            while (_ibEnable && !_interrupt && _commandBufferQueue.TryDequeue(out CommandBuffer entry))
            {
                bool flushCommandBuffer = true;

                if (_flushSkips != 0)
                {
                    _flushSkips--;
                    flushCommandBuffer = false;
                }

                ReadOnlySpan<int> words = entry.Fetch(entry.Processor.MemoryManager, flushCommandBuffer);

                // If we are changing the current channel,
                // we need to force all the host state to be updated.
                if (_prevChannelProcessor != entry.Processor)
                {
                    _prevChannelProcessor = entry.Processor;
                    entry.Processor.ForceAllDirty();
                }

                entry.Processor.Process(entry.EntryAddress, words);
            }

            _interrupt = false;
        }

        /// <summary>
        /// Sets the number of flushes that should be skipped for subsequent command buffers.
        /// </summary>
        /// <remarks>
        /// This can improve performance when command buffer data only needs to be consumed by the GPU.
        /// </remarks>
        /// <param name="count">The amount of flushes that should be skipped</param>
        // Switch 2: command buffers of channels that are suspended on a semaphore acquire, in order.
        private readonly System.Collections.Generic.Dictionary<GPFifoProcessor, System.Collections.Generic.Queue<CommandBuffer>> _nx2Deferred = [];
        private readonly System.Collections.Generic.List<GPFifoProcessor> _nx2DeferredOrder = [];
        private int _nx2Timeouts;

        private static readonly System.IO.BinaryWriter _nx2Dump = Environment.GetEnvironmentVariable("RYUJINX_NX2_GPFIFO_DUMP") is { Length: > 0 } path
            ? new System.IO.BinaryWriter(System.IO.File.Create(path))
            : null;

        private static readonly long _nx2AcquireTimeout = System.Diagnostics.Stopwatch.Frequency * 4;

        /// <summary>
        /// Switch 2: true while a channel has command buffers waiting on a semaphore acquire.
        /// </summary>
        public bool Nx2HasSuspendedChannels => _nx2DeferredOrder.Count != 0;

        private void Nx2DispatchCalls()
        {
            bool progress = true;

            while (_ibEnable && !_interrupt && progress)
            {
                progress = false;

                // Channels whose acquire was met since continue first: their buffers are older.
                for (int i = 0; i < _nx2DeferredOrder.Count; i++)
                {
                    GPFifoProcessor processor = _nx2DeferredOrder[i];
                    System.Collections.Generic.Queue<CommandBuffer> queue = _nx2Deferred[processor];

                    if (processor.Nx2IsSuspended && processor.Nx2PendingAcquire is Nx2PendingAcquire acquire &&
                        System.Diagnostics.Stopwatch.GetTimestamp() - acquire.Since > _nx2AcquireTimeout)
                    {
                        if (_nx2Timeouts++ < 32)
                        {
                            Ryujinx.Common.Logging.Logger.Warning?.Print(Ryujinx.Common.Logging.LogClass.Gpu,
                                $"Switch 2 channel waited 4 s on semaphore 0x{acquire.Address:X} ({acquire.Operation} 0x{acquire.Value:x}), continuing");
                        }

                        processor.Nx2ForceResume();
                    }

                    while (queue.Count != 0 && !processor.Nx2IsSuspended)
                    {
                        CommandBuffer entry = queue.Dequeue();

                        progress = true;

                        if (!Nx2Run(ref entry))
                        {
                            // Suspended again: the rest of this buffer goes back to the front.
                            CommandBuffer[] rest = queue.ToArray();

                            queue.Clear();
                            queue.Enqueue(entry);

                            foreach (CommandBuffer other in rest)
                            {
                                queue.Enqueue(other);
                            }

                            break;
                        }
                    }

                    if (queue.Count == 0 && !processor.Nx2IsSuspended)
                    {
                        _nx2Deferred.Remove(processor);
                        _nx2DeferredOrder.RemoveAt(i--);
                    }
                }

                while (_ibEnable && !_interrupt && _commandBufferQueue.TryDequeue(out CommandBuffer entry))
                {
                    progress = true;

                    if (_nx2Deferred.TryGetValue(entry.Processor, out System.Collections.Generic.Queue<CommandBuffer> queue))
                    {
                        queue.Enqueue(entry);

                        continue;
                    }

                    if (!Nx2Run(ref entry))
                    {
                        queue = new System.Collections.Generic.Queue<CommandBuffer>();
                        queue.Enqueue(entry);

                        _nx2Deferred.Add(entry.Processor, queue);
                        _nx2DeferredOrder.Add(entry.Processor);

                        // Another channel may be what releases this one.
                        break;
                    }
                }
            }
        }

        // Runs a command buffer from where it stopped. Returns false if the channel suspended inside it.
        private bool Nx2Run(ref CommandBuffer entry)
        {
            GPFifoProcessor processor = entry.Processor;
            ReadOnlySpan<int> words;

            if (entry.Words is { Length: 0 })
            {
                // A buffer that suspended on its last word: only the wait is left of it.
                return !processor.Nx2IsSuspended;
            }

            if (entry.Words != null)
            {
                words = entry.Words;
            }
            else if (!processor.MemoryManager.IsMapped(entry.EntryAddress))
            {
                words = default;
            }
            else
            {
                // Read again after a suspension: the CPU writes the commands behind an acquire before it
                // releases the semaphore.
                words = MemoryMarshal.Cast<byte, int>(processor.MemoryManager.GetSpan(entry.EntryAddress, (int)entry.EntryCount * 4, true));
            }

            if (_prevChannelProcessor != processor)
            {
                _prevChannelProcessor = processor;
            }

            if (_nx2Dump != null)
            {
                // RYUJINX_NX2_GPFIFO_DUMP=<file>: every entry as it runs (channel, address, resume index,
                // flags, word count, words), to study the command streams offline.
                _nx2Dump.Write(processor.GetHashCode());
                _nx2Dump.Write(entry.EntryAddress);
                _nx2Dump.Write(entry.ResumeIndex);
                _nx2Dump.Write(entry.EndOfSubmission ? 1 : 0);
                _nx2Dump.Write(words.Length);
                _nx2Dump.Write(MemoryMarshal.AsBytes(words));
                _nx2Dump.Flush();
            }

            entry.ResumeIndex = processor.Nx2Process(entry.EntryAddress, words, entry.ResumeIndex);

            if (entry.ResumeIndex < words.Length || processor.Nx2IsSuspended)
            {
                if (entry.ResumeIndex < words.Length)
                {
                    return false;
                }

                // Suspended on the last word: the buffer itself is done.
                if (entry.EndOfSubmission)
                {
                    processor.Nx2EndSubmission();
                }

                entry.EntryCount = 0;
                entry.Words = [];
                entry.EndOfSubmission = false;
                entry.ResumeIndex = 0;

                return false;
            }

            if (entry.EndOfSubmission)
            {
                processor.Nx2EndSubmission();
            }

            return true;
        }

        internal void SetFlushSkips(int count)
        {
            _flushSkips = count;
        }

        /// <summary>
        /// Interrupts command processing. This will break out of the DispatchCalls loop.
        /// </summary>
        public void Interrupt()
        {
            _interrupt = true;
            _event.Set();
        }

        /// <summary>
        /// Disposes of resources used for GPFifo command processing.
        /// </summary>
        public void Dispose() => _event.Dispose();
    }
}
