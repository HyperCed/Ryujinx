using Ryujinx.Common.Logging;
using Ryujinx.HLE.HOS.Kernel.Threading;
using Ryujinx.HLE.HOS.Services.Nv.NvDrvServices;
using System.Collections.Generic;

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
        // CreateSparseRegion: GPU-only memory [start, last] (words 3-6), job slot in word 11.
        public ResultCode CreateSparseRegion(ServiceCtx context)
        {
            uint[] w = Nx2Tipc.Words(context);

            Logger.Info?.Print(LogClass.ServiceNv, $"(STUBBED) nx2:gpu sparse region [{Nx2Tipc.Pair(w, 1):x}, {Nx2Tipc.Pair(w, 3):x}]");
            EventFor(context, w[9]).WritableEvent.Signal();

            return ResultCode.Success;
        }

        [CommandTipc(215)]
        // DestroySparseRegion: words 3-6 the range, job slot in word 8.
        public ResultCode DestroySparseRegion(ServiceCtx context)
        {
            uint[] w = Nx2Tipc.Words(context);

            Logger.Debug?.Print(LogClass.ServiceNv, $"(STUBBED) nx2:gpu sparse region [{Nx2Tipc.Pair(w, 1):x}, {Nx2Tipc.Pair(w, 3):x}]");
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
