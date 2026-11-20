using NUnit.Framework;
using Ryujinx.HLE.HOS.Services.Nx2;

namespace Ryujinx.Tests.HLE.Services.Nx2
{
    class Nx2ImageLayoutTests
    {
        private static uint[] MakePreference(uint width, uint height, uint blockHeightLog2, uint pitchGobs, uint format)
        {
            uint[] preference = new uint[16];

            preference[1] = 2;
            preference[2] = (width - 1) | ((height - 1) << 16);
            preference[4] = blockHeightLog2;
            preference[5] = pitchGobs;
            preference[6] = format;
            preference[7] = 1;

            return preference;
        }

        [Test]
        public void BlockLinearRgba8UsesTheRequestedPitchAndBlockHeight()
        {
            // The 2560x1440 window images Mario Kart World creates.
            Nx2ImageLayout layout = Nx2ImageLayout.Calculate(MakePreference(2560, 1440, 7, 160, (uint)Nx2NativeImageFormat.Rgba8));

            Assert.That(layout.Width, Is.EqualTo(2560u));
            Assert.That(layout.Height, Is.EqualTo(1440u));
            Assert.That(layout.Pitch, Is.EqualTo(0x2800ul));
            Assert.That(layout.Size, Is.EqualTo(0xf00000ul));
            Assert.That(layout.Alignment, Is.EqualTo(0x2000ul));
        }

        [Test]
        public void TooSmallPitchFallsBackToTheAlignedWidth()
        {
            Nx2ImageLayout layout = Nx2ImageLayout.Calculate(MakePreference(1280, 720, 4, 1, (uint)Nx2NativeImageFormat.Rgba16Float));

            Assert.That(layout.Pitch, Is.EqualTo(1280ul * 8));
            Assert.That(layout.BlockHeightLog2, Is.EqualTo(7u));
            Assert.That(layout.Size, Is.EqualTo(1280ul * 8 * 768));
        }

        [Test]
        public void SingleWindowImageOnlyNeedsPageAlignment()
        {
            uint[] preference = MakePreference(2560, 1440, 7, 160, (uint)Nx2NativeImageFormat.Rgba8);

            Assert.That(Nx2ImageLayout.CalculateNativeImageRequirements(preference, 1).Alignment, Is.EqualTo(0x1000ul));
            Assert.That(Nx2ImageLayout.CalculateNativeImageRequirements(preference, 2).Alignment, Is.EqualTo(0x2000ul));
        }
    }
}
