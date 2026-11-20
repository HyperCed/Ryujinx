using Ryujinx.Common.Logging;
using Ryujinx.Graphics.Gpu.Synchronization;
using Ryujinx.HLE.HOS.Kernel.Memory;
using Ryujinx.HLE.HOS.Kernel.Process;
using Ryujinx.HLE.HOS.Kernel.Threading;
using Ryujinx.HLE.HOS.Services.Nv;
using Ryujinx.Memory;
using System;
using System.Collections.Generic;
using System.Threading;

namespace Ryujinx.HLE.HOS.Services.Nx2
{
    // The Switch 2 graphics system server (nn::gsv), handed out by am ISelfController 2000.
    // It hands out the syncpt, gpu, vi2 and nvsrv sessions and owns native memories and images.
    class INx2GraphicsSystemSession : IpcService, INx2StubService, IDisposable
    {
        private const uint SyncptValuesOffset = 0x40;     // RO, u64 per syncpoint
        private const uint SyncptRoSummaryOffset = 0x1040;
        private const uint SyncptRoBitsOffset = 0x1100;   // RO, up to 0x40 u64 words
        private const uint SyncptRwSummaryOffset = 0x0;
        private const uint SyncptRwBitsOffset = 0x100;    // RW, up to 0x40 u64 words

        private static readonly HashSet<int> _zeroedCommands =
        [
            113, 120, 121, 122, 123, 124, 125, 131, 133, 187, 211, 212, 213, 214, 215, 303, 424, 425, 436,
            461, 462, 463, 464, 465, 466, 467, 468, 493, 603, 613, 614, 802, 803, 813, 814, 1003, 1011, 1012,
            1091, 1112, 1113, 1203, 1602, 1603,
        ];

        private readonly Nx2NativeObjects _objects = new();
        private readonly HashSet<uint> _addressSpaces = [];

        private IVirtualMemoryManager _clientMemory;
        private ulong _owner;

        private KEvent _syncptRequestEvent;
        private KEvent _syncptNotifyEvent;
        private IVirtualMemoryManager _syncptMemory;
        private ulong _syncptRw;
        private ulong _syncptRo;
        private uint _syncptCount;
        private Thread _syncptPoller;
        private volatile bool _disposed;

        public string InterfaceName => "nx2:gsv";

        public bool IsZeroedCommand(int commandId) => _zeroedCommands.Contains(commandId);

        public INx2GraphicsSystemSession() : base(registerTipc: true) { }

        [CommandTipc(4)]    // Boot (guessed)
        [CommandTipc(5)]
        [CommandTipc(101)]
        [CommandTipc(901)]  // VideoInitialize (guessed)
        [CommandTipc(903)]  // VideoFinalize (guessed)
        [CommandTipc(1001)] // VideoInitialize2 (guessed)
        [CommandTipc(601)]  // CameraInitialize (guessed)
        [CommandTipc(1201)] // ChatInitialize (guessed)
        public ResultCode Stub(ServiceCtx context)
        {
            Logger.Stub?.PrintStub(LogClass.ServiceNv, new { commandId = (int)context.Request.Type - 0x10 });

            return ResultCode.Success;
        }

        [CommandTipc(902)]  // OpenVideoDecoderSession (guessed)
        [CommandTipc(905)]  // OpenVideoDecoderSessionWithFlag
        [CommandTipc(906)]  // OpenVideoDecoderSessionWithVersion
        [CommandTipc(1002)] // OpenVideoDecoderSession2 (guessed)
        // The video decoder session (nn::codec::video). Left to the stub no handle came back and the movie
        // player aborted with InvalidHandle (Lichii NX2_NOTES rows 105, 146).
        public ResultCode OpenVideoSession(ServiceCtx context)
        {
            int command = (int)context.Request.Type - 0x10;

            Logger.Info?.Print(LogClass.ServiceNv, $"nx2:gsv {command}: returning a video decoder session");

            return Nx2Tipc.ReturnSession(context, this, new INx2VideoSession(command == 1002 ? "nx2:vdec2" : "nx2:vdec"));
        }

        [CommandTipc(991)]
        // CalculateDecoderImagePoolBuffer(preference[16 words], u32 count): result, u64 size, u64 alignment.
        // The preference holds an output layout code at word 1 (1/4: NV12, what movie players use; 2/5
        // BGRA; others RGBA) and (width - 1) | (height - 1) << 16 at word 2. Left to the stub, the reply
        // kept the request's words: the size read as 0x167027f'00000001 and Mario Kart World's movie
        // player aborted (main+0x19e5b58) when its first movie started, 1-4 min into the title screen
        // (Lichii NX2_NOTES row 96). Pitch-linear rows, NV12's half-height chroma below the luma, each
        // plane padded to 128 rows.
        public ResultCode CalculateDecoderImagePoolBuffer(ServiceCtx context)
        {
            uint[] w = Nx2Tipc.Words(context);
            uint code = w[1];
            ulong width = (w[2] & 0xffff) + 1;
            ulong height = (w[2] >> 16) + 1;
            uint count = System.Math.Max(w[16], 1);
            bool nv12 = code is 1 or 4;
            const ulong BlockRows = 128;

            ulong pitch = Ryujinx.Common.BitUtils.AlignUp(width * (nv12 ? 1UL : 4UL), 64UL);
            ulong chromaOffset = pitch * Ryujinx.Common.BitUtils.AlignUp(height, BlockRows);
            ulong size = (chromaOffset + (nv12 ? pitch * Ryujinx.Common.BitUtils.AlignUp((height + 1) / 2, BlockRows) : 0)) * count;
            const ulong Alignment = 64 * 128;

            Logger.Info?.Print(LogClass.ServiceNv, $"nx2:gsv 991 decoder image pool code={code} {width}x{height} count={count} -> size=0x{size:x}");
            Nx2Tipc.Reply64(context, size, Alignment);

            return ResultCode.Success;
        }

        [CommandTipc(201)]
        [CommandTipc(301)]
        [CommandTipc(401)]
        [CommandTipc(402)]
        [CommandTipc(403)]
        [CommandTipc(494)]
        [CommandTipc(801)]
        [CommandTipc(1101)]
        [CommandTipc(1102)]
        [CommandTipc(1501)]
        [CommandTipc(1502)]
        [CommandTipc(1503)]
        [CommandTipc(1601)]
        // Feature permission checks: each tests one bit of the permissions the session was opened with.
        public ResultCode CheckPermission(ServiceCtx context) => ResultCode.Success;

        [CommandTipc(11)]
        // RegisterClientHandle(handle<copy, process>): handed over once per session.
        public ResultCode RegisterClientHandle(ServiceCtx context)
        {
            if (context.Request.HandleDesc == null || context.Request.HandleDesc.ToCopy.Length < 1)
            {
                return ResultCode.NotAllocated;
            }

            int processHandle = context.Request.HandleDesc.ToCopy[0];
            KProcess process = context.Process.HandleTable.GetKProcess(processHandle);

            context.Device.System.KernelContext.Syscall.CloseHandle(processHandle);

            if (process == null || _clientMemory != null)
            {
                return ResultCode.NotAllocated;
            }

            _clientMemory = process.CpuMemory;
            _owner = process.Pid;

            return ResultCode.Success;
        }

        [CommandTipc(115)]
        // QueryGrantedFeatures(u64 requested feature flags): the subset that is granted, all of them here.
        public ResultCode QueryGrantedFeatures(ServiceCtx context)
        {
            uint[] w = Nx2Tipc.Words(context);

            Logger.Info?.Print(LogClass.ServiceNv, $"nx2:gsv 115 features {Nx2Tipc.Pair(w, 0):x}");
            Nx2Tipc.Reply(context, w[0], w[1]);

            return ResultCode.Success;
        }

        [CommandTipc(126)]
        // GetSessionFlag: one bit of the session's state (meaning unknown); clear.
        public ResultCode GetSessionFlag(ServiceCtx context)
        {
            Nx2Tipc.Reply(context, 0);

            return ResultCode.Success;
        }

        [CommandTipc(434)]
        // AttachGpuAddressSpace(u32 id from nx2:gpu 33)
        public ResultCode AttachGpuAddressSpace(ServiceCtx context)
        {
            _addressSpaces.Add(Nx2Tipc.Words(context)[0]);

            return ResultCode.Success;
        }

        [CommandTipc(1111)]
        // SetFeatureVersion(u32): a version or mode number the server checks and records.
        public ResultCode SetFeatureVersion(ServiceCtx context)
        {
            Logger.Info?.Print(LogClass.ServiceNv, $"nx2:gsv 1111 value {Nx2Tipc.Words(context)[0]:x}");

            return ResultCode.Success;
        }

        [CommandTipc(202)]
        // OpenSyncptSession, used by nn::syncpt::sdk::InitializeEx.
        public ResultCode OpenSyncptSession(ServiceCtx context) => Nx2Tipc.ReturnSession(context, this, new INx2SyncptSession());

        [CommandTipc(305)]
        // OpenViSession(u64), used by nn::vi2::sdk::Initialize.
        public ResultCode OpenViSession(ServiceCtx context) => Nx2Tipc.ReturnSession(context, this, new INx2Vi2Session(_objects));

        [CommandTipc(497)]
        // OpenGpuSession(u64), used by nn::gpu::sdk::BootWithRemoteMemory.
        public ResultCode OpenGpuSession(ServiceCtx context) => Nx2Tipc.ReturnSession(context, this, new INx2GpuSession());

        [CommandTipc(1402)]
        // OpenNvsrvSession: nvsrv speaks the nvdrv INvDrvServices protocol, so serve it with nvdrv.
        public ResultCode OpenNvsrvSession(ServiceCtx context)
        {
            if (_clientMemory == null)
            {
                Logger.Error?.Print(LogClass.ServiceNv, "nx2:gsv 1402 called before the client process was registered.");

                return ResultCode.NotAllocated;
            }

            return Nx2Tipc.ReturnSession(context, this, new INvDrvServices(context, _clientMemory, _owner));
        }

        [CommandTipc(204)]
        // AttachSyncptMemory(u32 version, u32 max syncpoints, two transfer memories) -> two events.
        //
        // nn::syncpt::sdk::InitializeEx sends two transfer memories (0x1000 bytes RW, then 0x2000 bytes the
        // client maps read-only) and expects a header in the second one: u32 version (must be 1) at +0,
        // u32 base id at +4, u32 count at +8, then offsets of: the u64 syncpoint values (RO, +0xc), the RW
        // "armed" summary word (+0x10), the RO summary word (+0x14), the RW armed bits (+0x18) and the RO
        // acknowledge bits (+0x1c).
        // To wait, the client makes its RW bit differ from the RO bit, signals the first returned event (it
        // attaches that handle as writable), and waits on the second until the server makes the bits equal
        // again; it then rereads the value and rearms if the threshold isn't reached.
        public ResultCode AttachSyncptMemory(ServiceCtx context)
        {
            uint[] w = Nx2Tipc.Words(context);
            uint maxSyncpoints = Math.Min(w[1], (0x1000 - 0x40) / 8);
            int[] handles = context.Request.HandleDesc?.ToCopy ?? [];

            KTransferMemory rw = handles.Length > 0 ? context.Process.HandleTable.GetObject<KTransferMemory>(handles[0]) : null;
            KTransferMemory ro = handles.Length > 1 ? context.Process.HandleTable.GetObject<KTransferMemory>(handles[1]) : null;

            if (rw != null && ro != null && ro.Size >= 0x2000)
            {
                IVirtualMemoryManager memory = ro.Creator.CpuMemory;

                _syncptRw = rw.Address;
                _syncptRo = ro.Address;
                _syncptCount = maxSyncpoints;

                memory.Write(_syncptRo + 0x00, 1u); // Version
                memory.Write(_syncptRo + 0x04, 0u); // Base id (guess)
                memory.Write(_syncptRo + 0x08, maxSyncpoints);
                memory.Write(_syncptRo + 0x0c, SyncptValuesOffset);
                memory.Write(_syncptRo + 0x10, SyncptRwSummaryOffset);
                memory.Write(_syncptRo + 0x14, SyncptRoSummaryOffset);
                memory.Write(_syncptRo + 0x18, SyncptRwBitsOffset);
                memory.Write(_syncptRo + 0x1c, SyncptRoBitsOffset);

                _syncptMemory = memory;
                UpdateSyncptValues(context.Device.Gpu.Synchronization);
            }
            else
            {
                Logger.Error?.Print(LogClass.ServiceNv, "nx2:gsv 204: missing syncpt transfer memory");
            }

            _syncptRequestEvent ??= new KEvent(context.Device.System.KernelContext);
            _syncptNotifyEvent ??= new KEvent(context.Device.System.KernelContext);

            if (_syncptMemory != null && _syncptPoller == null)
            {
                SynchronizationManager synchronization = context.Device.Gpu.Synchronization;

                _syncptPoller = new Thread(() =>
                {
                    while (!_disposed)
                    {
                        Thread.Sleep(TimeSpan.FromMicroseconds(500));

                        try
                        {
                            PollSyncpts(synchronization);
                        }
                        catch (InvalidMemoryRegionException)
                        {
                            return;
                        }
                    }
                })
                {
                    Name = "HLE.Nx2SyncptPoll",
                    IsBackground = true,
                };

                _syncptPoller.Start();
            }

            // The first event is signalled by the client, so it gets the writable end.
            return Nx2Tipc.ReturnHandles(context, _syncptRequestEvent.WritableEvent, _syncptNotifyEvent.ReadableEvent);
        }

        // Mirrors the syncpoint values into the shared RO page.
        private void UpdateSyncptValues(SynchronizationManager synchronization)
        {
            uint count = Math.Min(_syncptCount, SynchronizationManager.MaxHardwareSyncpoints);

            for (uint id = 0; id < count; id++)
            {
                ulong value = synchronization.GetSyncpointValue(id);
                ulong address = _syncptRo + SyncptValuesOffset + id * 8;

                if (_syncptMemory.Read<ulong>(address) != value)
                {
                    _syncptMemory.Write(address, value);
                }
            }
        }

        // Answers every armed wait: the value page is current, so the client rechecks its threshold and
        // rearms if it still has to wait.
        private void PollSyncpts(SynchronizationManager synchronization)
        {
            uint words = (_syncptCount + 63) / 64;
            Span<ulong> armed = stackalloc ulong[0x40];

            for (uint i = 0; i < words; i++)
            {
                armed[(int)i] = _syncptMemory.Read<ulong>(_syncptRw + SyncptRwBitsOffset + i * 8);
            }

            UpdateSyncptValues(synchronization);

            bool acked = false;

            for (uint i = 0; i < words; i++)
            {
                ulong roAddress = _syncptRo + SyncptRoBitsOffset + i * 8;

                if (_syncptMemory.Read<ulong>(roAddress) != armed[(int)i])
                {
                    _syncptMemory.Write(roAddress, armed[(int)i]);
                    acked = true;
                }
            }

            ulong summary = _syncptMemory.Read<ulong>(_syncptRw + SyncptRwSummaryOffset);

            if (_syncptMemory.Read<ulong>(_syncptRo + SyncptRoSummaryOffset) != summary)
            {
                _syncptMemory.Write(_syncptRo + SyncptRoSummaryOffset, summary);
            }

            if (acked)
            {
                _syncptNotifyEvent.WritableEvent.Signal();
            }
        }

        [CommandTipc(1510)]
        // CalculateRequirements(InterModuleUserImagePreference[0x40 bytes], u32 count) -> u64 size, u64 alignment
        public ResultCode CalculateRequirements(ServiceCtx context)
        {
            uint[] w = Nx2Tipc.Words(context);
            Nx2ImageLayout layout = Nx2ImageLayout.CalculateNativeImageRequirements(w.AsSpan(0, 16), w[16]);

            Logger.Debug?.Print(LogClass.ServiceNv, $"nx2:gsv 1510 {layout.Width}x{layout.Height} -> size={layout.Size:x} align={layout.Alignment:x}");
            Nx2Tipc.Reply64(context, layout.Size, layout.Alignment);

            return ResultCode.Success;
        }

        [CommandTipc(435)]
        // CalculateNativeImageSize(preference[16], u32 count) -> u64 size
        public ResultCode CalculateNativeImageSize(ServiceCtx context)
        {
            uint[] w = Nx2Tipc.Words(context);

            Nx2Tipc.Reply64(context, Nx2ImageLayout.Calculate(w.AsSpan(0, 16)).Size * Math.Max(w[16], 1));

            return ResultCode.Success;
        }

        [CommandTipc(391)]
        [CommandTipc(691)]
        [CommandTipc(891)]
        [CommandTipc(1291)]
        [CommandTipc(1691)]
        // Pool size of a feature's images: (preference[16], u32 count) -> u64 size, u64 alignment
        public ResultCode CalculateFeatureImagePoolSize(ServiceCtx context)
        {
            uint[] w = Nx2Tipc.Words(context);
            Nx2ImageLayout layout = Nx2ImageLayout.Calculate(w.AsSpan(0, 16));

            Nx2Tipc.Reply64(context, layout.Size * Math.Max(w[16], 1), layout.Alignment);

            return ResultCode.Success;
        }

        [CommandTipc(111)]
        // CreateNativeMemory(u8 mode, u64 size) -> u32 native memory id
        public ResultCode CreateNativeMemory(ServiceCtx context)
        {
            uint[] w = Nx2Tipc.Words(context);
            ulong size = Nx2Tipc.Pair(w, 1);
            uint id;

            lock (_objects.Lock)
            {
                id = _objects.NextMemory++;
                _objects.Memories[id] = new Nx2NativeObjects.Memory { Size = size };
            }

            Logger.Info?.Print(LogClass.ServiceNv, $"nx2:gsv 111 CreateNativeMemory mode={w[0] & 0xff} size={size:x} -> id {id}");
            Nx2Tipc.Reply(context, id);

            return ResultCode.Success;
        }

        [CommandTipc(112)]
        public ResultCode DestroyNativeMemory(ServiceCtx context)
        {
            lock (_objects.Lock)
            {
                _objects.Memories.Remove(Nx2Tipc.Words(context)[0]);
            }

            return ResultCode.Success;
        }

        [CommandTipc(114)]
        // BindNativeMemory(u32 id, u64 address, u64 size, u64 flags, u64 offset): back native memory with
        // process memory.
        public ResultCode BindNativeMemory(ServiceCtx context)
        {
            uint[] w = Nx2Tipc.Words(context);

            BindMemory(w[0], Nx2Tipc.Pair(w, 1), Nx2Tipc.Pair(w, 3), 0, 0);

            return ResultCode.Success;
        }

        [CommandTipc(423)]
        // BindNativeGpuMemory(u32 id, u64 address, u64 size, u32 parent GpuMemoryHandle, u64 offset, ...):
        // back native memory with part of GPU-accessible memory.
        public ResultCode BindNativeGpuMemory(ServiceCtx context)
        {
            uint[] w = Nx2Tipc.Words(context);

            BindMemory(w[0], Nx2Tipc.Pair(w, 1), Nx2Tipc.Pair(w, 3), w[5], Nx2Tipc.Pair(w, 6));

            return ResultCode.Success;
        }

        private void BindMemory(uint id, ulong address, ulong size, uint parent, ulong parentOffset)
        {
            lock (_objects.Lock)
            {
                if (!_objects.Memories.TryGetValue(id, out Nx2NativeObjects.Memory memory))
                {
                    _objects.Memories[id] = memory = new Nx2NativeObjects.Memory();
                }

                memory.Address = address;
                memory.Parent = parent;
                memory.ParentOffset = parentOffset;

                if (memory.Size == 0)
                {
                    memory.Size = size;
                }
            }

            Logger.Info?.Print(LogClass.ServiceNv, $"nx2:gsv bind native memory {id} at {address:x}+{size:x}");
        }

        [CommandTipc(311)]
        [CommandTipc(321)]
        [CommandTipc(429)]
        [CommandTipc(615)]
        [CommandTipc(811)]
        [CommandTipc(1211)]
        [CommandTipc(1611)]
        // CreateNativeImage(preference[16], u32 count, u32 memory id) -> u32 native image id
        public ResultCode CreateNativeImage(ServiceCtx context)
        {
            uint[] w = Nx2Tipc.Words(context);
            uint id;
            Nx2NativeObjects.Image image = new()
            {
                Count = Math.Max(w[16], 1),
                MemoryId = w[17],
                Size = Nx2ImageLayout.Calculate(w.AsSpan(0, 16)).Size,
            };

            w.AsSpan(0, 16).CopyTo(image.Preference);

            lock (_objects.Lock)
            {
                id = _objects.NextImage;
                _objects.Images[id] = image;

                // nn::gs::sdk::CalculateNativeImageHandle(pool, i) is pool + i, so keep those free.
                _objects.NextImage += image.Count;
            }

            Logger.Info?.Print(LogClass.ServiceNv, $"nx2:gsv CreateNativeImage memory={w[17]} count={w[16]} size={image.Size:x} format={w[6]} -> id {id}");
            Nx2Tipc.Reply(context, id);

            return ResultCode.Success;
        }

        [CommandTipc(312)]
        [CommandTipc(430)]
        [CommandTipc(612)]
        [CommandTipc(812)]
        [CommandTipc(1212)]
        [CommandTipc(1612)]
        public ResultCode DestroyNativeImage(ServiceCtx context)
        {
            lock (_objects.Lock)
            {
                _objects.Images.Remove(Nx2Tipc.Words(context)[0]);
            }

            return ResultCode.Success;
        }

        [CommandTipc(426)] // MapNativeImage(u32 handle, u32, u32 address space, u8 permission)
        [CommandTipc(428)] // QueryNativeImage(u32 handle)
        // Reply: u64 GPU address of the image (426 only; GPU == CPU address), u64 offset of the image in its
        // memory, a GpuOunceUserImageDescriptor (32 words) and the flags.
        public ResultCode QueryNativeImage(ServiceCtx context)
        {
            uint handle = Nx2Tipc.Words(context)[0];
            bool map = (int)context.Request.Type - 0x10 == 426;
            uint[] preference = new uint[16];
            ulong imageOffset = 0;
            ulong memoryAddress = 0;

            lock (_objects.Lock)
            {
                Nx2NativeObjects.Image image = _objects.FindImage(handle, out uint first);

                if (image != null)
                {
                    preference = image.Preference;
                    imageOffset = (handle - first) * image.Size;

                    if (_objects.Memories.TryGetValue(image.MemoryId, out Nx2NativeObjects.Memory memory))
                    {
                        memoryAddress = memory.Address;
                    }
                }
            }

            Nx2ImageLayout layout = Nx2ImageLayout.Calculate(preference);
            ulong gpuAddress = map ? memoryAddress + imageOffset : 0;
            uint[] words = new uint[37];

            words[0] = (uint)gpuAddress;
            words[1] = (uint)(gpuAddress >> 32);
            words[2] = (uint)imageOffset;
            words[3] = (uint)(imageOffset >> 32);

            Span<uint> desc = words.AsSpan(4);

            desc[0] = 1;
            desc[1] = preference[1];
            desc[2] = preference[2];
            desc[4] = layout.BlockHeightLog2;
            desc[5] = (uint)(layout.Pitch / 64);
            desc[8] = 1;
            desc[9] = 1;
            desc[11] = (uint)layout.Size;

            Nx2Tipc.Reply(context, words);

            return ResultCode.Success;
        }

        [CommandTipc(427)]
        // UnmapNativeImage(u32 handle, u32 address space, u64 address, u64 last address)
        public ResultCode UnmapNativeImage(ServiceCtx context) => ResultCode.Success;

        [CommandTipc(314)] // DestroyNativeWindowAliasImagePool(u32 image)
        [CommandTipc(315)] // CreateNativeWindowAliasImagePool(u32 image, u32 count)
        public ResultCode ConfigureWindowImagePool(ServiceCtx context)
        {
            uint id = Nx2Tipc.Words(context)[0];

            lock (_objects.Lock)
            {
                if (_objects.Images.TryGetValue(id, out Nx2NativeObjects.Image image))
                {
                    image.WindowPool = (int)context.Request.Type - 0x10 == 315;
                }
            }

            return ResultCode.Success;
        }

        public void Dispose()
        {
            _disposed = true;
        }
    }
}
