using System;

namespace Ryujinx.HLE.HOS.Services.Nx2
{
    // Pixel format IDs in InterModuleUserImagePreference word 6.
    enum Nx2NativeImageFormat : uint
    {
        Rgba8 = 1,
        Rgb10A2 = 4,
        Rgba16Float = 5,
    }

    struct Nx2ImageLayout
    {
        public uint Width;
        public uint Height;
        public ulong Pitch;
        public uint BlockHeightLog2;
        public ulong Size;
        public ulong Alignment;

        private static ulong AlignUp(ulong value, ulong alignment) => (value + alignment - 1) / alignment * alignment;

        public static Nx2ImageLayout Calculate(ReadOnlySpan<uint> preference)
        {
            Nx2ImageLayout layout = new()
            {
                Width = (preference[2] & 0xFFFF) + 1,
                Height = (preference[2] >> 16) + 1,
            };

            ulong pitchGobs = preference[5];
            uint blockHeightLog2 = preference[4];
            uint bytesPerPixel = (Nx2NativeImageFormat)preference[6] == Nx2NativeImageFormat.Rgba16Float ? 8u : 4u;

            if (blockHeightLog2 <= 8 && pitchGobs >= ((ulong)layout.Width * bytesPerPixel + 63) / 64 && pitchGobs <= 4096)
            {
                layout.Pitch = pitchGobs * 64;
                layout.BlockHeightLog2 = blockHeightLog2;
            }
            else
            {
                layout.Pitch = AlignUp((ulong)layout.Width * bytesPerPixel, 64);
                layout.BlockHeightLog2 = 7;
            }

            ulong blockRows = 1UL << (int)layout.BlockHeightLog2;
            layout.Size = layout.Pitch * AlignUp(layout.Height, blockRows);
            layout.Alignment = Math.Max(64 * blockRows, 0x1000);

            return layout;
        }

        public static Nx2ImageLayout CalculateNativeImageRequirements(ReadOnlySpan<uint> preference, uint count)
        {
            Nx2ImageLayout layout = Calculate(preference);

            if (count != 1 || preference[0] != 0 || preference[1] != 2 || preference[3] != 0 ||
                preference[4] < 3 || preference[4] > 8 || preference[6] != 1 || preference[7] != 1 ||
                layout.Pitch != (ulong)preference[5] * 64 ||
                preference[8..16].ContainsAnyExcept(0u))
            {
                return layout;
            }

            layout.Alignment = 0x1000;

            return layout;
        }
    }
}
