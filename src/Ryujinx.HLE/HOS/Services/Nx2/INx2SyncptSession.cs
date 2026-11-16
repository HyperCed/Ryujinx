using Ryujinx.Common.Logging;
using Ryujinx.Graphics.Gpu.Synchronization;
using Ryujinx.HLE.HOS.Kernel.Threading;
using System.Collections.Generic;

namespace Ryujinx.HLE.HOS.Services.Nx2
{
    // The Switch 2 syncpoint server (nn::syncpt), handed out by gsv 202.
    // - 0(u64): handshake, echo the token (newer SDKs).
    // - 1: a u32 the SDK requires to be < 0x400.
    // - 22(u32 slot): an event for that slot. NVN's NvOsEventPool asks for slots and multi-waits on them.
    // - 103(counter): the counter's u64 value.
    // - 201(slot, counter, u64 value, mode): signal the slot's event once the counter reaches value.
    //   The counter is a SyncCounterId, the bitwise NOT of the host1x syncpoint id.
    // - 202(slot): cancel the slot's request.
    class INx2SyncptSession : IpcService, INx2StubService
    {
        private sealed class SlotRequest
        {
            public uint Syncpoint;
            public SyncpointWaiterHandle Handle;
            public bool Pending;
        }

        private readonly object _lock = new();
        private readonly Dictionary<uint, KEvent> _events = [];
        private readonly Dictionary<uint, SlotRequest> _requests = [];

        public string InterfaceName => "nx2:syncpt";

        public bool IsZeroedCommand(int commandId) => commandId == 21;

        public INx2SyncptSession() : base(registerTipc: true) { }

        private KEvent EventFor(ServiceCtx context, uint slot)
        {
            if (!_events.TryGetValue(slot, out KEvent evt))
            {
                evt = new KEvent(context.Device.System.KernelContext);
                _events[slot] = evt;
            }

            return evt;
        }

        [CommandTipc(0)]
        public ResultCode Handshake(ServiceCtx context) => Nx2Tipc.Handshake(context);

        [CommandTipc(1)]
        public ResultCode GetCount(ServiceCtx context)
        {
            Nx2Tipc.Reply(context, 32);

            return ResultCode.Success;
        }

        [CommandTipc(103)]
        public ResultCode QueryCounter(ServiceCtx context)
        {
            uint syncpoint = ~Nx2Tipc.Words(context)[0];
            ulong value = syncpoint < SynchronizationManager.MaxHardwareSyncpoints
                ? context.Device.Gpu.Synchronization.GetSyncpointValue(syncpoint)
                : 0;

            Nx2Tipc.Reply(context, (uint)value, (uint)(value >> 32), 0);

            return ResultCode.Success;
        }

        [CommandTipc(22)]
        public ResultCode GetSlotEvent(ServiceCtx context)
        {
            uint slot = Nx2Tipc.Words(context)[0];

            lock (_lock)
            {
                return Nx2Tipc.ReturnEvent(context, EventFor(context, slot));
            }
        }

        [CommandTipc(201)]
        public ResultCode RequestSlotEvent(ServiceCtx context)
        {
            uint[] w = Nx2Tipc.Words(context);
            uint slot = w[0];
            uint syncpoint = ~w[1];
            ulong value = Nx2Tipc.Pair(w, 2);

            Logger.Debug?.Print(LogClass.ServiceNv, $"nx2:syncpt 201 slot={slot:x} syncpoint={syncpoint} value={value:x} mode={w[4]}");

            SlotRequest request;
            KEvent evt;

            lock (_lock)
            {
                evt = EventFor(context, slot);

                if (syncpoint >= SynchronizationManager.MaxHardwareSyncpoints)
                {
                    Logger.Warning?.Print(LogClass.ServiceNv, $"nx2:syncpt 201 syncpoint {syncpoint} out of range, signalling");
                    evt.WritableEvent.Signal();

                    return ResultCode.Success;
                }

                if (_requests.TryGetValue(slot, out SlotRequest old))
                {
                    Cancel(context, old);
                }

                // A new request starts unsignalled: slots are reused by different client objects.
                evt.WritableEvent.Clear();

                request = new SlotRequest { Syncpoint = syncpoint, Pending = true };
                _requests[slot] = request;
            }

            // The callback runs on the thread that reaches the value, or inline if it is already reached.
            SyncpointWaiterHandle handle = context.Device.Gpu.Synchronization.RegisterCallbackOnSyncpoint(syncpoint, (uint)value, _ =>
            {
                lock (request)
                {
                    if (!request.Pending)
                    {
                        return;
                    }

                    request.Pending = false;
                }

                evt.WritableEvent.Signal();
            });

            lock (request)
            {
                request.Handle = handle;
            }

            return ResultCode.Success;
        }

        [CommandTipc(202)]
        public ResultCode CancelSlotEvent(ServiceCtx context)
        {
            uint slot = Nx2Tipc.Words(context)[0];

            lock (_lock)
            {
                if (_requests.TryGetValue(slot, out SlotRequest request))
                {
                    Cancel(context, request);
                }

                // nn::syncpt::sdk's cancel always waits on the slot's event after this reply, so a cancel
                // signals it like a completion, also when nothing was pending.
                EventFor(context, slot).WritableEvent.Signal();
            }

            return ResultCode.Success;
        }

        private static void Cancel(ServiceCtx context, SlotRequest request)
        {
            SyncpointWaiterHandle handle;

            lock (request)
            {
                if (!request.Pending)
                {
                    return;
                }

                request.Pending = false;
                handle = request.Handle;
            }

            if (handle != null)
            {
                context.Device.Gpu.Synchronization.UnregisterCallback(request.Syncpoint, handle);
            }
        }
    }
}
