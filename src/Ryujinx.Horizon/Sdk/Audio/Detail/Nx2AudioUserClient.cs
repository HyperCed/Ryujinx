using Ryujinx.Horizon.Common;
using Ryujinx.Horizon.Sdk.Sf;

namespace Ryujinx.Horizon.Sdk.Audio.Detail
{
    // Switch 2 "aud:u" (nn::audio::CreateUserClientSessionByHipc). The SDK reaches the Switch 1 audio
    // interfaces through it: command 2 returns an object used exactly like IAudioRendererManager and
    // command 1 one used like IAudioOutManager, so they are the registered audren:u / audout:u / audin:u
    // instances.
    partial class Nx2AudioUserClient : IServiceObject
    {
        private readonly IAudioInManager _audioInManager;
        private readonly IAudioOutManager _audioOutManager;
        private readonly IAudioRendererManager _audioRendererManager;

        public Nx2AudioUserClient(IAudioInManager audioInManager, IAudioOutManager audioOutManager, IAudioRendererManager audioRendererManager)
        {
            _audioInManager = audioInManager;
            _audioOutManager = audioOutManager;
            _audioRendererManager = audioRendererManager;
        }

        [CmifCommand(0)]
        public Result GetAudioInManager(out IAudioInManager manager)
        {
            manager = _audioInManager;

            return Result.Success;
        }

        [CmifCommand(1)]
        public Result GetAudioOutManager(out IAudioOutManager manager)
        {
            manager = _audioOutManager;

            return Result.Success;
        }

        [CmifCommand(2)]
        public Result GetAudioRendererManager(out IAudioRendererManager manager)
        {
            manager = _audioRendererManager;

            return Result.Success;
        }
    }
}
