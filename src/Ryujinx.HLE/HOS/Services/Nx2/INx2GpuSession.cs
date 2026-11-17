using Ryujinx.Common.Logging;
using Ryujinx.HLE.HOS.Kernel;
using Ryujinx.HLE.HOS.Kernel.Memory;
using Ryujinx.HLE.HOS.Kernel.Process;
using Ryujinx.Horizon.Common;
using Ryujinx.HLE.HOS.Kernel.Threading;
using Ryujinx.HLE.HOS.Services.Nv.NvDrvServices;
using System.Collections.Generic;
using System.Linq;

namespace Ryujinx.HLE.HOS.Services.Nx2
{
    // The Switch 2 GPU session (nn::gpu::sdk), handed out by gsv 497.
    // - 11: [first, last] id range (first <= min(last, 64)).
    // - 12(id): event for that id; the SDK attaches it as a SystemEvent.
    // - 21(id): starts an operation; the SDK then waits on the id's event.
    // - 22: a sub-session, served by this same object so its jobs can signal the per-id events.
    class INx2GpuSession : IpcService, INx2StubService
    {
        private const uint NumIds = 32;

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

                if ((flags & SparseRegionFlag) == 0 && last > start && size % KPageTableBase.PageSize == 0 && size <= MaxBackedRegionSize)
                {
                    BackRegion(context, start, size);
                }
                else
                {
                    Logger.Info?.Print(LogClass.ServiceNv, $"nx2:gpu 214 {((flags & SparseRegionFlag) != 0 ? "sparse" : "GPU-only")} region [{start:x}, {last:x}] ({size >> 20} MiB, flags 0x{flags:x}) not backed");
                }
            }

            EventFor(context, w[9]).WritableEvent.Signal();

            return ResultCode.Success;
        }

        private const uint SparseRegionFlag = 0x200;
        private const ulong MaxBackedRegionSize = 512UL << 20;

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
            // allocations to its pool and fails when that runs short. Contiguous, so it maps in one go.
            KernelContext kernel = context.Device.System.KernelContext;
            KMemoryRegionManager pool = kernel.MemoryManager.MemoryRegions[(int)MemoryRegion.Service];

            if (pool.GetFreePages() < pages + ServicePoolReservePages)
            {
                Logger.Warning?.Print(LogClass.ServiceNv, $"nx2:gpu 214 GPU-only region [{start:x}, +{size >> 20} MiB] not backed: {pool.GetFreePages() * KPageTableBase.PageSize >> 20} MiB left in the system pool (use --dram-size MemoryConfiguration16GiB)");

                return;
            }

            ulong physicalAddress = pool.AllocatePagesContiguous(kernel, pages, backwards: true);

            if (physicalAddress == 0)
            {
                Logger.Warning?.Print(LogClass.ServiceNv, $"nx2:gpu 214 GPU-only region [{start:x}, +{size >> 20} MiB] not backed: no contiguous system memory");

                return;
            }

            Result result = pageTable.MapPages(
                pages,
                0x10000,
                physicalAddress,
                true,
                pageTable.TlsIoRegionStart,
                (pageTable.TlsIoRegionEnd - pageTable.TlsIoRegionStart) / KPageTableBase.PageSize,
                MemoryState.Io,
                KMemoryPermission.ReadAndWrite,
                out ulong cpuAddress);

            // The mapping holds its own reference to the pages.
            kernel.MemoryManager.DecrementPagesReferenceCount(physicalAddress, pages);

            if (result != Result.Success)
            {
                Logger.Warning?.Print(LogClass.ServiceNv, $"nx2:gpu 214 GPU-only region [{start:x}, +{size >> 20} MiB] could not be backed: {result} (TLS/IO region [{pageTable.TlsIoRegionStart:x}, {pageTable.TlsIoRegionEnd:x}), alias [{pageTable.AliasRegionStart:x}, {pageTable.AliasRegionEnd:x}), heap [{pageTable.HeapRegionStart:x}, {pageTable.HeapRegionEnd:x}))");

                return;
            }

            _regions[start] = (cpuAddress, size);
            Nx2GpuMemoryRegistry.MapAt(start, cpuAddress, size);

            Logger.Info?.Print(LogClass.ServiceNv, $"nx2:gpu 214 GPU-only region [{start:x}, +{size >> 20} MiB] backed at 0x{cpuAddress:x}");
        }

        private void DestroyRegion(ServiceCtx context, ulong start)
        {
            if (!_regions.Remove(start, out (ulong CpuAddress, ulong Size) region))
            {
                return;
            }

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
        // kind applied to each; job slot in data word 6. Replies the number of requests done.
        public ResultCode ExecuteSparseMapRequests(ServiceCtx context)
        {
            uint[] w = Nx2Tipc.Words(context);
            ulong count = context.Request.SendBuff.Count > 0 ? context.Request.SendBuff[0].Size / 40 : 0;

            Logger.Debug?.Print(LogClass.ServiceNv, $"(STUBBED) nx2:gpu 216 sparse map requests: {count}");
            EventFor(context, w[6]).WritableEvent.Signal();
            Nx2Tipc.Reply64(context, count);

            return ResultCode.Success;
        }
    }
}
