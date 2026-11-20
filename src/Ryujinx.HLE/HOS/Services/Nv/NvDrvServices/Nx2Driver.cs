namespace Ryujinx.HLE.HOS.Services.Nv.NvDrvServices
{
    // State of the Switch 2 NVIDIA driver path. A caller is taken for a Switch 2 NvRm once it asks for
    // the 0xD0-byte GPU characteristics block (Switch 1: 0xA0).
    static class Nx2Driver
    {
        public const int CharacteristicsSize = 0xD0;

        public const ulong GpuVaStart = 0x8000000UL;
        public const ulong GpuVaEnd = 0xC000000000UL;

        public static volatile bool Active;
    }
}
