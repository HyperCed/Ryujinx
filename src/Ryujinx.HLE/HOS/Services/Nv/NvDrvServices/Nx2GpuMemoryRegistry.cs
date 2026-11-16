using Ryujinx.Common.Logging;
using Ryujinx.Graphics.Gpu.Memory;
using System.Collections.Generic;

namespace Ryujinx.HLE.HOS.Services.Nv.NvDrvServices
{
    // GPU mappings a Switch 2 process made through nx2:gpu: CreateGpuAccessibleMemory (sub 101) maps the
    // memory at GPU address == CPU address, and NormalMapGpuMemory4K (211) maps a memory handle at a chosen
    // GPU address. Switch 2 titles never map through nvhost-as-gpu, so every nvhost-as-gpu address space
    // applies each mapping, including ones made before the address space existed.
    static class Nx2GpuMemoryRegistry
    {
        private static readonly object _lock = new();
        private static readonly SortedDictionary<ulong, (ulong CpuAddress, ulong Size)> _mappings = [];
        private static readonly List<MemoryManager> _listeners = [];

        public static void Reset()
        {
            lock (_lock)
            {
                _mappings.Clear();
                _listeners.Clear();
            }
        }

        public static void AddListener(MemoryManager gmm)
        {
            lock (_lock)
            {
                _listeners.Add(gmm);

                foreach ((ulong gpuAddress, (ulong cpuAddress, ulong size)) in _mappings)
                {
                    gmm.Map(cpuAddress, gpuAddress, size, PteKind.Pitch);
                }
            }
        }

        public static void RemoveListener(MemoryManager gmm)
        {
            lock (_lock)
            {
                _listeners.Remove(gmm);
            }
        }

        public static void Register(ulong address, ulong size) => MapAt(address, address, size);

        public static void MapAt(ulong gpuAddress, ulong cpuAddress, ulong size)
        {
            Logger.Debug?.Print(LogClass.ServiceNv, $"Switch 2 GPU mapping {gpuAddress:x}+{size:x} -> CPU {cpuAddress:x}");

            lock (_lock)
            {
                RemoveRange(gpuAddress, size);
                _mappings[gpuAddress] = (cpuAddress, size);

                foreach (MemoryManager gmm in _listeners)
                {
                    gmm.Map(cpuAddress, gpuAddress, size, PteKind.Pitch);
                }
            }
        }

        public static void Unmap(ulong gpuAddress, ulong size)
        {
            Logger.Debug?.Print(LogClass.ServiceNv, $"Switch 2 GPU unmapping {gpuAddress:x}+{size:x}");

            lock (_lock)
            {
                RemoveRange(gpuAddress, size);

                foreach (MemoryManager gmm in _listeners)
                {
                    gmm.Unmap(gpuAddress, size);
                }
            }
        }

        // Drops [gpuAddress, +size) from the recorded mappings, keeping the parts of mappings outside it.
        private static void RemoveRange(ulong gpuAddress, ulong size)
        {
            ulong end = gpuAddress + size;
            List<(ulong Address, ulong CpuAddress, ulong Size)> overlaps = [];

            foreach ((ulong address, (ulong cpuAddress, ulong mappingSize)) in _mappings)
            {
                if (address >= end)
                {
                    break;
                }

                if (address + mappingSize > gpuAddress)
                {
                    overlaps.Add((address, cpuAddress, mappingSize));
                }
            }

            foreach ((ulong address, ulong cpuAddress, ulong mappingSize) in overlaps)
            {
                _mappings.Remove(address);

                if (address < gpuAddress)
                {
                    _mappings[address] = (cpuAddress, gpuAddress - address);
                }

                if (address + mappingSize > end)
                {
                    _mappings[end] = (cpuAddress + (end - address), address + mappingSize - end);
                }
            }
        }
    }
}
