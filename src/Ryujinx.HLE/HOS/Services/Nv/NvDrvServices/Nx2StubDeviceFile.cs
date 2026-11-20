using Ryujinx.HLE.HOS.Kernel.Threading;
using Ryujinx.Horizon.Common;
using Ryujinx.Memory;
using System;
using System.Collections.Generic;

namespace Ryujinx.HLE.HOS.Services.Nv.NvDrvServices
{
    // Newer nvgpu nodes used by Switch 2 drivers (e.g. /dev/nvhost-tsg-gpu): every ioctl is answered by
    // INvDrvServices' logged, input-echoing success, and every event query gets an event.
    class Nx2StubDeviceFile : NvDeviceFile
    {
        private readonly Dictionary<uint, KEvent> _events = [];

        public Nx2StubDeviceFile(ServiceCtx context, IVirtualMemoryManager memory, ulong owner) : base(context, owner) { }

        public override NvInternalResult QueryEvent(out int eventHandle, uint eventId)
        {
            if (!_events.TryGetValue(eventId, out KEvent evt))
            {
                _events[eventId] = evt = new KEvent(Context.Device.System.KernelContext);
            }

            if (Context.Process.HandleTable.GenerateHandle(evt.ReadableEvent, out eventHandle) != Result.Success)
            {
                throw new InvalidOperationException("Out of handles!");
            }

            return NvInternalResult.Success;
        }

        public override void Close() { }
    }
}
