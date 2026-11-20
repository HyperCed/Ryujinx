using Ryujinx.Common;
using Ryujinx.Common.Configuration;
using Ryujinx.Graphics.Device;
using Ryujinx.Graphics.GAL;
using Ryujinx.Graphics.Gpu.Engine.GPFifo;
using Ryujinx.Graphics.Gpu.Memory;
using Ryujinx.Graphics.Gpu.Shader;
using Ryujinx.Graphics.Gpu.Synchronization;
using System;
using System.Linq;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;

namespace Ryujinx.Graphics.Gpu
{
    /// <summary>
    /// GPU emulation context.
    /// </summary>
    public sealed class GpuContext : IDisposable
    {
        private const int NsToTicksFractionNumerator = 384;
        private const int NsToTicksFractionDenominator = 625;

        /// <summary>
        /// Event signaled when the host emulation context is ready to be used by the gpu context.
        /// </summary>
        public ManualResetEvent HostInitalized { get; }

        /// <summary>
        /// Host renderer.
        /// </summary>
        public IRenderer Renderer { get; }

        /// <summary>
        /// GPU General Purpose FIFO queue.
        /// </summary>
        public GPFifoDevice GPFifo { get; }

        /// <summary>
        /// GPU synchronization manager.
        /// </summary>
        public SynchronizationManager Synchronization { get; }

        /// <summary>
        /// True once a Switch 2 NVIDIA driver identified itself. Its channels bind Ampere classes and drive
        /// 3D and compute state through Turing MME64 macros, which the Maxwell engines can't run.
        /// </summary>
        public bool Nx2Ampere
        {
            get => _nx2Ampere;
            set
            {
                _nx2Ampere = value;

                // Switch 2 GPUs swap two bits inside every GOB of block-linear images.
                Ryujinx.Graphics.Texture.LayoutConverter.NativeTiling = value;
            }
        }

        private bool _nx2Ampere;

        /// <summary>
        /// Switch 2 compute launch scheduling, shared by every channel.
        /// </summary>
        internal Engine.Nx2.Nx2QmdScheduler Nx2Qmd { get; } = new();

        /// <summary>
        /// The 3D engines of the Switch 2 channels (Maxwell forwarding mode). A QMD can be dispatched
        /// while another channel still holds deferred (instanced) draws, which run later with the host
        /// pipeline state as it is then: they are performed before the compute program is bound.
        /// </summary>
        internal System.Collections.Generic.List<WeakReference<Engine.Threed.ThreedClass>> Nx2ThreedEngines { get; } = [];

        internal void Nx2PerformAllDeferredDraws()
        {
            lock (Nx2ThreedEngines)
            {
                for (int i = Nx2ThreedEngines.Count - 1; i >= 0; i--)
                {
                    if (Nx2ThreedEngines[i].TryGetTarget(out Engine.Threed.ThreedClass engine))
                    {
                        engine.PerformDeferredDraws();
                    }
                    else
                    {
                        Nx2ThreedEngines.RemoveAt(i);
                    }
                }
            }
        }


        private readonly System.Collections.Generic.HashSet<ulong> _nx2SemaphoreRegions = [];

        /// <summary>
        /// Registers a two-page Switch 2 queue semaphore region: GPU releases go to the second page, and
        /// the CPU and GPU waits read the same offset in the first.
        /// </summary>
        /// <param name="address">GPU virtual address of the region</param>
        private readonly Dictionary<ulong, ulong> _nx2GpuOnlyRegions = new();

        /// <summary>
        /// RYUJINX_NX2_ALLOC_TRACE=1: logs buffers and textures of 32 MiB or more as they are created.
        /// </summary>
        internal static readonly bool Nx2AllocationTrace = Environment.GetEnvironmentVariable("RYUJINX_NX2_ALLOC_TRACE") == "1";

        /// <summary>
        /// Registers a Switch 2 GPU-only (or sparse) region the graphics server backs: the CPU never writes it,
        /// so textures created there start from zero instead of loading (and committing) its memory.
        /// </summary>
        public void Nx2AddGpuOnlyRegion(ulong address, ulong size)
        {
            lock (_nx2GpuOnlyRegions)
            {
                _nx2GpuOnlyRegions[address] = size;
            }
        }

        private readonly Dictionary<ulong, ulong> _nx2SparseRegions = new();

        /// <summary>
        /// Switch 2 virtual textures (sparse host textures of the unbacked sparse regions).
        /// </summary>
        internal Image.Nx2VirtualTextures Nx2VirtualTextures { get; }

        /// <summary>
        /// The game mapped memory into a sparse region (gpu 216): the virtual textures there upload it.
        /// </summary>
        public void Nx2SparseMapped(ulong address, ulong size) => Nx2VirtualTextures.Mapped(address, size);

        /// <summary>
        /// Registers an unbacked Switch 2 sparse region (gpu 214): virtual textures there have only the tiles
        /// the game maps (216), and their first page may well be unmapped.
        /// </summary>
        public void Nx2AddSparseRegion(ulong address, ulong size)
        {
            lock (_nx2SparseRegions)
            {
                _nx2SparseRegions[address] = size;
            }
        }

        public void Nx2RemoveSparseRegion(ulong address)
        {
            lock (_nx2SparseRegions)
            {
                _nx2SparseRegions.Remove(address);
            }
        }

        /// <summary>
        /// Whether a GPU address lies in an unbacked Switch 2 sparse region.
        /// </summary>
        internal bool Nx2InSparseRegion(ulong address)
        {
            lock (_nx2SparseRegions)
            {
                foreach ((ulong start, ulong size) in _nx2SparseRegions)
                {
                    if (address >= start && address - start < size)
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        public void Nx2RemoveGpuOnlyRegion(ulong address)
        {
            lock (_nx2GpuOnlyRegions)
            {
                _nx2GpuOnlyRegions.Remove(address);
            }
        }

        /// <summary>
        /// Whether a GPU range lies in a backed Switch 2 GPU-only region.
        /// </summary>
        internal bool Nx2IsGpuOnly(ulong address, ulong size)
        {
            lock (_nx2GpuOnlyRegions)
            {
                foreach ((ulong start, ulong regionSize) in _nx2GpuOnlyRegions)
                {
                    if (address >= start && address - start < regionSize && size <= regionSize - (address - start))
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        public void Nx2AddSemaphoreRegion(ulong address)
        {
            lock (_nx2SemaphoreRegions)
            {
                _nx2SemaphoreRegions.Add(address);
            }
        }

        /// <summary>
        /// Unregisters a Switch 2 queue semaphore region.
        /// </summary>
        /// <param name="address">GPU virtual address of the region</param>
        public void Nx2RemoveSemaphoreRegion(ulong address)
        {
            lock (_nx2SemaphoreRegions)
            {
                _nx2SemaphoreRegions.Remove(address);
            }
        }

        /// <summary>
        /// Gets the address a semaphore release has to be mirrored to, if it lands on the second page of a
        /// Switch 2 queue semaphore region.
        /// </summary>
        /// <param name="address">GPU virtual address of the release</param>
        /// <param name="mirror">GPU virtual address of the mirror</param>
        /// <returns>True if the release has a mirror</returns>
        private static readonly ulong _nx2SemaphoreWatch = ulong.TryParse(Environment.GetEnvironmentVariable("RYUJINX_NX2_SEM_WATCH"), System.Globalization.NumberStyles.HexNumber, null, out ulong watch) ? watch & ~0xfUL : 0;

        /// <summary>
        /// RYUJINX_NX2_SEM_WATCH=&lt;hex GPU address&gt;: logs every Switch 2 semaphore operation on the 16 bytes there.
        /// </summary>
        private struct Nx2SemaphoreEvent
        {
            public int Channel;
            public ulong Address;
            public uint Value;
            public ulong Qmd;
            public string Kind;
        }

        private static readonly Nx2SemaphoreEvent[] _nx2SemaphoreEvents = new Nx2SemaphoreEvent[512];
        private static int _nx2SemaphoreEventIndex;

        /// <summary>
        /// Records a Switch 2 semaphore operation in a ring of recent ones, cheaply, for stall reports.
        /// </summary>
        internal static void Nx2RecordSemaphore(string kind, ulong address, uint value, ulong qmd = 0)
        {
            if (kind == "acquire" && _nx2AcquiredAddresses.TryAdd(address, 0))
            {
                _nx2AcquiredList = [.. _nx2AcquiredAddresses.Keys];
            }

            int index = Interlocked.Increment(ref _nx2SemaphoreEventIndex) & (_nx2SemaphoreEvents.Length - 1);

            _nx2SemaphoreEvents[index] = new Nx2SemaphoreEvent { Channel = Nx2CurrentChannel, Address = address, Value = value, Qmd = qmd, Kind = kind };
        }

        private static readonly System.Collections.Concurrent.ConcurrentDictionary<ulong, byte> _nx2AcquiredAddresses = new();

        private static volatile ulong[] _nx2AcquiredList = [];

        /// <summary>
        /// Records a plain GPU write (any engine: DMA, inline-to-memory, 3D reports...) in the ring if it
        /// covers a word channels acquire on, with the phase of the GPU thread that wrote it.
        /// </summary>
        internal static void Nx2RecordWrite(ulong va, ReadOnlySpan<byte> data)
        {
            if (_nx2SemaphoreWatch != 0 && _nx2SemaphoreWatch + 16 > va && _nx2SemaphoreWatch < va + (ulong)data.Length)
            {
                int offset = (int)Math.Max(0, (long)_nx2SemaphoreWatch - (long)va);
                ReadOnlySpan<byte> words = data.Slice(offset, Math.Min(16, data.Length - offset));
                string text = Convert.ToHexString(words);

                Nx2WatchWrite(va, (ulong)data.Length, () => $"GPU write [{Nx2Phase}] channel {Nx2CurrentChannel:x} bytes {text} from {string.Join(" < ", Environment.StackTrace.Split('\n')[4..10].Select(l => l.Trim().Split(" in ")[0].Replace("at Ryujinx.Graphics.Gpu.", "")))}");
            }

            ulong[] acquired = _nx2AcquiredList;

            if (acquired.Length == 0)
            {
                return;
            }

            foreach (ulong address in acquired)
            {
                if (address >= va && address + 4 <= va + (ulong)data.Length)
                {
                    uint value = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(data[(int)(address - va)..]);

                    Nx2RecordSemaphore($"write[{Nx2Phase}]", address, value);
                }
            }
        }

        /// <summary>
        /// The recent semaphore operations on <paramref name="address"/> (or all if 0), oldest first.
        /// </summary>
        internal static string Nx2DescribeSemaphores(ulong address, int count)
        {
            System.Collections.Generic.List<string> lines = [];
            int last = _nx2SemaphoreEventIndex;

            for (int i = 0; i < _nx2SemaphoreEvents.Length && lines.Count < count; i++)
            {
                Nx2SemaphoreEvent e = _nx2SemaphoreEvents[(last - i) & (_nx2SemaphoreEvents.Length - 1)];

                if (e.Kind != null && (address == 0 || e.Address == address))
                {
                    lines.Add($"{e.Channel:x} {e.Kind} 0x{e.Address:X}={e.Value:x}{(e.Qmd != 0 ? $" qmd 0x{e.Qmd:X}" : "")}");
                }
            }

            lines.Reverse();

            return string.Join("; ", lines);
        }

        internal static void Nx2WatchSemaphore(ulong address, string operation)
        {
            if (_nx2SemaphoreWatch != 0 && (address & ~0xfUL) == _nx2SemaphoreWatch)
            {
                Ryujinx.Common.Logging.Logger.Info?.Print(Ryujinx.Common.Logging.LogClass.Gpu, $"Semaphore watch 0x{address:X}: channel {Nx2CurrentChannel:x} [{Nx2Phase}] {operation}");
            }
        }

        /// <summary>
        /// Logs a Switch 2 write of [address, address + size) that covers the watched address.
        /// </summary>
        internal static void Nx2WatchWrite(ulong address, ulong size, Func<string> writer)
        {
            if (_nx2SemaphoreWatch != 0 && _nx2SemaphoreWatch + 16 > address && _nx2SemaphoreWatch < address + size)
            {
                Ryujinx.Common.Logging.Logger.Info?.Print(Ryujinx.Common.Logging.LogClass.Gpu, $"Semaphore watch 0x{_nx2SemaphoreWatch:X}: written by {writer()} [0x{address:X}, +0x{size:x}]");
            }
        }

        /// <summary>
        /// What the GPU thread is doing on a Switch 2 channel, for diagnostics of stalls.
        /// </summary>
        public static volatile string Nx2Phase = "idle";

        /// <summary>
        /// The last Switch 2 graphics program bound, for draw traces.
        /// </summary>
        internal static string Nx2LastProgram;

        /// <summary>
        /// The program of the last draw (Nx2PassDumper names render targets after it).
        /// </summary>
        internal static string Nx2LastDrawProgram;

        /// <summary>
        /// RYUJINX_PRESENT_TRACE=1: logs Switch 2 render targets, draws into presented images and the
        /// content of presented images (sampled), to find out why a frame shows nothing.
        /// </summary>
        internal static ulong Nx2PresentedAddress;

        /// <summary>
        /// Render targets drawn since the last content report (RYUJINX_PRESENT_TRACE).
        /// </summary>
        internal static readonly System.Collections.Generic.Dictionary<ulong, (WeakReference<Image.Texture> Texture, int Draws)> Nx2RecentTargets = [];

        private static readonly bool _nx2StorageTrace = Environment.GetEnvironmentVariable("RYUJINX_NX2_SB_TRACE") == "1";
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<(int, int, int, ulong), byte> _nx2StorageTraced = new();

        /// <summary>
        /// RYUJINX_NX2_SB_TRACE=1: logs each distinct storage buffer binding (stage, descriptor location,
        /// address and size as read, mapped size), to check what Switch 2 constant buffers hold there.
        /// </summary>
        internal static void Nx2TraceStorageBuffer(string stage, int cbSlot, int cbOffset, Memory.MemoryManager memoryManager, ulong descriptorAddress, ulong address, int size, uint boundSize)
        {
            if (!_nx2StorageTrace || _nx2StorageTraced.Count > 20000 || !_nx2StorageTraced.TryAdd((stage.GetHashCode(), cbSlot, cbOffset, address), 0))
            {
                return;
            }

            Ryujinx.Common.Logging.Logger.Info?.Print(Ryujinx.Common.Logging.LogClass.Gpu,
                $"Switch 2 storage buffer {stage} c[{cbSlot}][0x{cbOffset * 4:x}] (descriptor at 0x{descriptorAddress:X}): address 0x{address:X} size field 0x{size:x}, bound 0x{boundSize:x}, mapped {(address != 0 && memoryManager.IsMapped(address) ? $"0x{memoryManager.GetMappedSize(address, 0x10000000):x}" : "no")}");
        }

        /// <summary>
        /// True when Switch 2 3D methods run on the Maxwell 3D engine (RYUJINX_NX2_MAXWELL=1), so presented
        /// images hold what was drawn.
        /// </summary>
        public static bool Nx2RendersNative => Engine.GPFifo.GPFifoProcessor.Nx2Maxwell;

        public static readonly bool Nx2PresentTrace = Environment.GetEnvironmentVariable("RYUJINX_PRESENT_TRACE") == "1";

        /// <summary>
        /// Hash of the Switch 2 channel whose commands the GPU thread runs, for diagnostics.
        /// </summary>
        internal static int Nx2CurrentChannel;

        /// <summary>
        /// Writes a release's mirror into the first page of its semaphore region, or into the mirror
        /// table when the word there also gets direct releases (<see cref="Engine.Nx2.Nx2MirrorShadow"/>).
        /// </summary>
        internal void Nx2WriteMirror(MemoryManager memoryManager, ulong address, uint value)
        {
            if (!Nx2TryGetSemaphoreMirror(address, out ulong mirror))
            {
                return;
            }

            if (Engine.Nx2.Nx2MirrorShadow.Diverts(mirror))
            {
                Engine.Nx2.Nx2MirrorShadow.Set(mirror, value, memoryManager.Read<uint>(mirror, tracked: true));
            }
            else
            {
                memoryManager.Write(mirror, value);
            }
        }

        private bool Nx2InSemaphoreFirstPage(ulong address)
        {
            lock (_nx2SemaphoreRegions)
            {
                return _nx2SemaphoreRegions.Count != 0 && _nx2SemaphoreRegions.Contains(address & ~0xfffUL);
            }
        }

        /// <summary>
        /// A plain (not mirrored) release landed at <paramref name="address"/>.
        /// </summary>
        internal void Nx2NoteDirectRelease(ulong address)
        {
            if (Nx2InSemaphoreFirstPage(address))
            {
                Engine.Nx2.Nx2MirrorShadow.NoteDirectRelease(address);
            }
        }

        /// <summary>
        /// A release combining with the word it lands on (max or reduction) took it from
        /// <paramref name="before"/> to <paramref name="after"/>.
        /// </summary>
        internal void Nx2NoteCombinedRelease(ulong address, uint before, uint after, Func<uint, uint> combine)
        {
            if (Nx2InSemaphoreFirstPage(address))
            {
                Engine.Nx2.Nx2MirrorShadow.NoteCombinedRelease(address, before, after, combine);
            }
        }

        /// <summary>
        /// The value of a semaphore word as GPU acquires see it (mirrored releases included).
        /// </summary>
        internal static uint Nx2ReadSemaphore(MemoryManager memoryManager, ulong address)
        {
            uint guest = memoryManager.Read<uint>(address, tracked: true);

            return Engine.Nx2.Nx2MirrorShadow.Read(address, guest) ?? guest;
        }

        /// <summary>
        /// Size of a Switch 2 storage buffer binding. A native pointer is bare: bound up to 8 MiB of its
        /// mapping. A read-only compute binding grows to a plausible size word after the pointer: Mario Kart
        /// World's lighting passes read past 8 MiB (capped, those reads came back empty and lit surfaces
        /// stayed dark). Port of Lichii's StorageBufferBinding (#1231, #1333).
        /// </summary>
        internal static uint Nx2StorageBufferSize(Memory.MemoryManager memoryManager, in Engine.Types.SbDescriptor descriptor, bool compute, bool written)
        {
            const ulong BareLimit = 8UL << 20;
            const ulong SizeWordLimit = (1UL << 30) - (64UL << 10);

            ulong address = descriptor.PackAddress();
            ulong mapped = memoryManager.GetMappedSize(address, SizeWordLimit);
            ulong bare = Math.Min(mapped, BareLimit);

            uint sizeWord = (uint)descriptor.Size;
            bool isSizeWord = descriptor.Padding == 0 && sizeWord != 0 && sizeWord <= mapped;

            if (compute && !written && isSizeWord && sizeWord > bare)
            {
                return (uint)Math.Min(sizeWord, SizeWordLimit);
            }

            return (uint)bare;
        }

        internal bool Nx2TryGetSemaphoreMirror(ulong address, out ulong mirror)
        {
            mirror = address - 0x1000;

            lock (_nx2SemaphoreRegions)
            {
                return _nx2SemaphoreRegions.Count != 0 && _nx2SemaphoreRegions.Contains(mirror & ~0xfffUL);
            }
        }

        /// <summary>
        /// Presentation window.
        /// </summary>
        public Window Window { get; }

        /// <summary>
        /// Internal sequence number, used to avoid needless resource data updates
        /// in the middle of a command buffer before synchronizations.
        /// </summary>
        internal int SequenceNumber { get; private set; }

        /// <summary>
        /// Internal sync number, used to denote points at which host synchronization can be requested.
        /// </summary>
        internal ulong SyncNumber { get; private set; }

        /// <summary>
        /// Actions to be performed when a CPU waiting syncpoint or barrier is triggered.
        /// If there are more than 0 items when this happens, a host sync object will be generated for the given <see cref="SyncNumber"/>,
        /// and the SyncNumber will be incremented.
        /// </summary>
        internal List<ISyncActionHandler> SyncActions { get; }

        /// <summary>
        /// Actions to be performed when a CPU waiting syncpoint is triggered.
        /// If there are more than 0 items when this happens, a host sync object will be generated for the given <see cref="SyncNumber"/>,
        /// and the SyncNumber will be incremented.
        /// </summary>
        internal List<ISyncActionHandler> SyncpointActions { get; }

        /// <summary>
        /// Buffer migrations that are currently in-flight. These are checked whenever sync is created to determine if buffer migration
        /// copies have completed on the GPU, and their data can be freed.
        /// </summary>
        internal List<BufferMigration> BufferMigrations { get; }

        /// <summary>
        /// Queue with deferred actions that must run on the render thread.
        /// </summary>
        internal Queue<Action> DeferredActions { get; }

        /// <summary>
        /// Registry with physical memories that can be used with this GPU context, keyed by owner process ID.
        /// </summary>
        internal ConcurrentDictionary<ulong, PhysicalMemory> PhysicalMemoryRegistry { get; }

        /// <summary>
        /// Support buffer updater.
        /// </summary>
        internal SupportBufferUpdater SupportBufferUpdater { get; }

        /// <summary>
        /// Enabled dirty hacks.
        /// Used for workarounds to emulator bugs we can't fix/don't know how to fix yet.
        /// </summary>
        internal DirtyHacks DirtyHacks { get; }


        /// <summary>
        /// Host hardware capabilities.
        /// </summary>
        internal Capabilities Capabilities;

        /// <summary>
        /// Event for signalling shader cache loading progress.
        /// </summary>
        public event Action<ShaderCacheState, int, int> ShaderCacheStateChanged;

        private Thread _gpuThread;
        private bool _pendingSync;

        private long _modifiedSequence;
        private readonly ulong _firstTimestamp;

        private readonly ManualResetEvent _gpuReadyEvent;

        /// <summary>
        /// Creates a new instance of the GPU emulation context.
        /// </summary>
        /// <param name="renderer">Host renderer</param>
        public GpuContext(IRenderer renderer, DirtyHacks hacks)
        {
            Renderer = renderer;

            GPFifo = new GPFifoDevice(this);

            Synchronization = new SynchronizationManager();

            Window = new Window(this);

            Nx2VirtualTextures = new Image.Nx2VirtualTextures(this);

            HostInitalized = new ManualResetEvent(false);
            _gpuReadyEvent = new ManualResetEvent(false);

            SyncActions = [];
            SyncpointActions = [];
            BufferMigrations = [];

            DeferredActions = new Queue<Action>();

            PhysicalMemoryRegistry = new ConcurrentDictionary<ulong, PhysicalMemory>();

            SupportBufferUpdater = new SupportBufferUpdater(renderer);

            DirtyHacks = hacks;

            _firstTimestamp = ConvertNanosecondsToTicks((ulong)PerformanceCounter.ElapsedNanoseconds);
        }

        /// <summary>
        /// Creates a new GPU channel.
        /// </summary>
        /// <returns>The GPU channel</returns>
        public GpuChannel CreateChannel()
        {
            return new GpuChannel(this);
        }

        /// <summary>
        /// Creates a new GPU memory manager.
        /// </summary>
        /// <param name="pid">ID of the process that owns the memory manager</param>
        /// <param name="cpuMemorySize">The amount of physical CPU Memory Avaiable on the device.</param>
        /// <returns>The memory manager</returns>
        /// <exception cref="ArgumentException">Thrown when <paramref name="pid"/> is invalid</exception>
        public MemoryManager CreateMemoryManager(ulong pid, ulong cpuMemorySize)
        {
            if (!PhysicalMemoryRegistry.TryGetValue(pid, out PhysicalMemory physicalMemory))
            {
                throw new ArgumentException("The PID is invalid or the process was not registered", nameof(pid));
            }

            return new MemoryManager(physicalMemory, cpuMemorySize);
        }

        /// <summary>
        /// Creates a new device memory manager.
        /// </summary>
        /// <param name="pid">ID of the process that owns the memory manager</param>
        /// <returns>The memory manager</returns>
        /// <exception cref="ArgumentException">Thrown when <paramref name="pid"/> is invalid</exception>
        public DeviceMemoryManager CreateDeviceMemoryManager(ulong pid)
        {
            if (!PhysicalMemoryRegistry.TryGetValue(pid, out PhysicalMemory physicalMemory))
            {
                throw new ArgumentException("The PID is invalid or the process was not registered", nameof(pid));
            }

            return physicalMemory.CreateDeviceMemoryManager();
        }

        /// <summary>
        /// Registers virtual memory used by a process for GPU memory access, caching and read/write tracking.
        /// </summary>
        /// <param name="pid">ID of the process that owns <paramref name="cpuMemory"/></param>
        /// <param name="cpuMemory">Virtual memory owned by the process</param>
        /// <exception cref="ArgumentException">Thrown if <paramref name="pid"/> was already registered</exception>
        public void RegisterProcess(ulong pid, Cpu.IVirtualMemoryManagerTracked cpuMemory)
        {
            PhysicalMemory physicalMemory = new(this, cpuMemory);
            if (!PhysicalMemoryRegistry.TryAdd(pid, physicalMemory))
            {
                throw new ArgumentException("The PID was already registered", nameof(pid));
            }

            physicalMemory.ShaderCache.ShaderCacheStateChanged += ShaderCacheStateUpdate;
        }

        /// <summary>
        /// Unregisters a process, indicating that its memory will no longer be used, and that caches can be freed.
        /// </summary>
        /// <param name="pid">ID of the process</param>
        public void UnregisterProcess(ulong pid)
        {
            if (PhysicalMemoryRegistry.TryRemove(pid, out PhysicalMemory physicalMemory))
            {
                physicalMemory.ShaderCache.ShaderCacheStateChanged -= ShaderCacheStateUpdate;
                physicalMemory.Dispose();
            }
        }

        /// <summary>
        /// Converts a nanoseconds timestamp value to Maxwell time ticks.
        /// </summary>
        /// <remarks>
        /// The frequency is 614400000 Hz.
        /// </remarks>
        /// <param name="nanoseconds">Timestamp in nanoseconds</param>
        /// <returns>Maxwell ticks</returns>
        private static ulong ConvertNanosecondsToTicks(ulong nanoseconds)
        {
            // We need to divide first to avoid overflows.
            // We fix up the result later by calculating the difference and adding
            // that to the result.
            ulong divided = nanoseconds / NsToTicksFractionDenominator;

            ulong rounded = divided * NsToTicksFractionDenominator;

            ulong errorBias = (nanoseconds - rounded) * NsToTicksFractionNumerator / NsToTicksFractionDenominator;

            return divided * NsToTicksFractionNumerator + errorBias;
        }

        /// <summary>
        /// Gets a sequence number for resource modification ordering. This increments on each call.
        /// </summary>
        /// <returns>A sequence number for resource modification ordering</returns>
        internal long GetModifiedSequence()
        {
            return _modifiedSequence++;
        }

        /// <summary>
        /// Gets the value of the GPU timer.
        /// </summary>
        /// <returns>The current GPU timestamp</returns>
        internal ulong GetTimestamp()
        {
            // Guest timestamp will start at 0, instead of host value.
            ulong ticks = ConvertNanosecondsToTicks((ulong)PerformanceCounter.ElapsedNanoseconds) - _firstTimestamp;

            if (GraphicsConfig.FastGpuTime)
            {
                // Divide by some amount to report time as if operations were performed faster than they really are.
                // This can prevent some games from switching to a lower resolution because rendering is too slow.
                ticks /= 256;
            }

            return ticks;
        }

        /// <summary>
        /// Shader cache state update handler.
        /// </summary>
        /// <param name="state">Current state of the shader cache load process</param>
        /// <param name="current">Number of the current shader being processed</param>
        /// <param name="total">Total number of shaders to process</param>
        private void ShaderCacheStateUpdate(ShaderCacheState state, int current, int total)
        {
            ShaderCacheStateChanged?.Invoke(state, current, total);
        }

        /// <summary>
        /// Initialize the GPU shader cache.
        /// </summary>
        public void InitializeShaderCache(CancellationToken cancellationToken)
        {
            HostInitalized.WaitOne();

            foreach (PhysicalMemory physicalMemory in PhysicalMemoryRegistry.Values)
            {
                physicalMemory.ShaderCache.Initialize(cancellationToken);
            }

            _gpuReadyEvent.Set();
        }

        /// <summary>
        /// Waits until the GPU is ready to receive commands.
        /// </summary>
        public void WaitUntilGpuReady()
        {
            _gpuReadyEvent.WaitOne();
        }

        /// <summary>
        /// Sets the current thread as the main GPU thread.
        /// </summary>
        public void SetGpuThread()
        {
            _gpuThread = Thread.CurrentThread;

            Capabilities = Renderer.GetCapabilities();
        }

        /// <summary>
        /// Checks if the current thread is the GPU thread.
        /// </summary>
        /// <returns>True if the thread is the GPU thread, false otherwise</returns>
        public bool IsGpuThread()
        {
            return _gpuThread == Thread.CurrentThread;
        }

        /// <summary>
        /// Processes the queue of shaders that must save their binaries to the disk cache.
        /// </summary>
        public void ProcessShaderCacheQueue()
        {
            foreach (PhysicalMemory physicalMemory in PhysicalMemoryRegistry.Values)
            {
                physicalMemory.ShaderCache.ProcessShaderCacheQueue();
            }
        }

        /// <summary>
        /// Advances internal sequence number.
        /// This forces the update of any modified GPU resource.
        /// </summary>
        internal void AdvanceSequence()
        {
            SequenceNumber++;
        }

        /// <summary>
        /// Registers a buffer migration. These are checked to see if they can be disposed when the sync number increases,
        /// and the migration copy has completed.
        /// </summary>
        /// <param name="migration">The buffer migration</param>
        internal void RegisterBufferMigration(BufferMigration migration)
        {
            BufferMigrations.Add(migration);
            _pendingSync = true;
        }

        /// <summary>
        /// Registers an action to be performed the next time a syncpoint is incremented.
        /// This will also ensure a host sync object is created, and <see cref="SyncNumber"/> is incremented.
        /// </summary>
        /// <param name="action">The resource with action to be performed on sync object creation</param>
        /// <param name="syncpointOnly">True if the sync action should only run when syncpoints are incremented</param>
        internal void RegisterSyncAction(ISyncActionHandler action, bool syncpointOnly = false)
        {
            if (syncpointOnly)
            {
                SyncpointActions.Add(action);
            }
            else
            {
                SyncActions.Add(action);
                _pendingSync = true;
            }
        }

        /// <summary>
        /// Creates a host sync object if there are any pending sync actions. The actions will then be called.
        /// If no actions are present, a host sync object is not created.
        /// </summary>
        /// <param name="flags">Modifiers for how host sync should be created</param>
        /// <summary>
        /// RYUJINX_NX2_FLUSH_TRACE=1: counts host syncs (which arm the read traps of GPU-written buffers) and
        /// the flushes of GPU-written buffer data the guest CPU's reads trigger.
        /// </summary>
        internal static readonly bool Nx2FlushTrace = Environment.GetEnvironmentVariable("RYUJINX_NX2_FLUSH_TRACE") == "1";
        private static int _nx2SyncsCreated;
        internal static int Nx2ExternalFlushes;

        internal static void Nx2CountFlushEvent(ref int counter, string what)
        {
            int count = Interlocked.Increment(ref counter);

            if (count <= 20 || (count & (count - 1)) == 0)
            {
                Ryujinx.Common.Logging.Logger.Info?.Print(Ryujinx.Common.Logging.LogClass.Gpu, $"Switch 2 flush trace #{count}: {what}");
            }
        }

        private static readonly bool _nx2ReleaseSync = Environment.GetEnvironmentVariable("RYUJINX_NX2_RELEASE_SYNC") == "1";

        /// <summary>
        /// Switch 2 fences are semaphore releases, not syncpoint increments: creates the host sync a syncpoint
        /// increment does, before the release becomes visible, so the guest CPU's reads of buffers the GPU
        /// wrote before it trap and flush (the read traps are armed when a sync is created). Without it the
        /// game may read compute results back as stale memory. RYUJINX_NX2_RELEASE_SYNC=1 turns it on: off by
        /// default, as Mario Kart World then crashed (null pointer in its main loop, Drive.nss 0x221518 ->
        /// 0x2248f4) 25-60 s into the title screen.
        /// </summary>
        internal void Nx2SyncBeforeRelease(Image.TextureManager textureManager)
        {
            if (_nx2ReleaseSync)
            {
                // As a syncpoint increment: render targets drawn so far become flushable textures.
                textureManager.RefreshModifiedTextures();
                CreateHostSyncIfNeeded(HostSyncFlags.StrictSyncpoint);
            }
        }

        internal void CreateHostSyncIfNeeded(HostSyncFlags flags)
        {
            bool syncPoint = (flags & HostSyncFlags.Syncpoint) == HostSyncFlags.Syncpoint;
            bool strict = (flags & HostSyncFlags.Strict) == HostSyncFlags.Strict;
            bool force = (flags & HostSyncFlags.Force) == HostSyncFlags.Force;

            if (BufferMigrations.Count > 0)
            {
                ulong currentSyncNumber = Renderer.GetCurrentSync();

                for (int i = 0; i < BufferMigrations.Count; i++)
                {
                    BufferMigration migration = BufferMigrations[i];
                    long diff = (long)(currentSyncNumber - migration.SyncNumber);

                    if (diff >= 0)
                    {
                        migration.Dispose();
                        BufferMigrations.RemoveAt(i--);
                    }
                }
            }

            if (force || _pendingSync || (syncPoint && SyncpointActions.Count > 0))
            {
                for (int i = 0; i < SyncActions.Count; i++)
                {
                    if (SyncActions[i].SyncPreAction(syncPoint))
                    {
                        SyncActions.RemoveAt(i--);
                    }
                }

                foreach (ISyncActionHandler action in SyncpointActions)
                {
                    action.SyncPreAction(syncPoint);
                }

                Renderer.CreateSync(SyncNumber, strict);

                SyncNumber++;

                if (Nx2FlushTrace)
                {
                    Nx2CountFlushEvent(ref _nx2SyncsCreated, $"host sync {SyncNumber} ({flags}, {SyncActions.Count} actions)");
                }

                for (int i = 0; i < SyncActions.Count; i++)
                {
                    if (SyncActions[i].SyncAction(syncPoint))
                    {
                        SyncActions.RemoveAt(i--);
                    }
                }
                
                for (int i = 0; i < SyncpointActions.Count; i++)
                {
                    if (SyncpointActions[i].SyncAction(syncPoint))
                    {
                        SyncpointActions.RemoveAt(i--);
                    }
                }
            }

            _pendingSync = false;
        }

        /// <summary>
        /// Performs deferred actions.
        /// This is useful for actions that must run on the render thread, such as resource disposal.
        /// </summary>
        internal void RunDeferredActions()
        {
            while (DeferredActions.TryDequeue(out Action action))
            {
                action();
            }
        }

        /// <summary>
        /// Disposes all GPU resources currently cached.
        /// It's an error to push any GPU commands after disposal.
        /// Additionally, the GPU commands FIFO must be empty for disposal,
        /// and processing of all commands must have finished.
        /// </summary>
        public void Dispose()
        {
            GPFifo.Dispose();
            HostInitalized.Dispose();
            _gpuReadyEvent.Dispose();

            // Has to be disposed before processing deferred actions, as it will produce some.
            foreach (PhysicalMemory physicalMemory in PhysicalMemoryRegistry.Values)
            {
                physicalMemory.Dispose();
            }

            SupportBufferUpdater.Dispose();

            PhysicalMemoryRegistry.Clear();

            RunDeferredActions();

            Renderer.Dispose();
        }
    }
}
