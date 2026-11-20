using Ryujinx.Common.Logging;
using Ryujinx.HLE.HOS.Kernel;
using Ryujinx.HLE.HOS.Kernel.Memory;
using Ryujinx.HLE.HOS.Kernel.Process;
using Ryujinx.Horizon.Common;
using Ryujinx.HLE.HOS.Kernel.Threading;
using Ryujinx.HLE.HOS.Services.Nv.NvDrvServices;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;

namespace Ryujinx.HLE.HOS.Services.Nx2
{
    // The Switch 2 GPU session (nn::gpu::sdk), handed out by gsv 497.
    // - 11: [first, last] id range (first <= min(last, 64)).
    // - 12(id): event for that id; the SDK attaches it as a SystemEvent.
    // - 21(id): starts an operation; the SDK then waits on the id's event.
    // - 22: a sub-session, served by this same object so its jobs can signal the per-id events.
    class INx2GpuSession : IpcService, INx2StubService
    {
        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        private struct SparseMapRequest
        {
            public ulong GpuAddress;
            public ulong Size;
            public byte NoWrite;
            public byte Cacheable;
            public byte Unknown12;
            public byte Kind;
            public byte FallbackKind;
            public byte Unknown15;
            public ushort Padding16;
            public uint MemoryHandle;
            public uint Padding1C;
            public ulong Offset;
        }

        private const uint NumIds = 32;
        private const int SparseMapRequestSize = 40;

        private readonly object _lock = new();
        private readonly KEvent[] _events = new KEvent[NumIds];
        private readonly bool[] _devices = new bool[256];
        private readonly Dictionary<uint, (ulong Address, ulong Size)> _memory = [];
        private uint _nextMemoryHandle = 1;

        public string InterfaceName => "nx2:gpu";

        public bool IsZeroedCommand(int commandId) => commandId is 23 or 24 or 25 or 34 or 51 or (>= 8001 and <= 8004);

        public INx2GpuSession() : base(registerTipc: true) { }

        private KEvent EventFor(ServiceCtx context, uint id)
        {
            id %= NumIds;

            lock (_lock)
            {
                return _events[id] ??= new KEvent(context.Device.System.KernelContext);
            }
        }

        [CommandTipc(0)]
        public ResultCode Handshake(ServiceCtx context) => Nx2Tipc.Handshake(context);

        [CommandTipc(11)]
        public ResultCode GetIdRange(ServiceCtx context)
        {
            Nx2Tipc.Reply(context, 0, NumIds - 1);

            return ResultCode.Success;
        }

        [CommandTipc(12)]
        public ResultCode GetIdEvent(ServiceCtx context)
        {
            return Nx2Tipc.ReturnEvent(context, EventFor(context, Nx2Tipc.Words(context)[0]));
        }

        [CommandTipc(21)]
        public ResultCode Start(ServiceCtx context)
        {
            EventFor(context, Nx2Tipc.Words(context)[0]).WritableEvent.Signal();

            return ResultCode.Success;
        }

        [CommandTipc(22)]
        public ResultCode OpenSubSession(ServiceCtx context) => Nx2Tipc.ReturnSession(context, this, this);

        [CommandTipc(33)]
        public ResultCode GetCount33(ServiceCtx context)
        {
            Nx2Tipc.Reply(context, 1);

            return ResultCode.Success;
        }

        [CommandTipc(41)]
        // CreateGraphicsDevice(u64 name, u64 flags, u64 debug label) -> u32 id; 0 is not a valid id.
        public ResultCode CreateGraphicsDevice(ServiceCtx context)
        {
            uint id = 0;

            lock (_lock)
            {
                for (uint candidate = 1; candidate < _devices.Length; candidate++)
                {
                    if (!_devices[candidate])
                    {
                        _devices[candidate] = true;
                        id = candidate;
                        break;
                    }
                }
            }

            Nx2Tipc.Reply(context, id);

            return id != 0 ? ResultCode.Success : (ResultCode)0xd001; // svc OutOfResource
        }

        [CommandTipc(42)]
        public ResultCode DestroyGraphicsDevice(ServiceCtx context)
        {
            uint id = Nx2Tipc.Words(context)[0] & 0xff;

            lock (_lock)
            {
                bool existed = _devices[id];
                _devices[id] = false;

                return existed ? ResultCode.Success : (ResultCode)0xf201; // svc NotFound
            }
        }

        [CommandTipc(201)]
        // GetUserPartition: two u64s, valid whether read as (start, size) or (min, max).
        public ResultCode GetUserPartition(ServiceCtx context)
        {
            Nx2Tipc.Reply64(context, Nx2Driver.GpuVaStart, Nx2Driver.GpuVaEnd - Nx2Driver.GpuVaStart);

            return ResultCode.Success;
        }

        [CommandTipc(202)]
        // GetGsvPartition: an empty partition is min = ~0, max = 0.
        public ResultCode GetGsvPartition(ServiceCtx context)
        {
            Nx2Tipc.Reply64(context, ulong.MaxValue, 0);

            return ResultCode.Success;
        }

        [CommandTipc(203)]
        // GetVaRange: kind (< 5, 0 = none), then the range NVN's in-process allocator serves.
        public ResultCode GetVaRange(ServiceCtx context)
        {
            Nx2Tipc.Reply64(context, 1, Nx2Driver.GpuVaStart, Nx2Driver.GpuVaEnd - Nx2Driver.GpuVaStart);

            return ResultCode.Success;
        }

        [CommandTipc(101)]
        // CreateGpuAccessibleMemory(u64 start, u64 last, flags...) -> u32 GpuMemoryHandle
        public ResultCode CreateGpuAccessibleMemory(ServiceCtx context)
        {
            uint[] w = Nx2Tipc.Words(context);
            ulong start = Nx2Tipc.Pair(w, 0);
            ulong last = Nx2Tipc.Pair(w, 2);
            uint handle;

            if (last > start)
            {
                Nx2GpuMemoryRegistry.Register(start, last - start + 1);

                // Two-page regions hold queue semaphores: GPU releases go to the second page, waits read
                // the first.
                if (last - start + 1 == 0x2000)
                {
                    context.Device.Gpu.Nx2AddSemaphoreRegion(start);
                }
            }

            lock (_lock)
            {
                handle = _nextMemoryHandle++;
                _memory[handle] = (start, last - start + 1);
            }

            Logger.Debug?.Print(LogClass.ServiceNv, $"nx2:gpu 101 [{start:x}, {last:x}] -> handle {handle}");
            Nx2Tipc.Reply(context, handle);

            return ResultCode.Success;
        }

        [CommandTipc(102)]
        // DestroyGpuAccessibleMemory(u32 handle, u32 job slot)
        public ResultCode DestroyGpuAccessibleMemory(ServiceCtx context)
        {
            uint[] w = Nx2Tipc.Words(context);

            lock (_lock)
            {
                if (_memory.Remove(w[0], out (ulong Address, ulong Size) memory))
                {
                    Nx2GpuMemoryRegistry.Unmap(memory.Address, memory.Size);

                    if (memory.Size == 0x2000)
                    {
                        context.Device.Gpu.Nx2RemoveSemaphoreRegion(memory.Address);
                    }
                }
                else
                {
                    Logger.Warning?.Print(LogClass.ServiceNv, $"nx2:gpu 102 destroy of unknown handle {w[0]}");
                }
            }

            EventFor(context, w[1]).WritableEvent.Signal();

            return ResultCode.Success;
        }

        [CommandTipc(211)]
        // NormalMapGpuMemory4K: words 3-4 first GPU address, 5-6 last, 7 memory handle, 8-9 offset, 17 job slot.
        public ResultCode NormalMapGpuMemory4K(ServiceCtx context)
        {
            uint[] w = Nx2Tipc.Words(context);
            ulong first = Nx2Tipc.Pair(w, 1);
            ulong last = Nx2Tipc.Pair(w, 3);
            uint handle = w[5];
            ulong offset = Nx2Tipc.Pair(w, 6);

            lock (_lock)
            {
                if (_memory.TryGetValue(handle, out (ulong Address, ulong Size) memory) && last > first)
                {
                    Nx2GpuMemoryRegistry.MapAt(first, memory.Address + offset, last - first + 1);
                }
                else
                {
                    Logger.Warning?.Print(LogClass.ServiceNv, $"nx2:gpu 211 map of unknown handle {handle}");
                }
            }

            EventFor(context, w[15]).WritableEvent.Signal();

            return ResultCode.Success;
        }

        [CommandTipc(212)]
        // NormalModifyMemoryKind4K: word 7 the wanted kind, 10 the job slot. Replies the kind applied.
        public ResultCode NormalModifyMemoryKind4K(ServiceCtx context)
        {
            uint[] w = Nx2Tipc.Words(context);

            EventFor(context, w[8]).WritableEvent.Signal();
            Nx2Tipc.Reply(context, w[5] & 0xff);

            return ResultCode.Success;
        }

        [CommandTipc(213)]
        // NormalUnmapGpuMemory4K: words 3-4 first GPU address, 5-6 last, 8 job slot.
        public ResultCode NormalUnmapGpuMemory4K(ServiceCtx context)
        {
            uint[] w = Nx2Tipc.Words(context);
            ulong first = Nx2Tipc.Pair(w, 1);
            ulong last = Nx2Tipc.Pair(w, 3);

            if (last > first)
            {
                Nx2GpuMemoryRegistry.Unmap(first, last - first + 1);
            }

            EventFor(context, w[6]).WritableEvent.Signal();

            return ResultCode.Success;
        }

        [CommandTipc(214)]
        // CreateSparseRegion: GPU-only memory [start, last] (words 1-4), flags in word 5 (0x200: sparse, the
        // game maps its own memory into it with 216), job slot in word 9. The graphics server backs GPU-only
        // regions itself: the game never maps anything there but renders into them (Mario Kart World keeps
        // its render targets in 250 MiB regions at 0x81_0000_0000). They get memory in the application's
        // TLS/IO region, mapped for the GPU at their address, up to MaxBackedRegionSize each.
        public ResultCode CreateSparseRegion(ServiceCtx context)
        {
            uint[] w = Nx2Tipc.Words(context);
            ulong start = Nx2Tipc.Pair(w, 1);
            ulong last = Nx2Tipc.Pair(w, 3);
            uint flags = w[5];
            ulong size = last + 1 - start;

            lock (_regions)
            {
                // A region created again from the same start replaces the earlier one (Mario Kart World grows
                // some this way without 215).
                DestroyRegion(context, start);

                bool sparse = (flags & SparseRegionFlag) != 0;

                if ((!sparse || _backSparseRegions) && last > start && size % KPageTableBase.PageSize == 0 && size <= MaxBackedRegionSize)
                {
                    BackRegion(context, start, size);

                    if (sparse && _regions.ContainsKey(start))
                    {
                        _sparseRegions.Add(start);
                    }
                }
                else
                {
                    Logger.Info?.Print(LogClass.ServiceNv, $"nx2:gpu 214 {((flags & SparseRegionFlag) != 0 ? "sparse" : "GPU-only")} region [{start:x}, {last:x}] ({size >> 20} MiB, flags 0x{flags:x}) not backed");

                    if (sparse && last > start)
                    {
                        context.Device.Gpu.Nx2AddSparseRegion(start, size);
                    }
                }
            }

            EventFor(context, w[9]).WritableEvent.Signal();

            return ResultCode.Success;
        }

        private const uint SparseRegionFlag = 0x200;

        // Sparse regions are backed too (as the system pool allows: MemoryConfiguration32GiB leaves room), so
        // textures there can be created: render targets the game never maps memory under, and virtual
        // textures whose resident tiles the game maps with 216 (those mappings replace the backing). Pages
        // never written cost no host memory. RYUJINX_NX2_BACK_SPARSE=0 leaves them unbacked.
        private static readonly bool _backSparseRegions = System.Environment.GetEnvironmentVariable("RYUJINX_NX2_BACK_SPARSE") != "0";
        // Larger regions hold virtual textures (gigabytes): backed, Ryujinx would create and upload them whole
        // (Lichii binds them as sparse images instead), which does not fit in a 16 GB Mac.
        private const ulong MaxBackedRegionSize = 512UL << 20;
        private const ulong MaxContiguousRegionSize = 512UL << 20;

        // Backed regions created sparse: the game's own mappings (216) apply over their backing.
        private readonly HashSet<ulong> _sparseRegions = [];

        // System memory left to the services when backing GPU-only regions.
        private const ulong ServicePoolReservePages = (256UL << 20) / KPageTableBase.PageSize;

        private readonly Dictionary<ulong, (ulong CpuAddress, ulong Size)> _regions = [];

        private static KProcess ApplicationProcess(ServiceCtx context)
        {
            return context.Device.Processes.ActiveApplication is { } application &&
                   context.Device.System.KernelContext.Processes.TryGetValue(application.ProcessId, out KProcess process)
                ? process
                : null;
        }

        private void BackRegion(ServiceCtx context, ulong start, ulong size)
        {
            KProcess process = ApplicationProcess(context);

            if (process == null)
            {
                return;
            }

            KPageTableBase pageTable = process.MemoryManager;
            ulong pages = size / KPageTableBase.PageSize;

            // The graphics server's own memory (the system pool), not the application's: the title sizes its
            // allocations to its pool and fails when that runs short.
            KernelContext kernel = context.Device.System.KernelContext;
            KMemoryRegionManager pool = kernel.MemoryManager.MemoryRegions[(int)MemoryRegion.Service];

            if (pool.GetFreePages() < pages + ServicePoolReservePages)
            {
                Logger.Warning?.Print(LogClass.ServiceNv, $"nx2:gpu 214 GPU-only region [{start:x}, +{size >> 20} MiB] not backed: {pool.GetFreePages() * KPageTableBase.PageSize >> 20} MiB left in the system pool (use --dram-size MemoryConfiguration16GiB)");

                return;
            }

            // The page heap's largest block is 1 GiB: larger regions are backed from a page list.
            ulong physicalAddress = size <= MaxContiguousRegionSize ? pool.AllocatePagesContiguous(kernel, pages, backwards: true) : 0;
            bool nonContiguous = physicalAddress == 0;
            ulong cpuAddress;
            Result result;

            if (physicalAddress != 0)
            {
                result = pageTable.MapPages(
                    pages,
                    0x10000,
                    physicalAddress,
                    true,
                    pageTable.TlsIoRegionStart,
                    (pageTable.TlsIoRegionEnd - pageTable.TlsIoRegionStart) / KPageTableBase.PageSize,
                    MemoryState.Io,
                    KMemoryPermission.ReadAndWrite,
                    out cpuAddress);

                // The mapping holds its own reference to the pages.
                kernel.MemoryManager.DecrementPagesReferenceCount(physicalAddress, pages);

                if (result == Result.Success)
                {
                    // The kernel hands out zeroed memory; pages the system pool gave out before (a region the
                    // title destroyed and backs again) would keep their old contents. Pages never used are still zero.
                    kernel.MemoryManager.ForEachDirtyRange(physicalAddress, pages,
                        (pa, count) => process.CpuMemory.Fill(cpuAddress + (pa - physicalAddress), count * KPageTableBase.PageSize, 0));
                }
            }
            else
            {
                // The system pool is too fragmented for one run: the region only needs contiguous virtual memory.
                result = pool.AllocatePages(out KPageList pageList, pages);

                if (result != Result.Success)
                {
                    Logger.Warning?.Print(LogClass.ServiceNv, $"nx2:gpu 214 GPU-only region [{start:x}, +{size >> 20} MiB] not backed: {result}");

                    return;
                }

                result = pageTable.MapPageList(
                    pageList,
                    0x10000,
                    pageTable.TlsIoRegionStart,
                    (pageTable.TlsIoRegionEnd - pageTable.TlsIoRegionStart) / KPageTableBase.PageSize,
                    MemoryState.Io,
                    KMemoryPermission.ReadAndWrite,
                    out cpuAddress);
            }

            if (result != Result.Success)
            {
                Logger.Warning?.Print(LogClass.ServiceNv, $"nx2:gpu 214 GPU-only region [{start:x}, +{size >> 20} MiB] could not be backed: {result} (TLS/IO region [{pageTable.TlsIoRegionStart:x}, {pageTable.TlsIoRegionEnd:x}), alias [{pageTable.AliasRegionStart:x}, {pageTable.AliasRegionEnd:x}), heap [{pageTable.HeapRegionStart:x}, {pageTable.HeapRegionEnd:x}))");

                return;
            }

            _regions[start] = (cpuAddress, size);
            context.Device.Gpu.Nx2AddGpuOnlyRegion(start, size);
            Nx2GpuMemoryRegistry.MapAt(start, cpuAddress, size);

            Logger.Info?.Print(LogClass.ServiceNv, $"nx2:gpu 214 GPU-only region [{start:x}, +{size >> 20} MiB] backed{(nonContiguous ? " from non-contiguous pages" : "")} at 0x{cpuAddress:x}");
        }

        private void DestroyRegion(ServiceCtx context, ulong start)
        {
            context.Device.Gpu.Nx2RemoveSparseRegion(start);

            if (!_regions.Remove(start, out (ulong CpuAddress, ulong Size) region))
            {
                return;
            }

            _sparseRegions.Remove(start);
            context.Device.Gpu.Nx2RemoveGpuOnlyRegion(start);

            Nx2GpuMemoryRegistry.Unmap(start, region.Size);

            KProcess process = ApplicationProcess(context);
            Result result = process?.MemoryManager.UnmapNoAttributeIfStateEquals(region.CpuAddress, region.Size, MemoryState.Io) ?? Result.Success;

            if (result != Result.Success)
            {
                Logger.Warning?.Print(LogClass.ServiceNv, $"nx2:gpu GPU-only region at {start:x}: unmapping its memory failed: {result}");
            }
        }

        [CommandTipc(215)]
        // DestroySparseRegion: words 1-4 the range, job slot in word 6.
        public ResultCode DestroySparseRegion(ServiceCtx context)
        {
            uint[] w = Nx2Tipc.Words(context);

            lock (_regions)
            {
                DestroyRegion(context, Nx2Tipc.Pair(w, 1));
            }

            EventFor(context, w[6]).WritableEvent.Signal();

            return ResultCode.Success;
        }

        [CommandTipc(216)]
        // ExecuteSparseMapRequests: an A buffer of 40-byte SparseMapRequests and a B buffer that gets the
        // kind applied to each; job slot in raw data word 12. Replies the number of requests done.
        public ResultCode ExecuteSparseMapRequests(ServiceCtx context)
        {
            uint[] w = Nx2Tipc.Words(context);
            ReadOnlySpan<SparseMapRequest> requests = ReadOnlySpan<SparseMapRequest>.Empty;

            if (context.Request.SendBuff.Count > 0)
            {
                var input = context.Request.SendBuff[0];
                int requestBytes = (int)(input.Size / SparseMapRequestSize * SparseMapRequestSize);

                if (input.Size % SparseMapRequestSize != 0)
                {
                    Logger.Warning?.Print(LogClass.ServiceNv, $"nx2:gpu 216 invalid sparse map buffer size {input.Size}");
                }

                requests = MemoryMarshal.Cast<byte, SparseMapRequest>(context.Memory.GetSpan(input.Position, requestBytes));
            }

            byte[] kinds = new byte[requests.Length];

            for (int i = 0; i < requests.Length; i++)
            {
                SparseMapRequest request = requests[i];
                kinds[i] = request.Kind;
                ApplySparseMapRequest(request);
            }

            if (context.Request.ReceiveBuff.Count > 0 && kinds.Length > 0)
            {
                var output = context.Request.ReceiveBuff[0];

                if ((ulong)kinds.Length <= output.Size)
                {
                    context.Memory.Write(output.Position, kinds);
                }
                else
                {
                    Logger.Warning?.Print(LogClass.ServiceNv, $"nx2:gpu 216 sparse map output buffer is too small for {requests.Length} requests");
                }
            }

            EventFor(context, w[12]).WritableEvent.Signal();
            Nx2Tipc.Reply64(context, (ulong)requests.Length);

            return ResultCode.Success;
        }

        // RYUJINX_NX2_SPARSE_TRACE=1: logs the sparse map requests (the first 2000).
        private static readonly bool _sparseTrace = System.Environment.GetEnvironmentVariable("RYUJINX_NX2_SPARSE_TRACE") == "1";
        private static int _sparseTraces;

        // RYUJINX_NX2_SPARSE_TRACE_RANGE=<hex start>-<hex end>: only the requests starting in that range.
        private static readonly ulong[] _sparseTraceRange = System.Environment.GetEnvironmentVariable("RYUJINX_NX2_SPARSE_TRACE_RANGE")?.Split('-') is [string from, string to] ? [System.Convert.ToUInt64(from, 16), System.Convert.ToUInt64(to, 16)] : [0, ulong.MaxValue];
        private static readonly ulong _sparseTraceFrom = _sparseTraceRange[0];
        private static readonly ulong _sparseTraceTo = _sparseTraceRange[1];

        // RYUJINX_NX2_SPARSE_OVERRIDE=1: the game's own mappings replace the memory backing GPU-only regions.
        private static readonly bool _sparseOverridesBacking = System.Environment.GetEnvironmentVariable("RYUJINX_NX2_SPARSE_OVERRIDE") == "1";

        private void ApplySparseMapRequest(SparseMapRequest request)
        {
            if (_sparseTrace && (request.GpuAddress >= _sparseTraceFrom && request.GpuAddress < _sparseTraceTo) && System.Threading.Interlocked.Increment(ref _sparseTraces) <= 2000)
            {
                Logger.Info?.Print(LogClass.ServiceNv, $"nx2:gpu 216 map [{request.GpuAddress:x}, +{request.Size:x}] handle {request.MemoryHandle} offset 0x{request.Offset:x} kind 0x{request.Kind:x}");
            }

            if (request.Size == 0)
            {
                return;
            }

            if (request.Size > ulong.MaxValue - request.GpuAddress)
            {
                Logger.Warning?.Print(LogClass.ServiceNv, $"nx2:gpu 216 sparse map range [{request.GpuAddress:x}, +{request.Size:x}] wraps the address space");
                return;
            }

            ulong pageMask = KPageTableBase.PageSize - 1;

            if (((request.GpuAddress | request.Size) & pageMask) != 0)
            {
                Logger.Warning?.Print(LogClass.ServiceNv, $"nx2:gpu 216 unaligned sparse map range [{request.GpuAddress:x}, +{request.Size:x}]");
                return;
            }

            // Backed regions (GPU-only, and sparse ones holding render targets) keep their backing: remapping the
            // game's tiles over it invalidated their textures (a 341 MiB virtual texture each) on every request.
            // RYUJINX_NX2_SPARSE_OVERRIDE=1 applies the game's mappings instead.
            if (_sparseOverridesBacking && request.MemoryHandle == 0 &&
                TryGetBackedSparseRegion(request.GpuAddress, out ulong regionStart, out ulong regionCpuAddress, out ulong regionSize))
            {
                // Unmapped from a backed sparse region: its own backing again.
                ulong end = Math.Min(request.GpuAddress + request.Size, regionStart + regionSize);

                Nx2GpuMemoryRegistry.MapAt(request.GpuAddress, regionCpuAddress + (request.GpuAddress - regionStart), end - request.GpuAddress);

                return;
            }

            if (!_sparseOverridesBacking && OverlapsBackedGpuOnlyRegion(request.GpuAddress, request.Size))
            {
                Logger.Debug?.Print(LogClass.ServiceNv, $"nx2:gpu 216 leaves backed GPU-only range [{request.GpuAddress:x}, +{request.Size:x}] unchanged");
                return;
            }

            if (request.MemoryHandle == 0)
            {
                Nx2GpuMemoryRegistry.Unmap(request.GpuAddress, request.Size);
                return;
            }

            lock (_lock)
            {
                if (!_memory.TryGetValue(request.MemoryHandle, out (ulong Address, ulong Size) memory) ||
                    request.Offset > memory.Size ||
                    request.Size > memory.Size - request.Offset)
                {
                    Logger.Warning?.Print(LogClass.ServiceNv, $"nx2:gpu 216 invalid map [{request.GpuAddress:x}, +{request.Size:x}] memory {request.MemoryHandle} offset {request.Offset:x}");
                    return;
                }

                Nx2GpuMemoryRegistry.MapAt(request.GpuAddress, memory.Address + request.Offset, request.Size);
            }
        }

        private bool TryGetBackedSparseRegion(ulong address, out ulong start, out ulong cpuAddress, out ulong size)
        {
            lock (_regions)
            {
                foreach (ulong regionStart in _sparseRegions)
                {
                    (ulong CpuAddress, ulong Size) region = _regions[regionStart];

                    if (address >= regionStart && address - regionStart < region.Size)
                    {
                        start = regionStart;
                        cpuAddress = region.CpuAddress;
                        size = region.Size;

                        return true;
                    }
                }
            }

            start = cpuAddress = size = 0;

            return false;
        }

        private bool OverlapsBackedGpuOnlyRegion(ulong address, ulong size)
        {
            lock (_regions)
            {
                foreach ((ulong start, (ulong CpuAddress, ulong Size) region) in _regions)
                {
                    if (address < start)
                    {
                        if (start - address < size)
                        {
                            return true;
                        }
                    }
                    else if (address - start < region.Size)
                    {
                        return true;
                    }
                }
            }

            return false;
        }
    }
}
