using Ryujinx.Common.Logging;
using Ryujinx.Common.Memory;
using Ryujinx.Graphics.GAL;
using Ryujinx.Graphics.Gpu.Memory;
using Ryujinx.Graphics.Texture;
using Ryujinx.Memory.Range;
using System;
using System.Collections.Generic;

namespace Ryujinx.Graphics.Gpu.Image
{
    /// <summary>
    /// Switch 2 virtual textures, as Lichii's sparse tiled images (vk_texture_cache Image::BindTiles,
    /// texture_cache RefreshSparseTiles): arrays in unbacked sparse regions (gpu 214) of which the game maps
    /// only the resident tiles (gpu 216), such as Mario Kart World's 8192x8192x68 terrain arrays (1.4 to
    /// 6 GiB of guest address space). The host texture is a sparse image of the full size; only the rows of
    /// GOB blocks the game maps are uploaded, and only the host blocks they cover get memory. The rest
    /// reads as zero. They live outside the texture cache (whose 32-bit sizes such ranges overflow): the
    /// pool binds the <see cref="Texture"/> made here, and its memory synchronization uploads what the
    /// game mapped since.
    /// </summary>
    class Nx2VirtualTextures
    {
        private readonly struct Level
        {
            public readonly ulong Offset;
            public readonly ulong End;
            public readonly int Width;      // in format blocks
            public readonly int Height;     // in format blocks
            public readonly int GobBlocksInY;
            public readonly int BlocksPerRow;

            public int BlockRows => GobBlocksInY * 8;
            public ulong BlockBytes => (ulong)GobBlocksInY * 512;

            public Level(ulong offset, ulong end, int width, int height, int gobBlocksInY, int blocksPerRow)
            {
                Offset = offset;
                End = end;
                Width = width;
                Height = height;
                GobBlocksInY = gobBlocksInY;
                BlocksPerRow = blocksPerRow;
            }
        }

        private sealed class Entry
        {
            public Texture Texture;
            public MemoryManager MemoryManager;
            public ulong Address;
            public ulong LayerStride;
            public ulong LayerBytes;
            public int Layers;
            public Level[] Levels;
            public TextureInfo Info;
            public readonly List<(ulong Offset, ulong Size)> Pending = [];
            public ulong UploadedBytes;

            // The ranges uploaded, tracked: the game's CPU fills the tiles it maps (often after mapping
            // them, reusing a slot that held another tile), so a write there uploads the range again (as
            // Lichii's MarkRangeWritten).
            public readonly List<(ulong Offset, ulong Size, GpuRegionHandle Handle)> Tracked = [];
            public readonly Dictionary<ulong, (ulong Size, ulong Hash)> Hashes = [];
            public long Refreshes;
            public long HashChecks;
            public int Dumped;
        }

        private static readonly bool _disabled = Environment.GetEnvironmentVariable("RYUJINX_NX2_NO_SPARSE_TEXTURES") == "1";

        private readonly GpuContext _context;
        private readonly Dictionary<ulong, Entry> _entries = [];
        private readonly Dictionary<Texture, Entry> _byTexture = [];

        public Nx2VirtualTextures(GpuContext context)
        {
            _context = context;
        }

        /// <summary>
        /// Whether a virtual texture at that address gets a sparse host texture.
        /// </summary>
        public bool Handles(ulong address)
        {
            return !_disabled && _context.Capabilities.SupportsNx2SparseTextures && _context.Nx2InSparseRegion(address);
        }

        /// <summary>
        /// The texture for a virtual texture descriptor (its info for the full size, layer stride in bytes).
        /// </summary>
        public Texture GetOrCreate(MemoryManager memoryManager, PhysicalMemory physicalMemory, TextureInfo info, int layerStride)
        {
            lock (_entries)
            {
                if (_entries.TryGetValue(info.GpuAddress, out Entry existing) && existing.Info.Equals(info))
                {
                    return existing.Texture;
                }
            }

            if (info.IsLinear || info.Target is not (Target.Texture2D or Target.Texture2DArray) || info.FormatInfo.BytesPerPixel > 16)
            {
                return null;
            }

            int layers = info.GetLayers();
            Level[] levels = GetLevels(info, out ulong layerBytes);
            if (layerStride == 0 && layers > 1)
            {
                // The block-linear layer stride: the levels of a layer, aligned.
                layerStride = SizeCalculator.GetBlockLinearTextureSize(
                    info.Width,
                    info.Height,
                    1,
                    info.Levels,
                    2,
                    info.FormatInfo.BlockWidth,
                    info.FormatInfo.BlockHeight,
                    info.FormatInfo.BytesPerPixel,
                    info.GobBlocksInY,
                    1,
                    info.GobBlocksInTileX).LayerSize;
            }

            ulong stride = layers > 1 ? (ulong)layerStride : layerBytes;

            if (layers > 1 && stride < layerBytes)
            {
                Logger.Warning?.Print(LogClass.Gpu, $"Switch 2 virtual texture 0x{info.GpuAddress:X}: layer stride 0x{stride:x} under its levels' 0x{layerBytes:x}");

                return null;
            }

            TextureCreateInfo createInfo = TextureCache.GetCreateInfo(info, _context.Capabilities, 1f) with { Nx2Sparse = true };

            if (createInfo.Format != info.FormatInfo.Format)
            {
                // Converted on upload (ASTC the host can't sample): not with partial uploads.
                return null;
            }

            ITexture host = _context.Renderer.CreateTexture(createInfo);

            Texture texture = new(_context, physicalMemory, info, new SizeInfo(0x1000), new MultiRange(MemoryManager.PteUnmapped, 0x1000), TextureScaleMode.Blacklisted);

            texture.Nx2SetSparseHost(host);
            texture.IncrementReferenceCount();

            Entry entry = new()
            {
                Texture = texture,
                MemoryManager = memoryManager,
                Address = info.GpuAddress,
                LayerStride = stride,
                LayerBytes = layerBytes,
                Layers = layers,
                Levels = levels,
                Info = info,
            };

            // Everything mapped now.
            ulong span = stride * (ulong)(layers - 1) + layerBytes;
            ulong mapped = QueueMapped(entry);

            Logger.Info?.Print(LogClass.Gpu, $"Switch 2 virtual texture 0x{info.GpuAddress:X}: sparse {info.Width}x{info.Height}x{layers} levels {info.Levels} {info.FormatInfo.Format}, " +
                $"layer stride 0x{stride:x}, 0x{mapped:x} of 0x{span:x} bytes mapped");

            lock (_entries)
            {
                if (_entries.Remove(info.GpuAddress, out Entry replaced))
                {
                    _byTexture.Remove(replaced.Texture);
                }

                _entries[info.GpuAddress] = entry;
                _byTexture[texture] = entry;
            }

            return texture;
        }

        /// <summary>
        /// The game mapped memory at a range of a sparse region (gpu 216): queues the data of the virtual
        /// textures there for upload.
        /// </summary>
        public void Mapped(ulong address, ulong size)
        {
            lock (_entries)
            {
                foreach (Entry entry in _entries.Values)
                {
                    ulong end = entry.Address + entry.LayerStride * (ulong)(entry.Layers - 1) + entry.LayerBytes;
                    ulong begin = Math.Max(address, entry.Address);
                    ulong last = Math.Min(address + size, end);

                    if (begin < last)
                    {
                        lock (entry.Pending)
                        {
                            entry.Pending.Add((begin - entry.Address, last - begin));
                        }
                    }
                }
            }
        }

        /// <summary>
        /// Uploads what was mapped since the last call (on the GPU thread, when the texture is bound).
        /// </summary>
        public void Refresh(Texture texture)
        {
            Entry entry;

            lock (_entries)
            {
                if (!_byTexture.TryGetValue(texture, out entry))
                {
                    return;
                }
            }

            // RYUJINX_NX2_VT_HASH=1: every 2000 refreshes, logs the uploaded ranges whose memory changed since.
            if (_hashCheck && ++entry.HashChecks % 2000 == 0)
            {
                int changed = 0;
                ulong changedBytes = 0;

                foreach ((ulong offset, (ulong size, ulong hash)) in entry.Hashes)
                {
                    if (Hash(entry, offset, size) != hash)
                    {
                        changed++;
                        changedBytes += size;
                    }
                }

                Logger.Info?.Print(LogClass.Gpu, $"Switch 2 virtual texture 0x{entry.Address:X}: {changed} of {entry.Hashes.Count} uploaded ranges changed in memory (0x{changedBytes:x} bytes), {entry.Tracked.Count} tracked");
            }

            // RYUJINX_NX2_VT_REFRESH=<n>: every n refreshes, upload everything mapped again (to see whether the
            // game writes its tiles after mapping them).
            if (_refreshEvery > 0 && ++entry.Refreshes % _refreshEvery == 0)
            {
                QueueMapped(entry);
            }

            (ulong Offset, ulong Size)[] pending;

            lock (entry.Pending)
            {
                foreach ((ulong offset, ulong size, GpuRegionHandle handle) in entry.Tracked)
                {
                    if (handle.Dirty)
                    {
                        entry.Pending.Add((offset, size));
                    }
                }

                if (entry.Pending.Count == 0)
                {
                    return;
                }

                pending = entry.Pending.ToArray();
                entry.Pending.Clear();
            }

            foreach ((ulong offset, ulong size) in pending)
            {
                Track(entry, offset, size);
                Upload(entry, offset, size);

                if (_hashCheck)
                {
                    entry.Hashes[offset] = (size, Hash(entry, offset, size));
                }

                if (_rawDump != null && entry.Dumped++ < 4)
                {
                    System.IO.Directory.CreateDirectory(_rawDump);
                    System.IO.File.WriteAllBytes(System.IO.Path.Combine(_rawDump, $"vt_{entry.Address:x}_{offset:x}_{size:x}.bin"),
                        entry.MemoryManager.Physical.GetSpan(entry.MemoryManager.GetPhysicalRegions(entry.Address + offset, size)).ToArray());
                }
            }
        }

        private static readonly int _refreshEvery = int.TryParse(Environment.GetEnvironmentVariable("RYUJINX_NX2_VT_REFRESH"), out int every) ? every : 0;

        private static ulong QueueMapped(Entry entry)
        {
            ulong span = entry.LayerStride * (ulong)(entry.Layers - 1) + entry.LayerBytes;
            MultiRange ranges = entry.MemoryManager.GetPhysicalRegions(entry.Address, span);
            ulong offset = 0;
            ulong mapped = 0;

            lock (entry.Pending)
            {
                for (int index = 0; index < ranges.Count; index++)
                {
                    MemoryRange range = ranges.GetSubRange(index);

                    if (range.Address != MemoryManager.PteUnmapped)
                    {
                        entry.Pending.Add((offset, range.Size));
                        mapped += range.Size;
                    }

                    offset += range.Size;
                }
            }

            return mapped;
        }

        private static int _writesSeen;
        // RYUJINX_NX2_VT_DUMP=<dir>: the raw guest bytes of the first ranges each virtual texture uploads.
        private static readonly string _rawDump = Environment.GetEnvironmentVariable("RYUJINX_NX2_VT_DUMP");
        private static readonly bool _hashCheck = Environment.GetEnvironmentVariable("RYUJINX_NX2_VT_HASH") == "1";

        private static ulong Hash(Entry entry, ulong offset, ulong size)
        {
            ReadOnlySpan<byte> data = entry.MemoryManager.Physical.GetSpan(entry.MemoryManager.GetPhysicalRegions(entry.Address + offset, size));
            ulong hash = 14695981039346656037UL;

            for (int index = 0; index < data.Length; index += 64)
            {
                hash = (hash ^ System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(data[index..])) * 1099511628211UL;
            }

            return hash;
        }

        // Tracks a range about to be uploaded, replacing the tracking of the ranges it overlaps.
        private static void Track(Entry entry, ulong offset, ulong size)
        {
            lock (entry.Pending)
            {
                for (int index = entry.Tracked.Count - 1; index >= 0; index--)
                {
                    (ulong trackedOffset, ulong trackedSize, GpuRegionHandle handle) = entry.Tracked[index];

                    if (trackedOffset < offset + size && offset < trackedOffset + trackedSize)
                    {
                        if (handle.Dirty && _writesSeen++ < 16)
                        {
                            Logger.Info?.Print(LogClass.Gpu, $"Switch 2 virtual texture 0x{entry.Address:X}: CPU wrote its tiles at +0x{trackedOffset:x} (0x{trackedSize:x} bytes), uploaded again");
                        }

                        handle.Dispose();
                        entry.Tracked.RemoveAt(index);
                    }
                }

                MultiRange range = entry.MemoryManager.GetPhysicalRegions(entry.Address + offset, size);

                if (range.Count == 1 && range.GetSubRange(0).Address == MemoryManager.PteUnmapped)
                {
                    return;
                }

                GpuRegionHandle tracking = entry.MemoryManager.Physical.BeginTracking(range, ResourceKind.Texture);

                tracking.Reprotect();
                entry.Tracked.Add((offset, size, tracking));
            }
        }

        private static Level[] GetLevels(TextureInfo info, out ulong layerBytes)
        {
            int blockWidth = info.FormatInfo.BlockWidth;
            int blockHeight = info.FormatInfo.BlockHeight;
            int bpp = info.FormatInfo.BytesPerPixel;

            SizeInfo sizeInfo = SizeCalculator.GetBlockLinearTextureSize(
                info.Width,
                info.Height,
                1,
                info.Levels,
                1,
                blockWidth,
                blockHeight,
                bpp,
                info.GobBlocksInY,
                1,
                info.GobBlocksInTileX);

            layerBytes = (ulong)sizeInfo.TotalSize;

            Level[] levels = new Level[info.Levels];
            int mipGobBlocksInY = info.GobBlocksInY;

            for (int level = 0; level < info.Levels; level++)
            {
                int width = Ryujinx.Common.BitUtils.DivRoundUp(Math.Max(info.Width >> level, 1), blockWidth);
                int height = Ryujinx.Common.BitUtils.DivRoundUp(Math.Max(info.Height >> level, 1), blockHeight);

                while (height <= (mipGobBlocksInY >> 1) * 8 && mipGobBlocksInY != 1)
                {
                    mipGobBlocksInY >>= 1;
                }

                ulong offset = (ulong)sizeInfo.GetMipOffset(level);
                ulong end = level + 1 < info.Levels ? (ulong)sizeInfo.GetMipOffset(level + 1) : layerBytes;
                int blocksPerRow = Ryujinx.Common.BitUtils.DivRoundUp(width * bpp, 64);

                levels[level] = new Level(offset, end, width, height, mipGobBlocksInY, blocksPerRow);
            }

            return levels;
        }

        private void Upload(Entry entry, ulong offset, ulong size)
        {
            TextureInfo info = entry.Info;
            ulong end = offset + size;
            int first = (int)(offset / entry.LayerStride);
            int last = (int)Math.Min((end - 1) / entry.LayerStride, (ulong)entry.Layers - 1);

            for (int layer = first; layer <= last; layer++)
            {
                ulong layerBase = (ulong)layer * entry.LayerStride;
                ulong begin = Math.Max(offset, layerBase) - layerBase;
                ulong stop = Math.Min(end, layerBase + entry.LayerBytes) - layerBase;

                if (begin >= stop || begin >= entry.LayerBytes)
                {
                    continue;
                }

                for (int index = 0; index < entry.Levels.Length; index++)
                {
                    Level level = entry.Levels[index];
                    ulong levelBegin = Math.Max(begin, level.Offset);
                    ulong levelEnd = Math.Min(stop, level.End);

                    if (levelBegin >= levelEnd)
                    {
                        continue;
                    }

                    ulong firstBlock = (levelBegin - level.Offset) / level.BlockBytes;
                    ulong lastBlock = (levelEnd - 1 - level.Offset) / level.BlockBytes;

                    for (ulong row = firstBlock / (ulong)level.BlocksPerRow; row <= lastBlock / (ulong)level.BlocksPerRow; row++)
                    {
                        ulong rowStart = row * (ulong)level.BlocksPerRow;
                        int low = (int)(Math.Max(firstBlock, rowStart) - rowStart);
                        int high = (int)(Math.Min(lastBlock, rowStart + (ulong)level.BlocksPerRow - 1) - rowStart);

                        UploadBlockRow(entry, layer, index, level, (int)row, low, high);
                    }
                }
            }
        }

        // One row of GOB blocks, from block `low` to `high`: laid out like an image of that width on its own.
        private void UploadBlockRow(Entry entry, int layer, int levelIndex, Level level, int row, int low, int high)
        {
            TextureInfo info = entry.Info;
            int bpp = info.FormatInfo.BytesPerPixel;
            int blockTexels = 64 / bpp; // format blocks across a GOB
            int x0 = low * blockTexels;
            int y0 = row * level.BlockRows;

            if (x0 >= level.Width || y0 >= level.Height)
            {
                return;
            }

            int x1 = Math.Min((high + 1) * blockTexels, level.Width);
            int y1 = Math.Min(y0 + level.BlockRows, level.Height);
            int width = x1 - x0;
            int rows = y1 - y0;

            ulong address = entry.Address + (ulong)layer * entry.LayerStride + level.Offset +
                (ulong)row * (ulong)level.BlocksPerRow * level.BlockBytes + (ulong)low * level.BlockBytes;
            ulong bytes = (ulong)(high - low + 1) * level.BlockBytes;

            MultiRange range = entry.MemoryManager.GetPhysicalRegions(address, bytes);
            ReadOnlySpan<byte> swizzled = entry.MemoryManager.Physical.GetSpan(range, false, info.FormatInfo.Format.IsAstc ? PhysicalMemory.AstcTransparentBlock : null);

            int blockWidth = info.FormatInfo.BlockWidth;
            int blockHeight = info.FormatInfo.BlockHeight;
            int texelWidth = width * blockWidth;
            int texelHeight = level.BlockRows * blockHeight;

            SizeInfo sizeInfo = SizeCalculator.GetBlockLinearTextureSize(texelWidth, texelHeight, 1, 1, 1, blockWidth, blockHeight, bpp, level.GobBlocksInY, 1, 1);

            if (swizzled.Length < sizeInfo.TotalSize)
            {
                return;
            }

            if (info.FormatInfo.Format.IsAstc)
            {
                // Zero bytes are a reserved ASTC block mode, decoded as the error colour (magenta). Tiles the game
                // maps but never fills hold zeros, which read as zero on the console (Lichii leaves such blocks
                // without memory): they become empty void-extent blocks.
                byte[] blocks = swizzled.ToArray();

                for (int offset = 0; offset + 16 <= blocks.Length; offset += 16)
                {
                    if (System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(blocks.AsSpan(offset)) == 0 &&
                        System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(blocks.AsSpan(offset + 8)) == 0)
                    {
                        PhysicalMemory.AstcTransparentBlock.CopyTo(blocks, offset);
                    }
                }

                swizzled = blocks;
            }

            using MemoryOwner<byte> linear = LayoutConverter.ConvertBlockLinearToLinear(
                texelWidth,
                texelHeight,
                1,
                1,
                1,
                1,
                blockWidth,
                blockHeight,
                bpp,
                level.GobBlocksInY,
                1,
                1,
                sizeInfo,
                swizzled);

            int stride = Ryujinx.Common.BitUtils.AlignUp(width * bpp, LayoutConverter.HostStrideAlignment);
            int packedStride = width * bpp;
            MemoryOwner<byte> data = MemoryOwner<byte>.Rent(packedStride * rows);

            for (int y = 0; y < rows; y++)
            {
                linear.Span.Slice(y * stride, packedStride).CopyTo(data.Span[(y * packedStride)..]);
            }

            int levelTexelWidth = Math.Max(info.Width >> levelIndex, 1);
            int levelTexelHeight = Math.Max(info.Height >> levelIndex, 1);
            int regionX = x0 * blockWidth;
            int regionY = y0 * blockHeight;
            int regionWidth = Math.Min(texelWidth, levelTexelWidth - regionX);
            int regionHeight = Math.Min(rows * blockHeight, levelTexelHeight - regionY);

            entry.Texture.HostTexture.SetData(data, layer, levelIndex, new Rectangle<int>(regionX, regionY, regionWidth, regionHeight));
            entry.UploadedBytes += (ulong)(packedStride * rows);
        }
    }
}
