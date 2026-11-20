using Ryujinx.Common.Logging;
using Ryujinx.Graphics.Gpu.Memory;
using System;
using System.Collections.Generic;

namespace Ryujinx.Graphics.Gpu.Engine.Nx2
{
    /// <summary>
    /// One engine (3D or compute) of a Switch 2 channel: the register file its methods and macros write,
    /// and the macro call in progress.
    /// </summary>
    class Nx2Engine
    {
        public const int RegisterCount = 0xe00;

        // Turing MME memory reads: the source address, the data RAM address, and the two read triggers.
        private const uint MemAddressA = 0x154;
        private const uint MemAddressB = 0x155;
        private const uint DataRamAddress = 0x156;
        private const uint DmaRead = 0x157;
        private const uint DmaReadFifoed = 0x158;

        // Compute class: the QMD address (>> 8), the PCAS signals, and the inline QMD registers.
        private const uint InlineQmdControl = 0xa7;
        private const uint SendPcasA = 0xad;
        private const uint SendSignalingPcasB = 0xaf;
        private const uint SendSignalingPcas2B = 0xb0;
        private const uint InlineQmdAddress = 0xc6;
        private const uint InlineQmdData = 0xc8;
        private const uint InlineQmdLaunch = 0x107;

        private const uint ReportSemaphoreA = 0x6c0;
        private const uint ReportSemaphoreD = 0x6c3;
        private const uint FirmwareCall4 = 0x8c4;
        private const uint ShadowScratch = 0xd00;

        private readonly GpuContext _context;
        private readonly GpuChannel _channel;
        private readonly string _name;
        private readonly bool _isCompute;
        private readonly InlineToMemory.InlineToMemoryClass _inlineToMemory;

        // Inline-to-memory methods (LINE_LENGTH_IN 0x60 to LOAD_INLINE_DATA 0x6d).
        private const uint InlineToMemoryStart = 0x60;
        private const uint InlineToMemoryEnd = 0x6e;
        private readonly uint[] _dataRam;
        private readonly List<uint> _parameters = [];
        private readonly bool[] _seen = new bool[RegisterCount];
        private int _macroIndex = -1;
        private int _failures;
        private static readonly bool _trace = Environment.GetEnvironmentVariable("RYUJINX_NX2_TRACE_CB") == "1";
        private int _traced;
        private int _macroDepth;
        private uint _macroStart;
        private readonly System.Collections.Generic.HashSet<uint> _dumpedMacros = [];
        private bool _qmdDumped;

        public uint[] Registers { get; } = new uint[RegisterCount];

        /// <summary>
        /// Called for every register write, from a method or from a macro.
        /// </summary>
        public System.Action<uint, uint> MethodWritten { get; set; }

        /// <summary>
        /// Runs the program of a QMD (64 words) with a non-empty grid; returns false if it can't.
        /// </summary>
        public System.Func<uint[], bool> Dispatch { get; set; }

        public Nx2MacroEngine Macros { get; }

        public Nx2Engine(GpuContext context, GpuChannel channel, string name, uint[] dataRam, Nx2MacroEngine macros)
        {
            Macros = macros;
            _isCompute = name == "compute";
            _inlineToMemory = new InlineToMemory.InlineToMemoryClass(context, channel);
            _context = context;
            _channel = channel;
            _name = name;
            _dataRam = dataRam;
        }

        /// <summary>
        /// Writes a method of the engine.
        /// </summary>
        public void Write(uint method, uint value)
        {
            if (method >= RegisterCount)
            {
                return;
            }

            if (!_seen[method])
            {
                _seen[method] = true;
                Logger.Debug?.Print(LogClass.Gpu, $"Switch 2 {_name} method first write: 0x{method:x} = 0x{value:x}");
            }

            Registers[method] = value;

            if (_trace && method is >= 0x8e0 and <= 0x8e3 && _traced++ < 200000)
            {
                Logger.Info?.Print(LogClass.Gpu, $"Switch 2 {_name} cb selector 0x{method:x} = 0x{value:x} ({(_macroDepth != 0 ? $"macro start 0x{_macroStart:x}" : "method")})");
            }

            if (method is >= InlineToMemoryStart and < InlineToMemoryEnd)
            {
                // The 3D and compute classes carry their own inline-to-memory block; NVN2 writes QMDs and
                // command lists through it, from methods and from macros.
                _inlineToMemory.Write((int)method * 4, (int)value);
            }

            if (method == FirmwareCall4)
            {
                // Firmware call 4 changes some registers depending on its parameters. Macros wait for it
                // by polling the first MME scratch register.
                Registers[ShadowScratch] = 1;
            }

            if (method == ReportSemaphoreD)
            {
                ReportSemaphore(value);
            }

            if (_isCompute)
            {
                WriteCompute(method, value);
            }

            MethodWritten?.Invoke(method, value);
        }

        private void WriteCompute(uint method, uint value)
        {
            switch (method)
            {
                case InlineQmdLaunch:
                    {
                        // An inline QMD: 256 bytes of registers written to the QMD's address, then launched.
                        ulong address = (((ulong)Registers[InlineQmdAddress] << 32) | Registers[InlineQmdAddress + 1]) << 8;
                        MemoryManager memory = _channel.MemoryManager;

                        if (!memory.IsMapped(address))
                        {
                            Warn($"unmapped inline QMD destination 0x{address:X}");
                            break;
                        }

                        memory.Write(address, System.Runtime.InteropServices.MemoryMarshal.AsBytes(Registers.AsSpan((int)InlineQmdData, Nx2QmdScheduler.DescriptorWords)));

                        if ((Registers[InlineQmdControl] & (1u << 24)) != 0 || (Registers[InlineQmdData + 4] & (1u << 12)) == 0)
                        {
                            SignalQmd(address, 3);
                        }

                        break;
                    }
                case SendSignalingPcasB:
                    if ((value & 2) != 0)
                    {
                        SignalQmd((ulong)Registers[SendPcasA] << 8, value);
                    }
                    break;
                case SendSignalingPcas2B:
                    if ((value & 15) is not (0 or 1))
                    {
                        SignalQmd((ulong)Registers[SendPcasA] << 8, value);
                    }
                    break;
            }
        }

        private void SignalQmd(ulong qmdAddress, uint argument)
        {
            MemoryManager memory = _channel.MemoryManager;

            _context.Nx2Qmd.CurrentChannel = GpuContext.Nx2CurrentChannel;

            string error = _context.Nx2Qmd.Signal(
                qmdAddress,
                argument,
                (address, descriptor) =>
                {
                    if (!memory.IsMapped(address))
                    {
                        return false;
                    }

                    memory.GetSpan(address, descriptor.Length * sizeof(uint), tracked: true).CopyTo(System.Runtime.InteropServices.MemoryMarshal.AsBytes(descriptor.AsSpan()));

                    return true;
                },
                (address, payload, flags) =>
                {
                    if (!memory.IsMapped(address))
                    {
                        return false;
                    }

                    bool fourWords = (flags >> 30) == 0;
                    uint released = payload;
                    System.Func<uint, uint> combine = null;

                    if ((flags & (1u << 26)) != 0)
                    {
                        combine = current => Nx2QmdScheduler.ReduceSemaphore((flags >> 20) & 7, ((flags >> 24) & 1) != 0, current, released);
                    }
                    else if (!fourWords && (flags & (1u << 19)) != 0)
                    {
                        // NVN2 tracks QMD completion with a max release into a word other releases also
                        // advance: a plain write would move it backwards.
                        combine = current => System.Math.Max(current, released);
                    }

                    _context.Nx2SyncBeforeRelease(_channel.TextureManager);

                    uint before = memory.Read<uint>(address, tracked: true);

                    if (combine != null)
                    {
                        payload = combine(before);
                    }

                    memory.Write(address, payload);

                    if (combine != null)
                    {
                        _context.Nx2NoteCombinedRelease(address, before, payload, combine);
                    }
                    else
                    {
                        _context.Nx2NoteDirectRelease(address);
                    }
                    GpuContext.Nx2RecordSemaphore("qmd", address, payload, qmdAddress);
                    GpuContext.Nx2WatchSemaphore(address, $"QMD 0x{qmdAddress:X} release 0x{payload:x} (flags 0x{flags:x})");

                    if (fourWords && (flags & (1u << 26)) == 0)
                    {
                        memory.Write(address + 4, 0u);
                        memory.Write(address + 8, _context.GetTimestamp());
                    }

                    _context.Nx2WriteMirror(memory, address, payload);

                    _context.GPFifo.Nx2OnSemaphoreWritten();

                    return true;
                },
                // Without a dispatcher (RYUJINX_NX2_MAXWELL unset) no program runs; the QMD still completes.
                descriptor => Dispatch?.Invoke(descriptor) ?? false);

            if (error != null && error != "native shader dispatch required")
            {
                Warn($"QMD 0x{qmdAddress:X} action {argument & 15}: {error}");

                if (!_qmdDumped && memory.IsMapped(qmdAddress))
                {
                    _qmdDumped = true;

                    uint[] words = new uint[Nx2QmdScheduler.DescriptorWords];

                    memory.GetSpan(qmdAddress, words.Length * sizeof(uint)).CopyTo(System.Runtime.InteropServices.MemoryMarshal.AsBytes(words.AsSpan()));
                    Warn($"QMD words: {string.Join(",", Array.ConvertAll(words, x => x.ToString("x")))}");
                }
            }
        }

        private void Warn(string message)
        {
            if (_failures++ < 64)
            {
                Logger.Warning?.Print(LogClass.Gpu, $"Switch 2 {_name}: {message}");
            }
        }

        // SET_REPORT_SEMAPHORE_A-D (0x6c0-0x6c3): address high/low, payload, then operation in bits 1:0
        // (release / acquire / report only / trap), reduction enable in bit 3 with the operation in bits
        // 11:9, and a one-word (no timestamp) structure in bit 28. NVN2 releases its queue syncs this way.
        private static readonly System.Collections.Generic.HashSet<(string, uint)> _reportTypesSeen = [];

        private void ReportSemaphore(uint control)
        {
            ulong address = ((ulong)(Registers[ReportSemaphoreA] & 0x1ffffff) << 32) | Registers[ReportSemaphoreA + 1];
            uint payload = Registers[ReportSemaphoreA + 2];
            uint operation = control & 3;
            bool oneWord = ((control >> 28) & 1) != 0;
            MemoryManager memory = _channel.MemoryManager;

            if (operation is not (0 or 2) || !memory.IsMapped(address))
            {
                return;
            }

            if (operation == 2)
            {
                uint type = (control >> 23) & 0x1f;

                // A counter report. Type 0 reports the payload (with a timestamp); no other counter is kept yet.
                if (type != 0)
                {
                    payload = 0;
                }

                if (_reportTypesSeen.Add((_name, type)))
                {
                    Logger.Info?.Print(LogClass.Gpu, $"Switch 2 {_name} counter report type 0x{type:x} (control 0x{control:x}) at 0x{address:X}");
                }
            }
            System.Func<uint, uint> combine = null;
            uint before = 0;

            _context.Nx2SyncBeforeRelease(_channel.TextureManager);

            if (operation != 2 && ((control >> 3) & 1) != 0)
            {
                uint released = payload;

                combine = current => Nx2QmdScheduler.ReduceSemaphore((control >> 9) & 7, false, current, released);
                before = memory.Read<uint>(address, tracked: true);
                payload = combine(before);
            }

            memory.Write(address, payload);

            if (combine != null)
            {
                _context.Nx2NoteCombinedRelease(address, before, payload, combine);
            }
            else
            {
                _context.Nx2NoteDirectRelease(address);
            }
            GpuContext.Nx2RecordSemaphore("report", address, payload);
            GpuContext.Nx2WatchSemaphore(address, $"{_name} report semaphore release 0x{payload:x} (control 0x{control:x})");

            if (!oneWord)
            {
                memory.Write(address + 4, 0u);
                memory.Write(address + 8, _context.GetTimestamp());
            }

            _context.Nx2WriteMirror(memory, address, payload);

            _context.GPFifo.Nx2OnSemaphoreWritten();
        }

        /// <summary>
        /// A word written to a macro method (0xe00 and up): even methods start a call, odd ones add a parameter.
        /// </summary>
        /// <param name="method">Method offset</param>
        /// <param name="argument">Word written</param>
        /// <param name="isLastCall">True if no more words of the call follow</param>
        public void WriteMacro(int method, uint argument, bool isLastCall)
        {
            if (_macroIndex < 0)
            {
                _macroIndex = ((method - RegisterCount) >> 1) % Nx2MacroEngine.MacroCount;
                _parameters.Clear();
            }

            _parameters.Add(argument);

            if (isLastCall)
            {
                int index = _macroIndex;

                _macroIndex = -1;
                Execute(index);
            }
        }

        private void Execute(int index)
        {
            Nx2MacroEngine macros = Macros;
            uint start = macros.Positions[index];

            _macroDepth++;
            _macroStart = start;

            TuringMacroInterpreter.Result result = macros.Interpreter.Execute(macros.Code, start, _parameters, _dataRam, Read, Emit);

            _macroDepth--;

            if (!result.Success && _failures++ < 64)
            {
                Logger.Error?.Print(LogClass.Gpu, $"Switch 2 {_name} macro {index:x} (start {start:x}) failed at {result.Pc:x}: {result.Error}, consumed {result.Consumed}/{_parameters.Count}");

                if (_dumpedMacros.Add(start))
                {
                    System.Text.StringBuilder dump = new();

                    for (uint pc = start; pc < start + 48 && pc * 3 + 2 < macros.Code.Length; pc++)
                    {
                        dump.Append($"\n  {pc:x}: {macros.Code[pc * 3]:x8} {macros.Code[pc * 3 + 1]:x8} {macros.Code[pc * 3 + 2]:x8}");
                    }

                    Logger.Error?.Print(LogClass.Gpu, $"parameters: {string.Join(",", _parameters.ConvertAll(x => x.ToString("x")))}{dump}");
                }
            }
        }

        private uint Read(uint address)
        {
            return address < RegisterCount ? Registers[address] : 0;
        }

        private bool Emit(uint address, uint value)
        {
            if (address >= RegisterCount)
            {
                // A macro never calls another.
                return false;
            }

            Write(address, value);

            if (address is DmaRead or DmaReadFifoed)
            {
                // MME_DMA_READ copies words into the data RAM, MME_DMA_READ_FIFOED into the load FIFO,
                // which the macro then reads after a load barrier.
                ulong source = ((ulong)(Registers[MemAddressA] & 0x1ffffff) << 32) | Registers[MemAddressB];
                uint ram = Registers[DataRamAddress];
                uint count = System.Math.Min(value, 0x1000);
                MemoryManager memory = _channel.MemoryManager;

                for (uint i = 0; i < count; i++)
                {
                    ulong va = source + (ulong)i * 4;
                    uint word = memory.IsMapped(va) ? memory.Read<uint>(va, tracked: true) : 0;

                    if (address == DmaReadFifoed)
                    {
                        _parameters.Add(word);
                    }
                    else if (ram + i < _dataRam.Length)
                    {
                        _dataRam[ram + i] = word;
                    }
                }
            }

            return true;
        }
    }
}
