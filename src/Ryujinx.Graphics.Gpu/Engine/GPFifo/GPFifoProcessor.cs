using Ryujinx.Common.Logging;
using Ryujinx.Graphics.Device;
using Ryujinx.Graphics.Gpu.Engine.Compute;
using Ryujinx.Graphics.Gpu.Engine.Dma;
using Ryujinx.Graphics.Gpu.Engine.InlineToMemory;
using Ryujinx.Graphics.Gpu.Engine.Nx2;
using Ryujinx.Graphics.Gpu.Engine.Threed;
using Ryujinx.Graphics.Gpu.Engine.Twod;
using Ryujinx.Graphics.Gpu.Image;
using Ryujinx.Graphics.Gpu.Memory;
using System;
using System.Linq;
using System.Runtime.CompilerServices;

namespace Ryujinx.Graphics.Gpu.Engine.GPFifo
{
    /// <summary>
    /// Represents a GPU General Purpose FIFO command processor.
    /// </summary>
    class GPFifoProcessor : IDisposable
    {
        private const int MacrosCount = 0x80;
        private const int MacroIndexMask = MacrosCount - 1;

        private const int LoadInlineDataMethodOffset = 0x6d;
        private const int UniformBufferUpdateDataMethodOffset = 0x8e4;

        private readonly GpuContext _context;
        private readonly GpuChannel _channel;

        /// <summary>
        /// Channel memory manager.
        /// </summary>
        public MemoryManager MemoryManager => _channel.MemoryManager;

        /// <summary>
        /// Channel texture manager.
        /// </summary>
        public TextureManager TextureManager => _channel.TextureManager;

        /// <summary>
        /// 3D Engine.
        /// </summary>
        public ThreedClass ThreedClass => _3dClass;

        /// <summary>
        /// Internal GPFIFO state.
        /// </summary>
        private struct DmaState
        {
            public int Method;
            public int SubChannel;
            public int MethodCount;
            public bool NonIncrementing;
            public bool IncrementOnce;
        }

        private DmaState _state;

        private readonly ThreedClass _3dClass;
        private readonly ComputeClass _computeClass;
        private readonly InlineToMemoryClass _i2mClass;
        private readonly TwodClass _2dClass;
        private readonly DmaClass _dmaClass;

        private readonly GPFifoClass _fifoClass;

        /// <summary>
        /// Creates a new instance of the GPU General Purpose FIFO command processor.
        /// </summary>
        /// <param name="context">GPU context</param>
        /// <param name="channel">Channel that the GPFIFO processor belongs to</param>
        public GPFifoProcessor(GpuContext context, GpuChannel channel)
        {
            _context = context;
            _channel = channel;

            _fifoClass = new GPFifoClass(context, this);
            _3dClass = new ThreedClass(context, channel, _fifoClass);
            _computeClass = new ComputeClass(context, channel, _3dClass);
            _i2mClass = new InlineToMemoryClass(context, channel);
            _2dClass = new TwodClass(channel);
            _dmaClass = new DmaClass(context, channel, _3dClass);

            if (Nx2Maxwell)
            {
                lock (context.Nx2ThreedEngines)
                {
                    context.Nx2ThreedEngines.Add(new WeakReference<ThreedClass>(_3dClass));
                }
            }
        }

        /// <summary>
        /// Processes a command buffer.
        /// </summary>
        /// <param name="baseGpuVa">Base GPU virtual address of the command buffer</param>
        /// <param name="commandBuffer">Command buffer</param>
        public void Process(ulong baseGpuVa, ReadOnlySpan<int> commandBuffer)
        {
            for (int index = 0; index < commandBuffer.Length; index++)
            {
                int command = commandBuffer[index];

                ulong gpuVa = baseGpuVa + (ulong)index * 4;

                if (_state.MethodCount != 0)
                {
                    if (TryFastI2mBufferUpdate(commandBuffer, ref index))
                    {
                        continue;
                    }

                    Send(gpuVa, _state.Method, command, _state.SubChannel, _state.MethodCount <= 1);

                    if (!_state.NonIncrementing)
                    {
                        _state.Method++;
                    }

                    if (_state.IncrementOnce)
                    {
                        _state.NonIncrementing = true;
                    }

                    _state.MethodCount--;
                }
                else
                {
                    CompressedMethod meth = Unsafe.As<int, CompressedMethod>(ref command);

                    if (TryFastUniformBufferUpdate(meth, commandBuffer, index))
                    {
                        index += meth.MethodCount;
                        continue;
                    }

                    switch (meth.SecOp)
                    {
                        case SecOp.IncMethod:
                        case SecOp.NonIncMethod:
                        case SecOp.OneInc:
                            _state.Method = meth.MethodAddress;
                            _state.SubChannel = meth.MethodSubchannel;
                            _state.MethodCount = meth.MethodCount;
                            _state.IncrementOnce = meth.SecOp == SecOp.OneInc;
                            _state.NonIncrementing = meth.SecOp == SecOp.NonIncMethod;
                            break;
                        case SecOp.ImmdDataMethod:
                            Send(gpuVa, meth.MethodAddress, meth.ImmdData, meth.MethodSubchannel, true);
                            break;
                    }
                }
            }

            _3dClass.FlushUboDirty();
        }

        /// <summary>
        /// Tries to perform a fast Inline-to-Memory data update.
        /// If successful, all data will be copied at once, and <see cref="DmaState.MethodCount"/>
        /// command buffer entries will be consumed.
        /// </summary>
        /// <param name="commandBuffer">Command buffer where the data is contained</param>
        /// <param name="offset">Offset at <paramref name="commandBuffer"/> where the data is located, auto-incremented on success</param>
        /// <returns>True if the fast copy was successful, false otherwise</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private bool TryFastI2mBufferUpdate(ReadOnlySpan<int> commandBuffer, ref int offset)
        {
            if (_state.Method == LoadInlineDataMethodOffset && _state.NonIncrementing && _state.SubChannel <= 2 && !_context.Nx2Ampere)
            {
                int availableCount = commandBuffer.Length - offset;
                int consumeCount = Math.Min(_state.MethodCount, availableCount);

                ReadOnlySpan<int> data = commandBuffer.Slice(offset, consumeCount);

                if (_state.SubChannel == 0)
                {
                    _3dClass.LoadInlineData(data);
                }
                else if (_state.SubChannel == 1)
                {
                    _computeClass.LoadInlineData(data);
                }
                else /* if (_state.SubChannel == 2) */
                {
                    _i2mClass.LoadInlineData(data);
                }

                offset += consumeCount - 1;
                _state.MethodCount -= consumeCount;

                return true;
            }

            return false;
        }

        /// <summary>
        /// Tries to perform a fast constant buffer data update.
        /// If successful, all data will be copied at once, and <see cref="CompressedMethod.MethodCount"/> + 1
        /// command buffer entries will be consumed.
        /// </summary>
        /// <param name="meth">Compressed method to be checked</param>
        /// <param name="commandBuffer">Command buffer where <paramref name="meth"/> is contained</param>
        /// <param name="offset">Offset at <paramref name="commandBuffer"/> where <paramref name="meth"/> is located</param>
        /// <returns>True if the fast copy was successful, false otherwise</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private bool TryFastUniformBufferUpdate(CompressedMethod meth, ReadOnlySpan<int> commandBuffer, int offset)
        {
            int availableCount = commandBuffer.Length - offset;

            if (!_context.Nx2Ampere &&
                meth.MethodAddress == UniformBufferUpdateDataMethodOffset &&
                meth.MethodCount < availableCount &&
                meth.SecOp == SecOp.NonIncMethod)
            {
                _3dClass.ConstantBufferUpdate(commandBuffer.Slice(offset + 1, meth.MethodCount));

                return true;
            }

            return false;
        }

        /// <summary>
        /// Sends a uncompressed method for processing by the graphics pipeline.
        /// </summary>
        /// <param name="gpuVa">GPU virtual address where the command word is located</param>
        /// <param name="meth">Method to be processed</param>
        private void Send(ulong gpuVa, int offset, int argument, int subChannel, bool isLastCall)
        {
            if (offset < 0x60)
            {
                if (_context.Nx2Ampere && offset is >= 0x45 and <= 0x48)
                {
                    // Macro code is 96-bit Turing instructions, kept apart from the Maxwell macros. A
                    // channel has one macro RAM for all its classes: the 3D queue uploads through its 3D
                    // subchannel and calls the same code on compute, compute queues upload through theirs.
                    Nx2MacroEngine macros = _nx2Macros;

                    switch (offset)
                    {
                        case 0x45:
                            macros.SetInstructionPointer((uint)argument);
                            break;
                        case 0x46:
                            macros.AddCode((uint)argument);
                            break;
                        case 0x47:
                            macros.SetStartAddressPointer((uint)argument);
                            break;
                        case 0x48:
                            macros.Bind((uint)argument);
                            break;
                    }

                    return;
                }

                if (_context.Nx2Ampere)
                {
                    _nx2FifoRegisters[offset] = (uint)argument;

                    if (offset is >= 0x17 and <= 0x1b)
                    {
                        if (offset == 0x1b)
                        {
                            Nx2SemaphoreExecute((uint)argument);
                        }

                        return;
                    }
                }

                _fifoClass.Write(offset * 4, argument);
            }
            else if (_context.Nx2Ampere)
            {
                // Switch 2 channels bind Ampere classes and drive them through Turing MME64 macros.
                // Their methods go to the channel's own register files; nothing is drawn from them yet.
                Nx2Engine engine = GetNx2Engine(subChannel);

                if (engine == null)
                {
                    // Inline-to-memory and copy engines keep their Maxwell implementations: NVN2 writes
                    // its QMDs and command lists through them. The 2D engine is not used.
                    if (offset < 0xe00)
                    {
                        if (subChannel == 2)
                        {
                            _i2mClass.Write(offset * 4, argument);
                        }
                        else if (subChannel == 4)
                        {
                            _dmaClass.Write(offset * 4, argument);
                        }
                    }
                }
                else if (offset < Nx2Engine.RegisterCount)
                {
                    engine.Write((uint)offset, (uint)argument);
                }
                else
                {
                    engine.WriteMacro(offset, (uint)argument, isLastCall);
                }
            }
            else if (offset < 0xe00)
            {
                offset *= 4;

                switch (subChannel)
                {
                    case 0:
                        _3dClass.Write(offset, argument);
                        break;
                    case 1:
                        _computeClass.Write(offset, argument);
                        break;
                    case 2:
                        _i2mClass.Write(offset, argument);
                        break;
                    case 3:
                        _2dClass.Write(offset, argument);
                        break;
                    case 4:
                        _dmaClass.Write(offset, argument);
                        break;
                }
            }
            else
            {
                IDeviceState state = subChannel switch
                {
                    0 => _3dClass,
                    3 => _2dClass,
                    _ => null,
                };

                if (state != null)
                {
                    int macroIndex = (offset >> 1) & MacroIndexMask;

                    if ((offset & 1) != 0)
                    {
                        _fifoClass.MmePushArgument(macroIndex, gpuVa, argument);
                    }
                    else
                    {
                        _fifoClass.MmeStart(macroIndex, argument);
                    }

                    if (isLastCall)
                    {
                        _fifoClass.CallMme(macroIndex, state);

                        _3dClass.PerformDeferredDraws();
                    }
                }
            }
        }

        private struct Nx2ParkedMethod
        {
            public DmaState State;
            public ulong ResumeAddress; // Where the entry that continues the method starts
        }

        private const int Nx2MaxParkedMethods = 16;

        private static readonly bool _nx2ParkSplitMethods = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("RYUJINX_NX2_PARK_SPLIT_METHODS")) && Environment.GetEnvironmentVariable("RYUJINX_NX2_PARK_SPLIT_METHODS") != "0";

        private readonly System.Collections.Generic.List<Nx2ParkedMethod> _nx2ParkedMethods = [];
        private ulong _nx2SplitMethodEnd; // End of the last entry that ended inside a method
        private readonly uint[] _nx2FifoRegisters = new uint[0x60];
        private int _nx2Warnings;

        /// <summary>
        /// Switch 2: true while the channel is suspended on a semaphore acquire that is not met yet.
        /// </summary>
        public bool Nx2IsSuspended
        {
            get
            {
                if (_fifoClass.Nx2PendingAcquire is Nx2PendingAcquire acquire)
                {
                    if (!acquire.IsMet(MemoryManager))
                    {
                        return true;
                    }

                    _fifoClass.Nx2PendingAcquire = null;
                }

                return false;
            }
        }

        /// <summary>
        /// Switch 2: the acquire the channel is suspended on, for diagnostics.
        /// </summary>
        public Nx2PendingAcquire? Nx2PendingAcquire => _fifoClass.Nx2PendingAcquire;

        /// <summary>
        /// Switch 2: gives up on the acquire the channel is suspended on.
        /// </summary>
        public void Nx2ForceResume()
        {
            _fifoClass.Nx2PendingAcquire = null;
        }

        /// <summary>
        /// Processes a command buffer of a Switch 2 channel, stopping at a semaphore acquire that is not met.
        /// </summary>
        /// <param name="baseGpuVa">Base GPU virtual address of the command buffer</param>
        /// <param name="commandBuffer">Command buffer</param>
        /// <param name="start">Index of the first word to process (not 0 when resuming)</param>
        /// <returns>Index of the next word to process; the length of the buffer once it is done</returns>
        public int Nx2Process(ulong baseGpuVa, ReadOnlySpan<int> commandBuffer, int start)
        {
            GpuContext.Nx2CurrentChannel = GetHashCode();

            // A method's data continues across GP entries, whatever their level: NVN2 ends a main entry
            // after a macro's first word and supplies the remaining parameters in a subroutine entry.
            // RYUJINX_NX2_PARK_SPLIT_METHODS restores the older guess (an entry elsewhere is a call that
            // starts clean; the split method resumes where it ended).
            if (start == 0)
            {
                Nx2ResolveSplitMethod(baseGpuVa, commandBuffer);
            }

            for (int index = start; index < commandBuffer.Length; index++)
            {
                int command = commandBuffer[index];

                ulong gpuVa = baseGpuVa + (ulong)index * 4;

                if (_state.MethodCount != 0)
                {
                    Send(gpuVa, _state.Method, command, _state.SubChannel, _state.MethodCount <= 1);

                    if (!_state.NonIncrementing)
                    {
                        _state.Method++;
                    }

                    if (_state.IncrementOnce)
                    {
                        _state.NonIncrementing = true;
                    }

                    _state.MethodCount--;
                }
                else
                {
                    CompressedMethod meth = Unsafe.As<int, CompressedMethod>(ref command);

                    switch (meth.SecOp)
                    {
                        case SecOp.IncMethod:
                        case SecOp.NonIncMethod:
                        case SecOp.OneInc:
                            _state.Method = meth.MethodAddress;
                            _state.SubChannel = meth.MethodSubchannel;
                            _state.MethodCount = meth.MethodCount;
                            _state.IncrementOnce = meth.SecOp == SecOp.OneInc;
                            _state.NonIncrementing = meth.SecOp == SecOp.NonIncMethod;
                            break;
                        case SecOp.ImmdDataMethod:
                            Send(gpuVa, meth.MethodAddress, meth.ImmdData, meth.MethodSubchannel, true);
                            break;
                    }
                }

                if (_fifoClass.Nx2PendingAcquire.HasValue)
                {
                    System.Threading.Interlocked.Increment(ref _nx2Suspensions);

                    return index + 1;
                }
            }

            if (_state.MethodCount != 0)
            {
                _nx2SplitMethodEnd = baseGpuVa + (ulong)commandBuffer.Length * 4;
            }

            if (Nx2Maxwell)
            {
                try
                {
                    GpuContext.Nx2Phase = "deferred draws";
                    _3dClass.PerformDeferredDraws();
                    GpuContext.Nx2Phase = "processing";
                }
                catch (Nx2DrawSkippedException)
                {
                    _nx2SkippedDraws++;
                }

                _3dClass.FlushUboDirty();
            }

            return commandBuffer.Length;
        }

        /// <summary>
        /// Switch 2: a submission ended. A split method resumes within the submission that split it; ring
        /// addresses repeat every frame, so a stale one could otherwise swallow the headers of a later entry.
        /// </summary>
        public void Nx2EndSubmission()
        {
            if (_nx2ParkedMethods.Count != 0 && _nx2Warnings++ < 16)
            {
                Nx2ParkedMethod parked = _nx2ParkedMethods[0];

                Logger.Warning?.Print(LogClass.Gpu, $"Switch 2 submission ended with parked method 0x{parked.State.Method:x} (sub {parked.State.SubChannel}, {parked.State.MethodCount} words left, resume 0x{parked.ResumeAddress:x})");
            }

            _nx2ParkedMethods.Clear();
        }

        // NVN on Switch 2 ends GPFIFO entries in the middle of a method's data and supplies the rest in other
        // entries (a macro's parameters in a 5-word entry, a 381-word upload in entries of 1 and 380 words),
        // then continues at the address where the split entry ended: method state simply carries over.
        // RYUJINX_NX2_PARK_SPLIT_METHODS=1 restores the older guess, where an entry elsewhere is a call that
        // starts clean and the method resumes when the stream continues at its address.
        private void Nx2ResolveSplitMethod(ulong entryAddress, ReadOnlySpan<int> commandBuffer)
        {
            Nx2ParkedMethod? resume = null;

            for (int i = 0; i < _nx2ParkedMethods.Count; i++)
            {
                if (_nx2ParkedMethods[i].ResumeAddress == entryAddress)
                {
                    resume = _nx2ParkedMethods[i];
                    _nx2ParkedMethods.RemoveAt(i);
                    break;
                }
            }

            if (_state.MethodCount != 0 && entryAddress != _nx2SplitMethodEnd &&
                _nx2ParkSplitMethods)
            {
                if (_nx2ParkedMethods.Count == Nx2MaxParkedMethods)
                {
                    _nx2ParkedMethods.RemoveAt(0);
                }

                _nx2ParkedMethods.Add(new Nx2ParkedMethod { State = _state, ResumeAddress = _nx2SplitMethodEnd });
                _state.MethodCount = 0;
            }

            if (resume.HasValue && _state.MethodCount == 0)
            {
                _state = resume.Value.State;
            }
        }

        // Ampere SEM_EXECUTE (method 0x1b): operation in bits 2:0 on the semaphore at SEM_ADDR_HI:SEM_ADDR_LO
        // (methods 0x18/0x17) with payload SEM_PAYLOAD_LO (0x19).
        private void Nx2SemaphoreExecute(uint argument)
        {
            ulong address = ((ulong)(_nx2FifoRegisters[0x18] & 0x1ffffff) << 32) | (_nx2FifoRegisters[0x17] & ~3u);
            uint payload = _nx2FifoRegisters[0x19];
            uint operation = argument & 7;

            if (!MemoryManager.IsMapped(address))
            {
                if (_nx2Warnings++ < 16)
                {
                    Logger.Warning?.Print(LogClass.Gpu, $"Switch 2 SEM_EXECUTE op {operation} on unmapped address 0x{address:X}");
                }

                return;
            }

            if (operation == 1) // RELEASE
            {
                _context.Nx2SyncBeforeRelease(_channel.TextureManager);
                MemoryManager.Write(address, payload);
                GpuContext.Nx2RecordSemaphore("sem_execute", address, payload);
                _context.Nx2NoteDirectRelease(address);
                _context.Nx2WriteMirror(MemoryManager, address, payload);

                return;
            }

            // Acquires: checked once, the GPU thread is never blocked on them.
            uint word = GpuContext.Nx2ReadSemaphore(MemoryManager, address);
            bool ready = operation switch
            {
                0 => word == payload,
                2 => word >= payload,
                3 => (int)(word - payload) >= 0,
                4 => (word & payload) != 0,
                5 => (word & payload) == 0,
                _ => true,
            };

            if (!ready && _nx2Warnings++ < 16)
            {
                Logger.Warning?.Print(LogClass.Gpu, $"Switch 2 SEM_EXECUTE op {operation} at 0x{address:X}: 0x{word:x} vs 0x{payload:x} not met, continuing");
            }
        }

        private readonly uint[] _nx2DataRam = new uint[TuringMacroInterpreter.DataRamSize];
        private readonly Nx2MacroEngine _nx2Macros = new();

        private Nx2Engine _nx2Threed;
        private Nx2Engine _nx2Compute;

        /// <summary>
        /// Gets the Switch 2 engine a subchannel's methods go to. NVN binds 3D to subchannel 0 and compute
        /// to subchannel 1; the macro data RAM belongs to the channel.
        /// </summary>
        /// <param name="subChannel">Subchannel index</param>
        /// <returns>The engine, or null if the subchannel has none</returns>
        /// <summary>
        /// RYUJINX_NX2_MAXWELL=1: Switch 2 3D methods also run on the Maxwell 3D engine, so its state
        /// tracking and draws see them. Shaders are still SM86 code the Maxwell decoder can't read.
        /// </summary>
        internal static readonly bool Nx2Maxwell = Environment.GetEnvironmentVariable("RYUJINX_NX2_MAXWELL") == "1";

        /// <summary>
        /// RYUJINX_NX2_COMPUTE=1 (with RYUJINX_NX2_MAXWELL=1): QMDs run their programs. Off by default: a
        /// compute program running on wrong data can hang the GPU, which on a Mac also stalls the screen
        /// until WindowServer is killed.
        /// </summary>
        internal static readonly bool Nx2Compute = Nx2Maxwell && Environment.GetEnvironmentVariable("RYUJINX_NX2_COMPUTE") == "1";

        private Nx2Engine CreateNx2Threed()
        {
            Nx2Engine engine = new(_context, _channel, "3D", _nx2DataRam, _nx2Macros);

            if (Nx2Maxwell)
            {
                engine.MethodWritten = ForwardToMaxwell3D;
                engine.Dispatch = Nx2Compute ? _computeClass.Nx2Dispatch : null;
            }

            return engine;
        }

        private void ForwardToMaxwell3D(uint method, uint value)
        {
            switch (method)
            {
                case >= 0x45 and <= 0x48:   // Macro upload: Turing code, kept by the Switch 2 engine
                case >= 0x60 and <= 0x6d:   // Inline-to-memory: run by the Switch 2 engine
                case >= 0x6c0 and <= 0x6c3: // Report semaphore: run by the Switch 2 engine
                    return;
            }

            if (_nx2FailingMethods.Contains(method) || method < _nx2ForwardLow || method >= _nx2ForwardHigh || _nx2BlockedMethods.Contains(method))
            {
                return;
            }

            if (method is >= 0x8e4 and < 0x904)
            {
                // LOAD_CONSTANT_BUFFER into the buffer SET_CONSTANT_BUFFER_SELECTOR (0x8e0-0x8e2) names, at
                // the offset in 0x8e3. NVN2 also patches memory other engines read right after (QMD release
                // payloads) this way, so the word is written now, not batched like Maxwell's updates.
                uint[] regs = _nx2Threed.Registers;
                ulong address = ((ulong)regs[0x8e1] << 32 | regs[0x8e2]) + regs[0x8e3];

                if (MemoryManager.IsMapped(address))
                {
                    GpuContext.Nx2Phase = "constant buffer update";
                    MemoryManager.Write(address, value);

                    // As Maxwell's constant buffer updater does: buffers only look for CPU writes once per
                    // sequence, so without this the next draws read the old word (each slice of a volume
                    // drawn with the same slice index, objects drawn with another draw's transform).
                    if (!_nx2NoCbDirty)
                    {
                        MemoryManager.Physical.BufferCache.ForceDirty(MemoryManager, address, 4);
                    }
                    GpuContext.Nx2Phase = "processing";
                    GpuContext.Nx2WatchWrite(address, 4, () => $"constant buffer update 0x{value:x}");
                }
                else if (_nx2UnmappedUbo++ < 16)
                {
                    Logger.Warning?.Print(LogClass.Gpu, $"Switch 2 constant buffer update at unmapped 0x{address:X} (size 0x{regs[0x8e0]:x}), dropped");
                }

                regs[0x8e3] += 4;
                _3dClass.Write(0x8e3 * 4, (int)regs[0x8e3]);

                return;
            }

            try
            {
                GpuContext.Nx2Phase = $"Maxwell 3D method 0x{method:x} = 0x{value:x}";
                _3dClass.Nx2FlushDeferredDrawBeforeWrite((int)method * 4, (int)value);
                _3dClass.Write((int)method * 4, (int)value);
                GpuContext.Nx2Phase = "processing";
            }
            catch (Nx2DrawSkippedException)
            {
                _nx2SkippedDraws++;
            }
            catch (Exception exception)
            {
                // A Maxwell method Ampere uses differently: drop it from now on.
                _nx2FailingMethods.Add(method);

                Logger.Warning?.Print(LogClass.Gpu, $"Switch 2 3D method 0x{method:x} = 0x{value:x} failed on the Maxwell engine and is dropped from now on: {exception.GetType().Name}: {exception.Message}\n{exception.StackTrace}");
            }
        }

        private static readonly System.Collections.Generic.HashSet<uint> _nx2FailingMethods = [];

        // RYUJINX_NX2_MAXWELL_RANGE=<low hex>-<high hex>: forward only those methods (to bisect problems).
        private static readonly uint _nx2ForwardLow = ParseForwardRange(0);

        // RYUJINX_NX2_MAXWELL_BLOCK=<hex>,<hex>,...: methods never forwarded (to bisect problems).
        private static readonly System.Collections.Generic.HashSet<uint> _nx2BlockedMethods =
            [.. (Environment.GetEnvironmentVariable("RYUJINX_NX2_MAXWELL_BLOCK") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries).Select(x => Convert.ToUInt32(x, 16))];
        private static readonly uint _nx2ForwardHigh = ParseForwardRange(1);

        private static uint ParseForwardRange(int index)
        {
            string[] parts = Environment.GetEnvironmentVariable("RYUJINX_NX2_MAXWELL_RANGE")?.Split('-');

            return parts is { Length: 2 } ? Convert.ToUInt32(parts[index], 16) : index == 0 ? 0u : 0xe00u;
        }

        private long _nx2SkippedDraws;
        private long _nx2Suspensions;

        /// <summary>
        /// Switch 2: how many times the channel suspended on a semaphore acquire.
        /// </summary>
        public long Nx2Suspensions => System.Threading.Interlocked.Read(ref _nx2Suspensions);
        private int _nx2UnmappedUbo;

        // RYUJINX_NX2_NO_CB_DIRTY=1: constant buffer updates no longer mark the buffer dirty (to compare).
        private static readonly bool _nx2NoCbDirty = Environment.GetEnvironmentVariable("RYUJINX_NX2_NO_CB_DIRTY") == "1";

        private Nx2Engine CreateNx2Compute()
        {
            Nx2Engine engine = new(_context, _channel, "compute", _nx2DataRam, _nx2Macros);

            if (Nx2Maxwell)
            {
                // The compute class state (texture and sampler pools, bindless constant buffer) for dispatches.
                engine.MethodWritten = (method, value) =>
                {
                    if (method is (>= 0x45 and <= 0x48) or (>= 0x60 and <= 0x6d) or (>= 0xad and <= 0xb0) or (>= 0x6c0 and <= 0x6c3) or >= 0xe00)
                    {
                        return;
                    }

                    _computeClass.Write((int)method * 4, (int)value);
                };

                engine.Dispatch = Nx2Compute ? _computeClass.Nx2Dispatch : null;
            }

            return engine;
        }

        private Nx2Engine GetNx2Engine(int subChannel)
        {
            return subChannel switch
            {
                0 => _nx2Threed ??= CreateNx2Threed(),
                1 => _nx2Compute ??= CreateNx2Compute(),
                _ => null,
            };
        }

        /// <summary>
        /// Writes data directly to the state of the specified class.
        /// </summary>
        /// <param name="classId">ID of the class to write the data into</param>
        /// <param name="offset">State offset in bytes</param>
        /// <param name="value">Value to be written</param>
        public void Write(ClassId classId, int offset, int value)
        {
            switch (classId)
            {
                case ClassId.Threed:
                    _3dClass.Write(offset, value);
                    break;
                case ClassId.Compute:
                    _computeClass.Write(offset, value);
                    break;
                case ClassId.InlineToMemory:
                    _i2mClass.Write(offset, value);
                    break;
                case ClassId.Twod:
                    _2dClass.Write(offset, value);
                    break;
                case ClassId.Dma:
                    _dmaClass.Write(offset, value);
                    break;
                case ClassId.GPFifo:
                    _fifoClass.Write(offset, value);
                    break;
            }
        }

        /// <summary>
        /// Sets the shadow ram control value of all sub-channels.
        /// </summary>
        /// <param name="control">New shadow ram control value</param>
        public void SetShadowRamControl(int control)
        {
            _3dClass.SetShadowRamControl(control);
        }

        /// <summary>
        /// Forces a full host state update by marking all state as modified,
        /// and also requests all GPU resources in use to be rebound.
        /// </summary>
        public void ForceAllDirty()
        {
            _3dClass.ForceStateDirty();
            _channel.BufferManager.Rebind();
            _channel.TextureManager.Rebind();
        }

        /// <summary>
        /// Perform any deferred draws.
        /// </summary>
        public void PerformDeferredDraws()
        {
            _3dClass.PerformDeferredDraws();
        }

        protected virtual void Dispose(bool disposing)
        {
            if (disposing)
            {
                _3dClass.Dispose();
            }
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }
    }
}
