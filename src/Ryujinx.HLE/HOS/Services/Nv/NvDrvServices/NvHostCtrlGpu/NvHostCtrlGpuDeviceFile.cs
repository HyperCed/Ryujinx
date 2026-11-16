using Ryujinx.Common.Logging;
using Ryujinx.HLE.HOS.Kernel.Threading;
using Ryujinx.HLE.HOS.Services.Nv.NvDrvServices.NvHostCtrlGpu.Types;
using Ryujinx.Horizon.Common;
using Ryujinx.Memory;
using System;
using System.Diagnostics;

namespace Ryujinx.HLE.HOS.Services.Nv.NvDrvServices.NvHostCtrlGpu
{
    class NvHostCtrlGpuDeviceFile : NvDeviceFile
    {
        private static readonly Stopwatch _pTimer = new();
        private static readonly double _ticksToNs = (1.0 / Stopwatch.Frequency) * 1_000_000_000;

        private readonly KEvent _errorEvent;
        private readonly KEvent _unknownEvent;
        private bool _nx2Caller;

        public NvHostCtrlGpuDeviceFile(ServiceCtx context, IVirtualMemoryManager memory, ulong owner) : base(context, owner)
        {
            _errorEvent = new KEvent(context.Device.System.KernelContext);
            _unknownEvent = new KEvent(context.Device.System.KernelContext);
        }

        static NvHostCtrlGpuDeviceFile()
        {
            _pTimer.Start();
        }

        public override NvInternalResult Ioctl(NvIoctl command, Span<byte> arguments)
        {
            NvInternalResult result = NvInternalResult.NotImplemented;

            if (command.Type == NvIoctl.NvGpuMagic)
            {
                switch (command.Number)
                {
                    case 0x01:
                        result = CallIoctlMethod<ZcullGetCtxSizeArguments>(ZcullGetCtxSize, arguments);
                        break;
                    case 0x02:
                        result = CallIoctlMethod<ZcullGetInfoArguments>(ZcullGetInfo, arguments);
                        break;
                    case 0x03:
                        result = CallIoctlMethod<ZbcSetTableArguments>(ZbcSetTable, arguments);
                        break;
                    case 0x05:
                        result = CallIoctlMethod<GetCharacteristicsArguments>(GetCharacteristics, arguments);
                        break;
                    case 0x06:
                        result = CallIoctlMethod<GetTpcMasksArguments>(GetTpcMasks, arguments);
                        break;
                    case 0x12:
                        result = CallIoctlMethod<NumVsmsArguments>(NumVsms, arguments);
                        break;
                    case 0x13:
                        result = CallIoctlMethod<VsmsMappingArguments>(VsmsMapping, arguments);
                        break;
                    case 0x14:
                        result = CallIoctlMethod<GetActiveSlotMaskArguments>(GetActiveSlotMask, arguments);
                        break;
                    case 0x1c:
                        result = CallIoctlMethod<GetGpuTimeArguments>(GetGpuTime, arguments);
                        break;
                }
            }

            if (result == NvInternalResult.NotImplemented && _nx2Caller)
            {
                Logger.Warning?.Print(LogClass.ServiceNv, $"(STUBBED) Switch 2 ctrl-gpu ioctl type={command.Type:x} number={command.Number:x}");
                arguments.Clear();
                result = NvInternalResult.Success;
            }

            return result;
        }

        public override NvInternalResult Ioctl3(NvIoctl command, Span<byte> arguments, Span<byte> inlineOutBuffer)
        {
            NvInternalResult result = NvInternalResult.NotImplemented;

            if (command.Type == NvIoctl.NvGpuMagic)
            {
                switch (command.Number)
                {
                    case 0x05:
                        result = inlineOutBuffer.Length >= Nx2Driver.CharacteristicsSize
                            ? Nx2GetCharacteristics(arguments, inlineOutBuffer)
                            : CallIoctlMethod<GetCharacteristicsArguments, GpuCharacteristics>(GetCharacteristics, arguments, inlineOutBuffer);
                        break;
                    case 0x06:
                        result = CallIoctlMethod<GetTpcMasksArguments, int>(GetTpcMasks, arguments, inlineOutBuffer);
                        break;
                    case 0x12:
                        result = CallIoctlMethod<NumVsmsArguments>(NumVsms, arguments);
                        break;
                    case 0x13:
                        result = _nx2Caller
                            ? Nx2VsmsMapping(inlineOutBuffer)
                            : CallIoctlMethod<VsmsMappingArguments>(VsmsMapping, arguments);
                        break;
                    case 0x1f:
                        if (_nx2Caller)
                        {
                            result = Nx2GetEngineInfo(arguments, inlineOutBuffer);
                        }
                        break;
                }
            }

            if (result == NvInternalResult.NotImplemented && _nx2Caller)
            {
                // Switch 2 NvRm uses ioctls Switch 1 never had; answer them with a zeroed success so
                // device setup can continue, and log each one.
                Logger.Warning?.Print(LogClass.ServiceNv, $"(STUBBED) Switch 2 ctrl-gpu ioctl3 type={command.Type:x} number={command.Number:x}");
                arguments.Clear();
                inlineOutBuffer.Clear();
                result = NvInternalResult.Success;
            }

            return result;
        }

        // Switch 2 SDKs ask for a 0xD0-byte characteristics block. Their NvRm only accepts chip ids 0x17E,
        // 0x12B and 0x12E and its NVN path rejects the Maxwell ones, so report the Switch 2 Ampere chip.
        // Fields past the Switch 1 layout are left zero. Values are best guesses.
        private NvInternalResult Nx2GetCharacteristics(Span<byte> arguments, Span<byte> inlineOutBuffer)
        {
            _nx2Caller = true;
            Nx2Driver.Active = true;
            Context.Device.Gpu.Nx2Ampere = true;

            GetCharacteristicsArguments scratch = default;
            GpuCharacteristics characteristics = default;

            GetCharacteristics(ref scratch, ref characteristics);

#pragma warning disable IDE0055 // Disable formatting
            characteristics.Arch             = 0x170;  // Ampere
            characteristics.Impl             = 0xe;    // Chip id 0x17E as listed in the Switch 2 NvRm chip table
            characteristics.NumTpcPerGpc     = 0x6;
            characteristics.SmArchSmVersion  = 0x808;  // NVN2 only accepts SM 0x808 (its class table lookup)
            characteristics.SmArchSpaVersion = 0x808;
            characteristics.ThreedClass      = 0xc797; // AMPERE_B
            characteristics.ComputeClass     = 0xc7c0; // AMPERE_COMPUTE_B
            characteristics.GpfifoClass      = 0xc56f; // AMPERE_CHANNEL_GPFIFO_A
            characteristics.DmaCopyClass     = 0xc7b5; // AMPERE_DMA_COPY_B
            characteristics.ChipName         = 0x6631306167; // "ga10f"
#pragma warning restore IDE0055
            characteristics.Flags |= 0x100;      // NVGPU_GPU_FLAGS_SUPPORT_TSG: NvRm binds channels to a TSG
            characteristics.Flags |= 1L << 22;   // NVGPU_GPU_FLAGS_SUPPORT_TSG_SUBCONTEXTS
            characteristics.Flags |= 1L << 26;   // NVGPU_GPU_FLAGS_SUPPORT_SYNCPOINT_ADDRESS: syncpoint shim
            characteristics.Flags |= 1L << 34;   // NVGPU_GPU_FLAGS_SUPPORT_MAPPING_MODIFY
            characteristics.Flags |= 1L << 35;   // NVGPU_GPU_FLAGS_SUPPORT_REMAP
            characteristics.Flags |= 0x1fL << 40; // Engine support bits (2D, 3D, compute, inline-to-memory, ZBC)

            Span<byte> block = stackalloc byte[Nx2Driver.CharacteristicsSize];

            System.Runtime.InteropServices.MemoryMarshal.Write(block, in characteristics);

            // The Switch 2 layout keeps nine s16 interface versions at 0x70..0x80; its NvRm requires e.g.
            // [0x78] >= 6 and enables features above 0xa, 0x1f and 0x26.
            for (int offset = 0x70; offset <= 0x80; offset += 2)
            {
                System.Buffers.Binary.BinaryPrimitives.WriteInt16LittleEndian(block[offset..], 0x40);
            }

            // NvRm sizes each TSG's subcontext (VEID) allocator from the u32 at 0xb4.
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(block[0xb4..], 64);

            block.CopyTo(inlineOutBuffer);

            if (arguments.Length >= 8)
            {
                System.Buffers.Binary.BinaryPrimitives.WriteInt64LittleEndian(arguments, Nx2Driver.CharacteristicsSize);
            }

            if (arguments.Length > 16)
            {
                block[..Math.Min(block.Length, arguments.Length - 16)].CopyTo(arguments[16..]);
            }

            Logger.Warning?.Print(LogClass.ServiceNv, "Switch 2 caller, reporting Ampere chip 0x17E");

            return NvInternalResult.Success;
        }

        // NVGPU_GPU_IOCTL_VSMS_MAPPING: a buffer of {gpc_index, tpc_index} byte pairs, one per virtual SM.
        // The SDK treats a pair left at its 0xc0c0 sentinel as failure: one GPC, two SMs per TPC.
        private static NvInternalResult Nx2VsmsMapping(Span<byte> inlineOutBuffer)
        {
            for (int i = 0; i + 1 < inlineOutBuffer.Length; i += 2)
            {
                inlineOutBuffer[i] = 0;
                inlineOutBuffer[i + 1] = (byte)(i / 4);
            }

            return NvInternalResult.Success;
        }

        // nvgpu GET_ENGINE_INFO as the Switch 2 NvRm uses it: args {u32 buf_size, u32 reserved, u64 buf_addr},
        // queried once for the size and once with an inline output buffer of 16-byte items
        // {u32 engine_id, u32 engine_instance, s32 runlist_id, u32 reserved}.
        private static NvInternalResult Nx2GetEngineInfo(Span<byte> arguments, Span<byte> inlineOutBuffer)
        {
            ReadOnlySpan<int> items =
            [
                0, 0, 0, 0, // NVGPU_ENGINE_GR
                2, 0, 1, 0, // NVGPU_ENGINE_ASYNC_COPY
            ];
            ReadOnlySpan<byte> itemBytes = System.Runtime.InteropServices.MemoryMarshal.AsBytes(items);

            uint bufferSize = arguments.Length >= 4 ? System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(arguments) : 0;

            if (bufferSize >= itemBytes.Length && inlineOutBuffer.Length >= itemBytes.Length)
            {
                itemBytes.CopyTo(inlineOutBuffer);
            }

            if (arguments.Length >= 4)
            {
                System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(arguments, (uint)itemBytes.Length);
            }

            return NvInternalResult.Success;
        }

        public override NvInternalResult QueryEvent(out int eventHandle, uint eventId)
        {
            // TODO: accurately represent and implement those events.
            KEvent targetEvent = null;

            switch (eventId)
            {
                case 0x1:
                    targetEvent = _errorEvent;
                    break;
                case 0x2:
                    targetEvent = _unknownEvent;
                    break;
            }

            if (targetEvent != null)
            {
                if (Context.Process.HandleTable.GenerateHandle(targetEvent.ReadableEvent, out eventHandle) != Result.Success)
                {
                    throw new InvalidOperationException("Out of handles!");
                }
            }
            else
            {
                eventHandle = 0;

                return NvInternalResult.InvalidInput;
            }

            return NvInternalResult.Success;
        }

        public override void Close() { }

        private NvInternalResult ZcullGetCtxSize(ref ZcullGetCtxSizeArguments arguments)
        {
            arguments.Size = 1;

            return NvInternalResult.Success;
        }

        private NvInternalResult ZcullGetInfo(ref ZcullGetInfoArguments arguments)
        {
#pragma warning disable IDE0055 // Disable formatting
            arguments.WidthAlignPixels           = 0x20;
            arguments.HeightAlignPixels          = 0x20;
            arguments.PixelSquaresByAliquots     = 0x400;
            arguments.AliquotTotal               = 0x800;
            arguments.RegionByteMultiplier       = 0x20;
            arguments.RegionHeaderSize           = 0x20;
            arguments.SubregionHeaderSize        = 0xc0;
            arguments.SubregionWidthAlignPixels  = 0x20;
            arguments.SubregionHeightAlignPixels = 0x40;
            arguments.SubregionCount             = 0x10;
#pragma warning restore IDE0055

            return NvInternalResult.Success;
        }

        private NvInternalResult ZbcSetTable(ref ZbcSetTableArguments arguments)
        {
            Logger.Stub?.PrintStub(LogClass.ServiceNv);

            return NvInternalResult.Success;
        }

        private NvInternalResult GetCharacteristics(ref GetCharacteristicsArguments arguments)
        {
            return GetCharacteristics(ref arguments, ref arguments.Characteristics);
        }

        private NvInternalResult GetCharacteristics(ref GetCharacteristicsArguments arguments, ref GpuCharacteristics characteristics)
        {
            arguments.Header.BufferSize = 0xa0;

#pragma warning disable IDE0055 // Disable formatting
            characteristics.Arch                   = 0x120;
            characteristics.Impl                   = 0xb;
            characteristics.Rev                    = 0xa1;
            characteristics.NumGpc                 = 0x1;
            characteristics.L2CacheSize            = 0x40000;
            characteristics.OnBoardVideoMemorySize = 0x0;
            characteristics.NumTpcPerGpc           = 0x2;
            characteristics.BusType                = 0x20;
            characteristics.BigPageSize            = 0x20000;
            characteristics.CompressionPageSize    = 0x20000;
            characteristics.PdeCoverageBitCount    = 0x1b;
            characteristics.AvailableBigPageSizes  = 0x30000;
            characteristics.GpcMask                = 0x1;
            characteristics.SmArchSmVersion        = 0x503;
            characteristics.SmArchSpaVersion       = 0x503;
            characteristics.SmArchWarpCount        = 0x80;
            characteristics.GpuVaBitCount          = 0x28;
            characteristics.Reserved               = 0x0;
            characteristics.Flags                  = 0x55;
            characteristics.TwodClass              = 0x902d;
            characteristics.ThreedClass            = 0xb197;
            characteristics.ComputeClass           = 0xb1c0;
            characteristics.GpfifoClass            = 0xb06f;
            characteristics.InlineToMemoryClass    = 0xa140;
            characteristics.DmaCopyClass           = 0xb0b5;
            characteristics.MaxFbpsCount           = 0x1;
            characteristics.FbpEnMask              = 0x0;
            characteristics.MaxLtcPerFbp           = 0x2;
            characteristics.MaxLtsPerLtc           = 0x1;
            characteristics.MaxTexPerTpc           = 0x0;
            characteristics.MaxGpcCount            = 0x1;
            characteristics.RopL2EnMask0           = 0x21d70;
            characteristics.RopL2EnMask1           = 0x0;
            characteristics.ChipName               = 0x6230326d67;
            characteristics.GrCompbitStoreBaseHw   = 0x0;
#pragma warning restore IDE0055

            arguments.Characteristics = characteristics;

            return NvInternalResult.Success;
        }

        private NvInternalResult GetTpcMasks(ref GetTpcMasksArguments arguments)
        {
            return GetTpcMasks(ref arguments, ref arguments.TpcMask);
        }

        private NvInternalResult GetTpcMasks(ref GetTpcMasksArguments arguments, ref int tpcMask)
        {
            if (arguments.MaskBufferSize != 0)
            {
                tpcMask = 3;
                arguments.TpcMask = tpcMask;
            }

            return NvInternalResult.Success;
        }

        private NvInternalResult NumVsms(ref NumVsmsArguments arguments)
        {
            Logger.Stub?.PrintStub(LogClass.ServiceNv);

            arguments.NumVsms = 2;

            return NvInternalResult.Success;
        }

        private NvInternalResult VsmsMapping(ref VsmsMappingArguments arguments)
        {
            Logger.Stub?.PrintStub(LogClass.ServiceNv);

            arguments.Sm0GpcIndex = 0;
            arguments.Sm0TpcIndex = 0;
            arguments.Sm1GpcIndex = 0;
            arguments.Sm1TpcIndex = 1;

            return NvInternalResult.Success;
        }

        private NvInternalResult GetActiveSlotMask(ref GetActiveSlotMaskArguments arguments)
        {
            Logger.Stub?.PrintStub(LogClass.ServiceNv);

            arguments.Slot = 0x07;
            arguments.Mask = 0x01;

            return NvInternalResult.Success;
        }

        private NvInternalResult GetGpuTime(ref GetGpuTimeArguments arguments)
        {
            arguments.Timestamp = GetPTimerNanoSeconds();

            return NvInternalResult.Success;
        }

        private static ulong GetPTimerNanoSeconds()
        {
            double ticks = _pTimer.ElapsedTicks;

            return (ulong)(ticks * _ticksToNs) & 0xff_ffff_ffff_ffff;
        }
    }
}
