using Ryujinx.Common.Logging;

namespace Ryujinx.HLE.HOS.Services.Am.AppletOE.ApplicationProxyService.ApplicationProxy
{
    // Switch 2: handed out by IApplicationProxy 60.
    class IGraphicsScaleController : IpcService, INx2StubService
    {
        public string InterfaceName => "nx2:IGraphicsScaleController";

        [CommandCmif(10)]
        // GetCurrentGraphicsScale() -> (u32 scale, u32, u32)
        public ResultCode GetCurrentGraphicsScale(ServiceCtx context)
        {
            // nn::oe::GetCurrentGraphicsScale only accepts 10, 20 or 30. The other two words are unknown.
            context.ResponseData.Write(10u);
            context.ResponseData.Write(0u);
            context.ResponseData.Write(0u);

            Logger.Stub?.PrintStub(LogClass.ServiceAm);

            return ResultCode.Success;
        }

        [CommandCmif(1050)]
        // SetGraphicsScaleChangedNotificationEnabled(bool)
        public ResultCode SetGraphicsScaleChangedNotificationEnabled(ServiceCtx context)
        {
            // The scale never changes here, so there is nothing to notify.
            return ResultCode.Success;
        }
    }
}
