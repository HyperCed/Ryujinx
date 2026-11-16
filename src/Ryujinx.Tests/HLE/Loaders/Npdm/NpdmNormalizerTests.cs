using LibHac.Loader;
using NUnit.Framework;
using Ryujinx.Common;
using Ryujinx.HLE.Loaders.Npdm;
using System.Buffers.Binary;
using System;

namespace Ryujinx.Tests.HLE.Loaders.Npdm
{
    class NpdmNormalizerTests
    {
        [Test]
        public void NormalizesSwitch2AcidLayoutForLibHac()
        {
            byte[] switch1Npdm = EmbeddedResources.Read("Ryujinx.HLE/Homebrew.npdm");
            MetaLoader originalLoader = new();
            Assert.IsTrue(originalLoader.Load(switch1Npdm).IsSuccess());

            LibHac.Loader.Npdm originalNpdm;
            Assert.IsTrue(originalLoader.GetNpdm(out originalNpdm).IsSuccess());

            byte[] switch2Npdm = AddSwitch2AcidKeyArea(switch1Npdm);
            MetaLoader unnormalizedLoader = new();
            Assert.IsTrue(unnormalizedLoader.Load(switch2Npdm).IsFailure());

            byte[] normalizedNpdm = NpdmNormalizer.NormalizeForLibHac(switch2Npdm);
            MetaLoader normalizedLoader = new();
            LibHac.Result normalizedResult = normalizedLoader.Load(normalizedNpdm);
            Assert.IsTrue(normalizedResult.IsSuccess(), normalizedResult.ToString());

            LibHac.Loader.Npdm normalized;
            Assert.IsTrue(normalizedLoader.GetNpdm(out normalized).IsSuccess());
            Assert.AreEqual(originalNpdm.Aci.ProgramId, normalized.Aci.ProgramId);
            Assert.AreEqual(originalNpdm.Acid.Flags, normalized.Acid.Flags);
            Assert.AreEqual(originalNpdm.Acid.Size, normalized.Acid.Size);
            Assert.AreEqual(originalNpdm.KernelCapabilityData.ToArray(), normalized.KernelCapabilityData.ToArray());
            Assert.AreEqual(originalNpdm.FsAccessControlData.ToArray(), normalized.FsAccessControlData.ToArray());
        }

        private static byte[] AddSwitch2AcidKeyArea(byte[] npdm)
        {
            MetaLoader loader = new();
            Assert.IsTrue(loader.Load(npdm).IsSuccess());
            LibHac.Loader.Npdm parsed;
            Assert.IsTrue(loader.GetNpdm(out parsed).IsSuccess());

            int acidOffset = parsed.Meta.AcidOffset;
            int acidSize = parsed.Meta.AcidSize;
            int aciOffset = parsed.Meta.AciOffset;
            int insertionOffset = acidOffset + 0x200;
            byte[] expanded = new byte[npdm.Length + 0x100];

            npdm.AsSpan(0, insertionOffset).CopyTo(expanded);
            npdm.AsSpan(insertionOffset).CopyTo(expanded.AsSpan(insertionOffset + 0x100));

            BinaryPrimitives.WriteInt32LittleEndian(expanded.AsSpan(0x70), aciOffset + 0x100);
            BinaryPrimitives.WriteInt32LittleEndian(expanded.AsSpan(0x7c), acidSize + 0x100);

            int acidHeaderOffset = acidOffset + 0x300;
            AddInt32(expanded, acidHeaderOffset + 4, 0x100);
            AddInt32(expanded, acidHeaderOffset + 0x20, 0x100);
            AddInt32(expanded, acidHeaderOffset + 0x28, 0x100);
            AddInt32(expanded, acidHeaderOffset + 0x30, 0x100);

            return expanded;
        }

        private static void AddInt32(byte[] data, int offset, int value)
        {
            int current = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(offset));

            if (current != 0)
            {
                BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(offset), current + value);
            }
        }
    }
}