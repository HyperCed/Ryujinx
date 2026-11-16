using System.Buffers.Binary;
using System;

namespace Ryujinx.HLE.Loaders.Npdm
{
    static class NpdmNormalizer
    {
        private const int MetaHeaderSize = 0x80;
        private const int StandardAcidKeyAreaSize = 0x200;
        private const int Switch2AcidKeyAreaSize = 0x300;
        private const int Switch2ExtraKeyAreaSize = Switch2AcidKeyAreaSize - StandardAcidKeyAreaSize;
        private const uint MetaMagic = 0x4154454d;
        private const uint AcidMagic = 0x44494341;

        public static byte[] NormalizeForLibHac(byte[] npdm)
        {
            if (npdm.Length < MetaHeaderSize || ReadUInt32(npdm, 0) != MetaMagic)
            {
                return npdm;
            }

            int aciOffset = ReadInt32(npdm, 0x70);
            int aciSize = ReadInt32(npdm, 0x74);
            int acidOffset = ReadInt32(npdm, 0x78);
            int acidSize = ReadInt32(npdm, 0x7c);

            if (aciOffset < MetaHeaderSize || aciSize < 0 || aciOffset > npdm.Length - aciSize ||
                acidOffset < MetaHeaderSize || acidSize < Switch2AcidKeyAreaSize + 0x38 ||
                acidOffset > npdm.Length - acidSize ||
                ReadUInt32(npdm, acidOffset + StandardAcidKeyAreaSize) == AcidMagic ||
                ReadUInt32(npdm, acidOffset + Switch2AcidKeyAreaSize) != AcidMagic)
            {
                return npdm;
            }

            int removeOffset = acidOffset + StandardAcidKeyAreaSize;

            if (aciOffset < removeOffset)
            {
                return npdm;
            }

            byte[] normalized = new byte[npdm.Length - Switch2ExtraKeyAreaSize];
            npdm.AsSpan(0, removeOffset).CopyTo(normalized);
            npdm.AsSpan(removeOffset + Switch2ExtraKeyAreaSize).CopyTo(normalized.AsSpan(removeOffset));

            WriteInt32(normalized, 0x70, aciOffset - Switch2ExtraKeyAreaSize);
            WriteInt32(normalized, 0x78, acidOffset);
            WriteInt32(normalized, 0x7c, acidSize - Switch2ExtraKeyAreaSize);

            int acidHeaderOffset = acidOffset + StandardAcidKeyAreaSize;
            WriteInt32(normalized, acidHeaderOffset + 4, ReadInt32(normalized, acidHeaderOffset + 4) - Switch2ExtraKeyAreaSize);

            if (!AdjustSubregionOffset(normalized, acidHeaderOffset, 0x20, 0x24, acidSize) ||
                !AdjustSubregionOffset(normalized, acidHeaderOffset, 0x28, 0x2c, acidSize) ||
                !AdjustSubregionOffset(normalized, acidHeaderOffset, 0x30, 0x34, acidSize))
            {
                return npdm;
            }

            return normalized;
        }

        private static bool AdjustSubregionOffset(byte[] npdm, int acidHeaderOffset, int offsetField, int sizeField, int acidSize)
        {
            int offset = ReadInt32(npdm, acidHeaderOffset + offsetField);
            int size = ReadInt32(npdm, acidHeaderOffset + sizeField);

            if (offset < Switch2AcidKeyAreaSize || size < 0 || offset > acidSize - size)
            {
                return false;
            }

            WriteInt32(npdm, acidHeaderOffset + offsetField, offset - Switch2ExtraKeyAreaSize);

            return true;
        }

        private static int ReadInt32(byte[] data, int offset) => BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(offset));

        private static uint ReadUInt32(byte[] data, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset));

        private static void WriteInt32(byte[] data, int offset, int value) => BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(offset), value);
    }
}