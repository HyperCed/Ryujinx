using System;

namespace Ryujinx.Cpu.AppleHv
{
    class HvIpaAllocator
    {
        private const ulong AllocationGranule = 1UL << 14;
        // The default VM (hv_vm_create without a configuration) has a 36-bit IPA space: room for a 32 GiB
        // DRAM (MemoryConfiguration32GiB) next to the page tables.
        private const ulong IpaRegionSize = 1UL << 36;

        private readonly PrivateMemoryAllocator.Block _block;

        public HvIpaAllocator()
        {
            _block = new PrivateMemoryAllocator.Block(null, IpaRegionSize);
        }

        public ulong Allocate(ulong size, ulong alignment = AllocationGranule)
        {
            ulong offset = _block.Allocate(size, alignment);

            if (offset == PrivateMemoryAllocator.InvalidOffset)
            {
                throw new InvalidOperationException($"No enough free IPA memory to allocate 0x{size:X} bytes with alignment 0x{alignment:X}.");
            }

            return offset;
        }

        public void Free(ulong offset, ulong size)
        {
            _block.Free(offset, size);
        }
    }
}
