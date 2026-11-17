using Ryujinx.Common.Logging;
using Ryujinx.HLE.HOS.Kernel.Threading;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;

namespace Ryujinx.HLE.HOS.Services.Nx2
{
    // The Switch 2 display session (nn::vi2::sdk), handed out by gsv 305.
    class INx2Vi2Session : IpcService, INx2StubService, IDisposable
    {
        private const ulong AvailableSlots = 0x7fff;
        private const ulong DefaultDisplay = 0x00746c7561666564UL; // "default"
        private const int VsyncRateHz = 60;

        private static readonly HashSet<int> _zeroedCommands =
        [
            13, 51, 101, 102, 107, 108, 111, 112, 113, 201, 202, 230, 231, 232, 251, 252, 301, 302,
            351, 352, 353, 354, 355, 356, 358, 359,
        ];

        private readonly Nx2NativeObjects _objects;
        private readonly object _vsyncLock = new();
        private readonly KEvent[] _nativeEvents = new KEvent[8];
        private readonly List<KEvent> _vsyncEvents = [];
        private Thread _vsyncThread;
        private volatile bool _disposed;
        private ulong _presents;

        public string InterfaceName => "nx2:vi2";

        public bool IsZeroedCommand(int commandId) => _zeroedCommands.Contains(commandId);

        public INx2Vi2Session(Nx2NativeObjects objects) : base(registerTipc: true)
        {
            _objects = objects;
        }

        [CommandTipc(0)]
        public ResultCode Handshake(ServiceCtx context) => Nx2Tipc.Handshake(context);

        [CommandTipc(1)]
        [CommandTipc(3)] // GetDisplayCount (guessed)
        public ResultCode ReturnOne(ServiceCtx context)
        {
            Nx2Tipc.Reply(context, 1);

            return ResultCode.Success;
        }

        [CommandTipc(5)]
        // Must be < 8; nn::vi::detail::LayerHistory creates 7 timestamp recorders per layer.
        public ResultCode GetTimestampRecorderCount(ServiceCtx context)
        {
            Nx2Tipc.Reply(context, 7);

            return ResultCode.Success;
        }

        [CommandTipc(53)]
        // GetRecordedTimestamps(B buffer, u32 recorder, u64 checkpoint bits, u64 max count):
        // u64 byte count written and a u8 "still pending". Nothing is recorded yet.
        public ResultCode GetRecordedTimestamps(ServiceCtx context)
        {
            Nx2Tipc.Reply(context, 0, 0, 1);

            return ResultCode.Success;
        }

        [CommandTipc(11)]
        public ResultCode ReturnEvent(ServiceCtx context)
        {
            return Nx2Tipc.ReturnEvent(context, new KEvent(context.Device.System.KernelContext));
        }

        [CommandTipc(31)]
        // AcquireNativeEvent(u32 index < 8): the session's event for that slot. The 261..272 commands bind a
        // slot to a display event source. Until a slot is bound it fires on every vblank.
        public ResultCode AcquireNativeEvent(ServiceCtx context) => ReplyNativeEvent(context, Nx2Tipc.Words(context)[0]);

        [CommandTipc(32)]
        // DuplicateNativeEvent(u32 index, copy handle): the client hands back its handle for an acquired slot
        // and gets the slot's event again. Mario Kart World's main thread waits on this event until it has
        // fired twice before it submits any GPU work.
        public ResultCode DuplicateNativeEvent(ServiceCtx context)
        {
            if (context.Request.HandleDesc != null)
            {
                foreach (int handle in context.Request.HandleDesc.ToCopy)
                {
                    context.Device.System.KernelContext.Syscall.CloseHandle(handle);
                }
            }

            return ReplyNativeEvent(context, Nx2Tipc.Words(context)[0]);
        }

        [CommandTipc(261)]
        // BindDisplayVsyncEvent(u64 display name, u32 index): fire the slot on every vblank.
        public ResultCode BindVsync(ServiceCtx context)
        {
            uint index = Nx2Tipc.Words(context)[2];

            if (index < _nativeEvents.Length && _nativeEvents[index] != null)
            {
                SetVsyncSlot(index, true);
            }

            return ResultCode.Success;
        }

        [CommandTipc(263)] // BindDisplayRefreshRateEvent
        [CommandTipc(265)] // BindDisplayHdrStatusEvent
        [CommandTipc(267)] // BindDisplayHdrPaperWhiteEvent
        [CommandTipc(269)] // BindDisplayBufferMaxResolutionEvent
        [CommandTipc(271)] // BindDisplayModeCapabilityEvent
        // (u64 display name, u32 index): fire the slot when that display state changes. Fire once so the game
        // reads the current state, then leave the slot quiet.
        public ResultCode BindChange(ServiceCtx context)
        {
            uint index = Nx2Tipc.Words(context)[2];

            if (index < _nativeEvents.Length && _nativeEvents[index] != null)
            {
                SetVsyncSlot(index, false);
                _nativeEvents[index].WritableEvent.Signal();
            }

            return ResultCode.Success;
        }

        [CommandTipc(262)]
        [CommandTipc(264)]
        [CommandTipc(266)]
        [CommandTipc(268)]
        [CommandTipc(270)]
        [CommandTipc(272)]
        // (u64 display name, u32 index): detach the slot from its source.
        public ResultCode UnbindNativeEvent(ServiceCtx context)
        {
            uint index = Nx2Tipc.Words(context)[2];

            if (index < _nativeEvents.Length && _nativeEvents[index] != null)
            {
                SetVsyncSlot(index, false);
            }

            return ResultCode.Success;
        }

        [CommandTipc(103)]
        // QueryDisplayRefreshRate(display name): u32 refresh rate in Hz, then a bool.
        // nn::vi::detail::DefaultDisplay::QueryRefreshRate takes only -1, 60 or 120.
        public ResultCode QueryDisplayRefreshRate(ServiceCtx context)
        {
            Nx2Tipc.Reply(context, VsyncRateHz, 0);

            return ResultCode.Success;
        }

        [CommandTipc(104)]
        // QueryDisplayHdrStatus: u64 output format (1 = SDR, 2 = HDR), u16 peak luminance in nits.
        public ResultCode QueryDisplayHdrStatus(ServiceCtx context)
        {
            Nx2Tipc.Reply(context, 1, 0, 0);

            return ResultCode.Success;
        }

        [CommandTipc(105)]
        // QueryDisplayHdrPaperWhite: u16 reference white in nits.
        public ResultCode QueryDisplayHdrPaperWhite(ServiceCtx context)
        {
            Nx2Tipc.Reply(context, 200);

            return ResultCode.Success;
        }

        [CommandTipc(106)]
        // GetBufferMaxResolution(display name): 1 -> 1280x720, 2 -> 1920x1080, 4 and 8 -> 2560x1440.
        public ResultCode GetBufferMaxResolution(ServiceCtx context)
        {
            ulong name = Nx2Tipc.Pair(Nx2Tipc.Words(context), 0);

            if (name != DefaultDisplay)
            {
                context.ResponseData.Write(0UL);

                return (ResultCode)((7 << 9) | 114); // vi: not found
            }

            context.ResponseData.Write(context.Device.System.State.DockedMode ? 4UL : 2UL);

            return ResultCode.Success;
        }

        [CommandTipc(212)]
        // GetLayerBufferBits(u64 layer): u64 bits of the layer's buffer slots that are ready.
        public ResultCode GetLayerBufferBits(ServiceCtx context)
        {
            Nx2Tipc.Reply64(context, AvailableSlots);

            return ResultCode.Success;
        }

        [CommandTipc(228)]
        public ResultCode CancelClientBuffer(ServiceCtx context)
        {
            Nx2Tipc.Reply64(context, AvailableSlots, 0, 0);

            return ResultCode.Success;
        }

        [CommandTipc(229)]
        // PresentClientBuffer(u64 client token, u32 slot, u32 image, ...fences): u64 mask of buffer slots the
        // client may acquire next, four zero words. Nothing is shown yet; every slot is handed back at once.
        public ResultCode PresentClientBuffer(ServiceCtx context)
        {
            uint[] w = Nx2Tipc.Words(context);

            if (_presents++ % 600 == 0)
            {
                Logger.Stub?.PrintStub(LogClass.ServiceVi, new { presents = _presents, image = w[3] });
            }

            // Wait for this frame's fence (syncpoint ~word 20, value word 22), so the game doesn't write the
            // next frame's data (QMDs, constants, command memory) while the GPU thread still reads this
            // frame's. While a channel waits on a semaphore the CPU releases after this present, wait for the
            // previous frame's fence instead and let the game prepare the next one meanwhile.
            uint fenceId = ~w[20];
            uint fenceValue = w[22];
            Ryujinx.Graphics.Gpu.GpuContext gpu = context.Device.Gpu;

            WaitForFrame(gpu, fenceId, fenceValue);

            _previousFenceId = fenceId;
            _previousFenceValue = fenceValue;

            Nx2Tipc.Reply64(context, AvailableSlots, 0, 0);

            return ResultCode.Success;
        }

        private uint _previousFenceId = uint.MaxValue;
        private uint _previousFenceValue;
        private int _fenceTimeouts;

        // Waits for this frame's fence, or only for the previous frame's while a channel is suspended on a
        // semaphore: the CPU may release it only after this present returns. Checked again while waiting,
        // since a channel can suspend after the wait started.
        private void WaitForFrame(Ryujinx.Graphics.Gpu.GpuContext gpu, uint fenceId, uint fenceValue)
        {
            const uint MaxSyncpoints = Ryujinx.Graphics.Gpu.Synchronization.SynchronizationManager.MaxHardwareSyncpoints;

            if (fenceId >= MaxSyncpoints)
            {
                return;
            }

            long deadline = Stopwatch.GetTimestamp() + Stopwatch.Frequency * 4;

            while (true)
            {
                bool gpuWaits = gpu.GPFifo.Nx2HasSuspendedChannels;
                uint id = gpuWaits ? _previousFenceId : fenceId;
                uint value = gpuWaits ? _previousFenceValue : fenceValue;

                if (id >= MaxSyncpoints || IsReached(gpu, id, value))
                {
                    return;
                }

                if (Stopwatch.GetTimestamp() > deadline)
                {
                    if (_fenceTimeouts++ < 16)
                    {
                        Logger.Warning?.Print(LogClass.ServiceVi, $"nx2:vi2 present: fence ({id}, 0x{value:x}) not reached in 4 s, now 0x{gpu.Synchronization.GetSyncpointValue(id):x}; GPU thread: {Ryujinx.Graphics.Gpu.GpuContext.Nx2Phase}; {gpu.GPFifo.Nx2Describe()}");
                    }

                    return;
                }

                gpu.Synchronization.WaitOnSyncpoint(id, value, TimeSpan.FromMilliseconds(2), quiet: true);
            }
        }

        private static bool IsReached(Ryujinx.Graphics.Gpu.GpuContext gpu, uint id, uint value)
        {
            return (int)(gpu.Synchronization.GetSyncpointValue(id) - value) >= 0;
        }

        private ResultCode ReplyNativeEvent(ServiceCtx context, uint index)
        {
            if (index >= _nativeEvents.Length)
            {
                Logger.Error?.Print(LogClass.ServiceVi, $"nx2:vi2 native event index {index} out of range");

                return ResultCode.NotAllocated;
            }

            if (_nativeEvents[index] == null)
            {
                _nativeEvents[index] = new KEvent(context.Device.System.KernelContext);
                SetVsyncSlot(index, true);
                StartVsyncTimer();
            }

            return Nx2Tipc.ReturnEvent(context, _nativeEvents[index]);
        }

        private void SetVsyncSlot(uint index, bool on)
        {
            KEvent evt = _nativeEvents[index];

            lock (_vsyncLock)
            {
                bool listed = _vsyncEvents.Contains(evt);

                if (on && !listed)
                {
                    _vsyncEvents.Add(evt);
                }
                else if (!on && listed)
                {
                    _vsyncEvents.Remove(evt);
                }
            }
        }

        // The game starts frames on this event, so its rate is the frame rate limit.
        private void StartVsyncTimer()
        {
            if (_vsyncThread != null)
            {
                return;
            }

            _vsyncThread = new Thread(() =>
            {
                long interval = Stopwatch.Frequency / VsyncRateHz;
                long next = Stopwatch.GetTimestamp() + interval;

                while (!_disposed)
                {
                    long remaining = next - Stopwatch.GetTimestamp();

                    if (remaining > 0)
                    {
                        Thread.Sleep(TimeSpan.FromTicks(remaining * TimeSpan.TicksPerSecond / Stopwatch.Frequency));
                    }
                    else if (remaining < -interval * 4)
                    {
                        next = Stopwatch.GetTimestamp();
                    }

                    next += interval;

                    lock (_vsyncLock)
                    {
                        foreach (KEvent evt in _vsyncEvents)
                        {
                            evt.WritableEvent.Signal();
                        }
                    }
                }
            })
            {
                Name = "HLE.Nx2Vsync",
                IsBackground = true,
            };

            _vsyncThread.Start();
        }

        public void Dispose()
        {
            _disposed = true;
        }
    }
}
