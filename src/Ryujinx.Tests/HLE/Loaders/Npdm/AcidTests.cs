using NUnit.Framework;
using Ryujinx.HLE.Loaders.Npdm;
using System.IO;

namespace Ryujinx.Tests.HLE.Loaders.Npdm
{
    class AcidTests
    {
        [TestCase(0x200, 0x100)]
        [TestCase(0x300, 0x200)]
        public void ParsesAcidKeyAreaSize(int keyAreaSize, int modulusSize)
        {
            const int acidOffset = 0x100;
            const int flags = 0x24;
            const long titleIdRangeMin = 0x0100000000000000;
            const long titleIdRangeMax = 0x010000000000FFFF;

            byte[] data = new byte[0x2000];

            using (MemoryStream stream = new(data, writable: true))
            using (BinaryWriter writer = new(stream))
            {
                long headerOffset = acidOffset + keyAreaSize;

                stream.Position = headerOffset;
                writer.Write('A' | ('C' << 8) | ('I' << 16) | ('D' << 24));
                writer.Write(0x100);
                writer.Write(0);
                writer.Write(flags);
                writer.Write(titleIdRangeMin);
                writer.Write(titleIdRangeMax);
                writer.Write(0x500);
                writer.Write(0x18);
                writer.Write(0x600);
                writer.Write(0);
                writer.Write(0x700);
                writer.Write(0);

                stream.Position = acidOffset + 0x500;
                writer.Write(7);
            }

            Acid acid = new(new MemoryStream(data), acidOffset);

            Assert.AreEqual(0x100, acid.Rsa2048Signature.Length);
            Assert.AreEqual(modulusSize, acid.Rsa2048Modulus.Length);
            Assert.AreEqual(flags, acid.Flags);
            Assert.AreEqual(titleIdRangeMin, acid.TitleIdRangeMin);
            Assert.AreEqual(titleIdRangeMax, acid.TitleIdRangeMax);
            Assert.AreEqual(7, acid.FsAccessControl.Version);
        }
    }
}