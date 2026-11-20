using Ryujinx.Common.Memory;
using Ryujinx.Cpu;
using Ryujinx.Graphics.Device;
using Ryujinx.Graphics.Gpu.Image;
using Ryujinx.Graphics.Gpu.Shader;
using Ryujinx.Memory;
using Ryujinx.Memory.Range;
using Ryujinx.Memory.Tracking;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;

namespace Ryujinx.Graphics.Gpu.Memory
{
    /// <summary>
    /// Represents physical memory, accessible from the GPU.
    /// This is actually working CPU virtual addresses, of memory mapped on the application process.
    /// </summary>
    class PhysicalMemory : IDisposable
    {
        private readonly GpuContext _context;
        private readonly IVirtualMemoryManagerTracked _cpuMemory;
        private int _referenceCount;

        /// <summary>
        /// In-memory shader cache.
        /// </summary>
        public ShaderCache ShaderCache { get; }

        /// <summary>
        /// GPU buffer manager.
        /// </summary>
        public BufferCache BufferCache { get; }

        /// <summary>
        /// GPU texture manager.
        /// </summary>
        public TextureCache TextureCache { get; }

        /// <summary>
        /// Creates a new instance of the physical memory.
        /// </summary>
        /// <param name="context">GPU context that the physical memory belongs to</param>
        /// <param name="cpuMemory">CPU memory manager of the application process</param>
        private int _nx2SparseWrites;

        public bool Nx2InSparseRegion(ulong gpuVa) => _context.Nx2Ampere && _context.Nx2InSparseRegion(gpuVa);

        public void Nx2NoteSparseWrite(ulong gpuVa, ulong size)
        {
            if (!_context.Nx2Ampere || !_context.Nx2InSparseRegion(gpuVa))
            {
                return;
            }

            if (_nx2SparseWrites++ < 32)
            {
                Ryujinx.Common.Logging.Logger.Info?.Print(Ryujinx.Common.Logging.LogClass.Gpu, $"Switch 2 GPU write into a sparse region: 0x{gpuVa:X}+0x{size:x} ({Environment.StackTrace.Split('\n')[4].Trim()})");
            }

            _context.Nx2VirtualTextures.Mapped(gpuVa, size);
        }

        public PhysicalMemory(GpuContext context, IVirtualMemoryManagerTracked cpuMemory)
        {
            _context = context;
            _cpuMemory = cpuMemory;
            ShaderCache = new ShaderCache(context);
            BufferCache = new BufferCache(context, this);
            TextureCache = new TextureCache(context, this);

            if (cpuMemory is IRefCounted rc)
            {
                rc.IncrementReferenceCount();
            }

            _referenceCount = 1;
        }

        /// <summary>
        /// Increments the memory reference count.
        /// </summary>
        public void IncrementReferenceCount()
        {
            Interlocked.Increment(ref _referenceCount);
        }

        /// <summary>
        /// Decrements the memory reference count.
        /// </summary>
        public void DecrementReferenceCount()
        {
            if (Interlocked.Decrement(ref _referenceCount) == 0 && _cpuMemory is IRefCounted rc)
            {
                rc.DecrementReferenceCount();
            }
        }

        /// <summary>
        /// Creates a new device memory manager.
        /// </summary>
        /// <returns>The memory manager</returns>
        public DeviceMemoryManager CreateDeviceMemoryManager()
        {
            return new DeviceMemoryManager(_cpuMemory);
        }

        /// <summary>
        /// Gets a host pointer for a given range of application memory.
        /// If the memory region is not a single contiguous block, this method returns 0.
        /// </summary>
        /// <remarks>
        /// Getting a host pointer is unsafe. It should be considered invalid immediately if the GPU memory is unmapped.
        /// </remarks>
        /// <param name="range">Ranges of physical memory where the target data is located</param>
        /// <returns>Pointer to the range of memory</returns>
        public nint GetHostPointer(MultiRange range)
        {
            if (range.Count == 1)
            {
                MemoryRange singleRange = range.GetSubRange(0);
                if (singleRange.Address != MemoryManager.PteUnmapped)
                {
                    IEnumerable<HostMemoryRange> regions = _cpuMemory.GetHostRegions(singleRange.Address, singleRange.Size);

                    if (regions != null && regions.Count() == 1)
                    {
                        return (nint)regions.First().Address;
                    }
                }
            }

            return 0;
        }

        /// <summary>
        /// Gets a span of data from the application process.
        /// </summary>
        /// <param name="address">Start address of the range</param>
        /// <param name="size">Size in bytes to be range</param>
        /// <param name="tracked">True if read tracking is triggered on the span</param>
        /// <returns>A read only span of the data at the specified memory location</returns>
        public ReadOnlySpan<byte> GetSpan(ulong address, int size, bool tracked = false)
        {
            return _cpuMemory.GetSpan(address, size, tracked);
        }

        /// <summary>
        /// Gets a span of data from the application process.
        /// </summary>
        /// <param name="range">Ranges of physical memory where the data is located</param>
        /// <param name="tracked">True if read tracking is triggered on the span</param>
        /// <returns>A read only span of the data at the specified memory location</returns>
        private static int _nx2UnmappedReads;

        /// <summary>
        /// An ASTC void-extent block: constant colour (0, 0, 0, 0), valid for every block size. Zero bytes
        /// are a reserved block mode that GPUs decode as the error colour, magenta.
        /// </summary>
        public static readonly byte[] AstcTransparentBlock = [0xfc, 0xfd, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0, 0, 0, 0, 0, 0, 0, 0];

        public ReadOnlySpan<byte> GetSpan(MultiRange range, bool tracked = false) => GetSpan(range, tracked, null);

        /// <summary>
        /// Gets the data of memory ranges, unmapped parts filled with a 16-byte pattern (zero without one).
        /// Switch 2 virtual textures read their non-resident tiles that way: as ASTC zero blocks would be
        /// magenta, they get void-extent blocks instead.
        /// </summary>
        public ReadOnlySpan<byte> GetSpan(MultiRange range, bool tracked, byte[] unmappedPattern)
        {
            if (range.Count == 1)
            {
                MemoryRange singleRange = range.GetSubRange(0);
                if (singleRange.Address != MemoryManager.PteUnmapped)
                {
                    return _cpuMemory.GetSpan(singleRange.Address, (int)singleRange.Size, tracked);
                }
            }

            Span<byte> data = new byte[range.GetSize()];

            int offset = 0;

            for (int i = 0; i < range.Count; i++)
            {
                MemoryRange currentRange = range.GetSubRange(i);
                int size = (int)currentRange.Size;

                // Switch 2 sparse textures: a GPU mapping can name CPU memory the process does not map.
                if (unmappedPattern != null && (currentRange.Address == MemoryManager.PteUnmapped || !_cpuMemory.IsRangeMapped(currentRange.Address, (ulong)size)))
                {
                    Span<byte> fill = data.Slice(offset, size);

                    for (int position = 0; position + unmappedPattern.Length <= fill.Length; position += unmappedPattern.Length)
                    {
                        unmappedPattern.CopyTo(fill[position..]);
                    }
                }
                else if (currentRange.Address != MemoryManager.PteUnmapped && range.Count > 1 && !_cpuMemory.IsRangeMapped(currentRange.Address, (ulong)size))
                {
                    if (_nx2UnmappedReads++ < 16)
                    {
                        Ryujinx.Common.Logging.Logger.Warning?.Print(Ryujinx.Common.Logging.LogClass.Gpu, $"GPU read of CPU memory not mapped by the process: 0x{currentRange.Address:X}+0x{size:x} (left zero)");
                    }
                }
                else if (currentRange.Address != MemoryManager.PteUnmapped)
                {
                    _cpuMemory.GetSpan(currentRange.Address, size, tracked).CopyTo(data.Slice(offset, size));
                }

                offset += size;
            }

            return data;
        }

        /// <summary>
        /// Gets a writable region from the application process.
        /// </summary>
        /// <param name="address">Start address of the range</param>
        /// <param name="size">Size in bytes to be range</param>
        /// <param name="tracked">True if write tracking is triggered on the span</param>
        /// <returns>A writable region with the data at the specified memory location</returns>
        public WritableRegion GetWritableRegion(ulong address, int size, bool tracked = false)
        {
            return _cpuMemory.GetWritableRegion(address, size, tracked);
        }

        /// <summary>
        /// Gets a writable region from GPU mapped memory.
        /// </summary>
        /// <param name="range">Range</param>
        /// <param name="tracked">True if write tracking is triggered on the span</param>
        /// <returns>A writable region with the data at the specified memory location</returns>
        public WritableRegion GetWritableRegion(MultiRange range, bool tracked = false)
        {
            if (range.Count == 1)
            {
                MemoryRange subrange = range.GetSubRange(0);

                return GetWritableRegion(subrange.Address, (int)subrange.Size, tracked);
            }
            else
            {
                MemoryOwner<byte> memoryOwner = MemoryOwner<byte>.Rent(checked((int)range.GetSize()));

                Span<byte> memorySpan = memoryOwner.Span;

                int offset = 0;
                for (int i = 0; i < range.Count; i++)
                {
                    MemoryRange currentRange = range.GetSubRange(i);
                    int size = (int)currentRange.Size;
                    if (currentRange.Address != MemoryManager.PteUnmapped)
                    {
                        GetSpan(currentRange.Address, size).CopyTo(memorySpan.Slice(offset, size));
                    }

                    offset += size;
                }

                return new WritableRegion(new MultiRangeWritableBlock(range, this), 0, memoryOwner, tracked);
            }
        }

        /// <summary>
        /// Reads data from the application process.
        /// </summary>
        /// <typeparam name="T">Type of the structure</typeparam>
        /// <param name="address">Address to read from</param>
        /// <returns>The data at the specified memory location</returns>
        public T Read<T>(ulong address) where T : unmanaged
        {
            return _cpuMemory.Read<T>(address);
        }

        /// <summary>
        /// Reads data from the application process, with write tracking.
        /// </summary>
        /// <typeparam name="T">Type of the structure</typeparam>
        /// <param name="address">Address to read from</param>
        /// <returns>The data at the specified memory location</returns>
        public T ReadTracked<T>(ulong address) where T : unmanaged
        {
            return _cpuMemory.ReadTracked<T>(address);
        }

        /// <summary>
        /// Writes data to the application process, triggering a precise memory tracking event.
        /// </summary>
        /// <param name="address">Address to write into</param>
        /// <param name="data">Data to be written</param>
        public void WriteTrackedResource(ulong address, ReadOnlySpan<byte> data)
        {
            _cpuMemory.SignalMemoryTracking(address, (ulong)data.Length, true, precise: true);
            _cpuMemory.WriteUntracked(address, data);
        }

        /// <summary>
        /// Writes data to the application process, triggering a precise memory tracking event.
        /// </summary>
        /// <param name="address">Address to write into</param>
        /// <param name="data">Data to be written</param>
        /// <param name="kind">Kind of the resource being written, which will not be signalled as CPU modified</param>
        public void WriteTrackedResource(ulong address, ReadOnlySpan<byte> data, ResourceKind kind)
        {
            _cpuMemory.SignalMemoryTracking(address, (ulong)data.Length, true, precise: true, exemptId: (int)kind);
            _cpuMemory.WriteUntracked(address, data);
        }

        /// <summary>
        /// Writes data to the application process.
        /// </summary>
        /// <param name="address">Address to write into</param>
        /// <param name="data">Data to be written</param>
        public void Write(ulong address, ReadOnlySpan<byte> data)
        {
            _cpuMemory.Write(address, data);
        }

        /// <summary>
        /// Writes data to the application process.
        /// </summary>
        /// <param name="range">Ranges of physical memory where the data is located</param>
        /// <param name="data">Data to be written</param>
        public void Write(MultiRange range, ReadOnlySpan<byte> data)
        {
            WriteImpl(range, data, _cpuMemory.Write);
        }

        /// <summary>
        /// Writes data to the application process, without any tracking.
        /// </summary>
        /// <param name="address">Address to write into</param>
        /// <param name="data">Data to be written</param>
        public void WriteUntracked(ulong address, ReadOnlySpan<byte> data)
        {
            _cpuMemory.WriteUntracked(address, data);
        }

        /// <summary>
        /// Writes data to the application process, without any tracking.
        /// </summary>
        /// <param name="range">Ranges of physical memory where the data is located</param>
        /// <param name="data">Data to be written</param>
        public void WriteUntracked(MultiRange range, ReadOnlySpan<byte> data)
        {
            WriteImpl(range, data, _cpuMemory.WriteUntracked);
        }

        /// <summary>
        /// Writes data to the application process, returning false if the data was not changed.
        /// This triggers read memory tracking, as a redundancy check would be useless if the data is not up to date.
        /// </summary>
        /// <remarks>The memory manager can return that memory has changed when it hasn't to avoid expensive data copies.</remarks>
        /// <param name="address">Address to write into</param>
        /// <param name="data">Data to be written</param>
        /// <returns>True if the data was changed, false otherwise</returns>
        public bool WriteWithRedundancyCheck(ulong address, ReadOnlySpan<byte> data)
        {
            return _cpuMemory.WriteWithRedundancyCheck(address, data);
        }

        private delegate void WriteCallback(ulong address, ReadOnlySpan<byte> data);

        /// <summary>
        /// Writes data to the application process, using the supplied callback method.
        /// </summary>
        /// <param name="range">Ranges of physical memory where the data is located</param>
        /// <param name="data">Data to be written</param>
        /// <param name="writeCallback">Callback method that will perform the write</param>
        private static void WriteImpl(MultiRange range, ReadOnlySpan<byte> data, WriteCallback writeCallback)
        {
            if (range.Count == 1)
            {
                MemoryRange singleRange = range.GetSubRange(0);
                if (singleRange.Address != MemoryManager.PteUnmapped)
                {
                    writeCallback(singleRange.Address, data);
                }
            }
            else
            {
                int offset = 0;

                for (int i = 0; i < range.Count; i++)
                {
                    MemoryRange currentRange = range.GetSubRange(i);
                    int size = (int)currentRange.Size;
                    if (currentRange.Address != MemoryManager.PteUnmapped)
                    {
                        writeCallback(currentRange.Address, data.Slice(offset, size));
                    }

                    offset += size;
                }
            }
        }

        /// <summary>
        /// Fills the specified memory region with a 32-bit integer value.
        /// </summary>
        /// <param name="address">CPU virtual address of the region</param>
        /// <param name="size">Size of the region</param>
        /// <param name="value">Value to fill the region with</param>
        /// <param name="kind">Kind of the resource being filled, which will not be signalled as CPU modified</param>
        public void FillTrackedResource(ulong address, ulong size, uint value, ResourceKind kind)
        {
            _cpuMemory.SignalMemoryTracking(address, size, write: true, precise: true, (int)kind);

            using WritableRegion region = _cpuMemory.GetWritableRegion(address, (int)size);

            MemoryMarshal.Cast<byte, uint>(region.Memory.Span).Fill(value);
        }

        /// <summary>
        /// Obtains a memory tracking handle for the given virtual region. This should be disposed when finished with.
        /// </summary>
        /// <param name="address">CPU virtual address of the region</param>
        /// <param name="size">Size of the region</param>
        /// <param name="kind">Kind of the resource being tracked</param>
        /// <param name="flags">Region flags</param>
        /// <returns>The memory tracking handle</returns>
        public RegionHandle BeginTracking(ulong address, ulong size, ResourceKind kind, RegionFlags flags = RegionFlags.None)
        {
            return _cpuMemory.BeginTracking(address, size, (int)kind, flags);
        }

        /// <summary>
        /// Obtains a memory tracking handle for the given virtual region. This should be disposed when finished with.
        /// </summary>
        /// <param name="range">Ranges of physical memory where the data is located</param>
        /// <param name="kind">Kind of the resource being tracked</param>
        /// <returns>The memory tracking handle</returns>
        public GpuRegionHandle BeginTracking(MultiRange range, ResourceKind kind)
        {
            RegionHandle[] cpuRegionHandles = new RegionHandle[range.Count];
            int count = 0;

            for (int i = 0; i < range.Count; i++)
            {
                MemoryRange currentRange = range.GetSubRange(i);
                if (currentRange.Address != MemoryManager.PteUnmapped)
                {
                    cpuRegionHandles[count++] = _cpuMemory.BeginTracking(currentRange.Address, currentRange.Size, (int)kind);
                }
            }

            if (count != range.Count)
            {
                Array.Resize(ref cpuRegionHandles, count);
            }

            return new GpuRegionHandle(cpuRegionHandles);
        }

        /// <summary>
        /// Obtains a memory tracking handle for the given virtual region, with a specified granularity. This should be disposed when finished with.
        /// </summary>
        /// <param name="address">CPU virtual address of the region</param>
        /// <param name="size">Size of the region</param>
        /// <param name="kind">Kind of the resource being tracked</param>
        /// <param name="flags">Region flags</param>
        /// <param name="handles">Handles to inherit state from or reuse</param>
        /// <param name="granularity">Desired granularity of write tracking</param>
        /// <returns>The memory tracking handle</returns>
        public MultiRegionHandle BeginGranularTracking(
            ulong address,
            ulong size,
            ResourceKind kind,
            RegionFlags flags = RegionFlags.None,
            IEnumerable<IRegionHandle> handles = null,
            ulong granularity = 4096)
        {
            return _cpuMemory.BeginGranularTracking(address, size, handles, granularity, (int)kind, flags);
        }

        /// <summary>
        /// Obtains a smart memory tracking handle for the given virtual region, with a specified granularity. This should be disposed when finished with.
        /// </summary>
        /// <param name="address">CPU virtual address of the region</param>
        /// <param name="size">Size of the region</param>
        /// <param name="kind">Kind of the resource being tracked</param>
        /// <param name="granularity">Desired granularity of write tracking</param>
        /// <returns>The memory tracking handle</returns>
        public SmartMultiRegionHandle BeginSmartGranularTracking(ulong address, ulong size, ResourceKind kind, ulong granularity = 4096)
        {
            return _cpuMemory.BeginSmartGranularTracking(address, size, granularity, (int)kind);
        }

        /// <summary>
        /// Checks if a given memory page is mapped.
        /// </summary>
        /// <param name="address">CPU virtual address of the page</param>
        /// <returns>True if mapped, false otherwise</returns>
        public bool IsMapped(ulong address)
        {
            return _cpuMemory.IsMapped(address);
        }

        /// <summary>
        /// Release our reference to the CPU memory manager.
        /// </summary>
        public void Dispose()
        {
            _context.DeferredActions.Enqueue(Destroy);
        }

        /// <summary>
        /// Performs disposal of the host GPU caches with resources mapped on this physical memory.
        /// This must only be called from the render thread.
        /// </summary>
        private void Destroy()
        {
            ShaderCache.Dispose();
            BufferCache.Dispose();
            TextureCache.Dispose();

            DecrementReferenceCount();
        }
    }
}
