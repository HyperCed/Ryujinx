using Ryujinx.Common.Logging;
using Ryujinx.HLE.HOS.Kernel.Threading;

namespace Ryujinx.HLE.HOS.Services.Nx2
{
    // The Switch 2 video decoder (nn::codec::video::detail::ipc), port of Lichii's INx2VideoSession without
    // the decoding: gsv 902/905/906/1002 hand out a session whose TIPC 1 (CreateVideoDecoderService)
    // creates a decoder service, another session of this class. Every other command succeeds with a zeroed
    // reply (initialize 200/201/202/210, configure 101, start/stop/flush 6/207/8, finalize 115, buffers
    // 311/314/212, input 109/400, output 213), so movies play with no picture instead of aborting the game.
    class INx2VideoSession : IpcService, INx2StubService
    {
        private KEvent _event;

        public string InterfaceName { get; }

        public bool IsZeroedCommand(int commandId) => true;

        public INx2VideoSession(string name) : base(registerTipc: true)
        {
            InterfaceName = name;
        }

        [CommandTipc(1)]
        // CreateVideoDecoderService: a decoder service session.
        public ResultCode CreateDecoderService(ServiceCtx context)
        {
            Logger.Info?.Print(LogClass.ServiceNv, $"{InterfaceName} 1: creating a video decoder service (no decoding)");

            return Nx2Tipc.ReturnSession(context, this, new INx2VideoSession(InterfaceName + ":decoder"));
        }

        [CommandTipc(3)]
        // GetEvent: the submission-ready event.
        public ResultCode GetEvent(ServiceCtx context)
        {
            _event ??= new KEvent(context.Device.System.KernelContext);

            return Nx2Tipc.ReturnEvent(context, _event);
        }
    }
}
