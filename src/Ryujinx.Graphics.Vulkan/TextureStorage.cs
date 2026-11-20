using Ryujinx.Common;
using Ryujinx.Graphics.GAL;
using Silk.NET.Vulkan;
using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.CompilerServices;
using Format = Ryujinx.Graphics.GAL.Format;
using VkBuffer = Silk.NET.Vulkan.Buffer;
using VkFormat = Silk.NET.Vulkan.Format;

namespace Ryujinx.Graphics.Vulkan
{
    class TextureStorage : IDisposable
    {
        private struct TextureSliceInfo
        {
            public int BindCount;
        }

        private const MemoryPropertyFlags DefaultImageMemoryFlags =
            MemoryPropertyFlags.DeviceLocalBit;

        private const ImageUsageFlags DefaultUsageFlags =
            ImageUsageFlags.SampledBit |
            ImageUsageFlags.TransferSrcBit |
            ImageUsageFlags.TransferDstBit;

        public const AccessFlags DefaultAccessMask =
            AccessFlags.ShaderReadBit |
            AccessFlags.ShaderWriteBit |
            AccessFlags.ColorAttachmentReadBit |
            AccessFlags.ColorAttachmentWriteBit |
            AccessFlags.DepthStencilAttachmentReadBit |
            AccessFlags.DepthStencilAttachmentWriteBit |
            AccessFlags.TransferReadBit |
            AccessFlags.TransferWriteBit;

        private readonly VulkanRenderer _gd;

        private readonly Device _device;

        private TextureCreateInfo _info;

        public TextureCreateInfo Info => _info;

        public bool Disposed { get; private set; }

        private readonly Image _image;
        private readonly Auto<DisposableImage> _imageAuto;
        private readonly Auto<MemoryAllocation> _allocationAuto;
        private readonly int _depthOrLayers;
        private Auto<MemoryAllocation> _foreignAllocationAuto;

        private Dictionary<Format, TextureStorage> _aliasedStorages;

        private AccessFlags _lastModificationAccess;
        private PipelineStageFlags _lastModificationStage;
        private AccessFlags _lastReadAccess;
        private PipelineStageFlags _lastReadStage;

        private int _viewsCount;
        private readonly ulong _size;

        private int _bindCount;
        private readonly TextureSliceInfo[] _slices;

        public VkFormat VkFormat { get; }

        public ImageUsageFlags UsageFlags { get; }

        public unsafe TextureStorage(
            VulkanRenderer gd,
            Device device,
            TextureCreateInfo info,
            Auto<MemoryAllocation> foreignAllocation = null)
        {
            _gd = gd;
            _device = device;
            _info = info;

            bool isMsImageStorageSupported = gd.Capabilities.SupportsShaderStorageImageMultisample || !info.Target.IsMultisample;

            VkFormat format = _gd.FormatCapabilities.ConvertToVkFormat(info.Format, isMsImageStorageSupported);
            uint levels = (uint)info.Levels;
            uint layers = (uint)info.GetLayers();
            uint depth = (uint)(info.Target == Target.Texture3D ? info.Depth : 1);

            VkFormat = format;
            _depthOrLayers = info.GetDepthOrLayers();

            ImageType type = info.Target.Convert();

            Extent3D extent = new((uint)info.Width, (uint)info.Height, depth);

            SampleCountFlags sampleCountFlags = ConvertToSampleCountFlags(gd.Capabilities.SupportedSampleCounts, (uint)info.Samples);

            ImageUsageFlags usage = GetImageUsage(info.Format, gd.Capabilities, isMsImageStorageSupported);
            UsageFlags = usage;

            ImageCreateFlags flags = ImageCreateFlags.CreateMutableFormatBit | ImageCreateFlags.CreateExtendedUsageBit;

            // This flag causes mipmapped texture arrays to break on AMD GCN, so for that copy dependencies are forced for aliasing as cube.
            bool isCube = info.Target is Target.Cubemap or Target.CubemapArray;
            bool cubeCompatible = gd.IsAmdGcn ? isCube : (info.Width == info.Height && layers >= 6);

            if (type == ImageType.Type2D && cubeCompatible)
            {
                flags |= ImageCreateFlags.CreateCubeCompatibleBit;
            }

            if (type == ImageType.Type3D && !gd.Capabilities.PortabilitySubset.HasFlag(PortabilitySubsetFlags.No3DImageView))
            {
                flags |= ImageCreateFlags.Create2DArrayCompatibleBit;
            }

            bool sparse = info.Nx2Sparse && foreignAllocation == null;

            if (sparse)
            {
                flags |= ImageCreateFlags.CreateSparseBindingBit | ImageCreateFlags.CreateSparseResidencyBit;
            }

            ImageCreateInfo imageCreateInfo = new()
            {
                SType = StructureType.ImageCreateInfo,
                ImageType = type,
                Format = format,
                Extent = extent,
                MipLevels = levels,
                ArrayLayers = layers,
                Samples = sampleCountFlags,
                Tiling = ImageTiling.Optimal,
                Usage = usage,
                SharingMode = SharingMode.Exclusive,
                InitialLayout = ImageLayout.Undefined,
                Flags = flags,
            };

            gd.Api.CreateImage(device, in imageCreateInfo, null, out _image).ThrowOnError();

            if (sparse)
            {
                Nx2InitSparse(levels);

                _imageAuto = new Auto<DisposableImage>(new DisposableImage(_gd.Api, device, _image));

                InitialTransition(ImageLayout.Undefined, ImageLayout.General);
            }
            else if (foreignAllocation == null)
            {
                gd.Api.GetImageMemoryRequirements(device, _image, out MemoryRequirements requirements);
                MemoryAllocation allocation = gd.MemoryAllocator.AllocateDeviceMemory(requirements, DefaultImageMemoryFlags);

                if (allocation.Memory.Handle == 0UL)
                {
                    gd.Api.DestroyImage(device, _image, null);
                    throw new Exception("Image initialization failed.");
                }

                _size = requirements.Size;

                gd.Api.BindImageMemory(device, _image, allocation.Memory, allocation.Offset).ThrowOnError();

                _allocationAuto = new Auto<MemoryAllocation>(allocation);
                _imageAuto = new Auto<DisposableImage>(new DisposableImage(_gd.Api, device, _image), null, _allocationAuto);

                InitialTransition(ImageLayout.Undefined, ImageLayout.General);
            }
            else
            {
                _foreignAllocationAuto = foreignAllocation;
                foreignAllocation.IncrementReferenceCount();
                MemoryAllocation allocation = foreignAllocation.GetUnsafe();

                gd.Api.BindImageMemory(device, _image, allocation.Memory, allocation.Offset).ThrowOnError();

                _imageAuto = new Auto<DisposableImage>(new DisposableImage(_gd.Api, device, _image));

                InitialTransition(ImageLayout.Undefined, ImageLayout.General);
            }

            _slices = new TextureSliceInfo[levels * _depthOrLayers];
        }

        public TextureStorage CreateAliasedColorForDepthStorageUnsafe(Format format)
        {
            Format colorFormat = format switch
            {
                Format.S8Uint => Format.R8Unorm,
                Format.D16Unorm => Format.R16Unorm,
                Format.D24UnormS8Uint or Format.S8UintD24Unorm or Format.X8UintD24Unorm => Format.R8G8B8A8Unorm,
                Format.D32Float => Format.R32Float,
                Format.D32FloatS8Uint => Format.R32G32Float,
                _ => throw new ArgumentException($"\"{format}\" is not a supported depth or stencil format."),
            };

            return CreateAliasedStorageUnsafe(colorFormat);
        }

        public TextureStorage CreateAliasedStorageUnsafe(Format format)
        {
            if (_aliasedStorages == null || !_aliasedStorages.TryGetValue(format, out TextureStorage storage))
            {
                _aliasedStorages ??= new Dictionary<Format, TextureStorage>();

                TextureCreateInfo info = NewCreateInfoWith(ref _info, format, _info.BytesPerPixel);

                storage = new TextureStorage(_gd, _device, info, _allocationAuto);

                _aliasedStorages.Add(format, storage);
            }

            return storage;
        }

        public static TextureCreateInfo NewCreateInfoWith(ref TextureCreateInfo info, Format format, int bytesPerPixel)
        {
            return NewCreateInfoWith(ref info, format, bytesPerPixel, info.Width, info.Height);
        }

        public static TextureCreateInfo NewCreateInfoWith(
            ref TextureCreateInfo info,
            Format format,
            int bytesPerPixel,
            int width,
            int height)
        {
            return new TextureCreateInfo(
                width,
                height,
                info.Depth,
                info.Levels,
                info.Samples,
                info.BlockWidth,
                info.BlockHeight,
                bytesPerPixel,
                format,
                info.DepthStencilMode,
                info.Target,
                info.SwizzleR,
                info.SwizzleG,
                info.SwizzleB,
                info.SwizzleA);
        }

        public Auto<DisposableImage> GetImage()
        {
            return _imageAuto;
        }

        public Image GetImageForViewCreation()
        {
            return _image;
        }

        public bool HasCommandBufferDependency(CommandBufferScoped cbs)
        {
            if (_foreignAllocationAuto != null)
            {
                return _foreignAllocationAuto.HasCommandBufferDependency(cbs);
            }
            else if (_allocationAuto != null)
            {
                return _allocationAuto.HasCommandBufferDependency(cbs);
            }

            return false;
        }

        private unsafe void InitialTransition(ImageLayout srcLayout, ImageLayout dstLayout)
        {
            CommandBufferScoped cbs;
            bool useTempCbs = !_gd.CommandBufferPool.OwnedByCurrentThread;

            if (useTempCbs)
            {
                cbs = _gd.BackgroundResources.Get().GetPool().Rent();
            }
            else
            {
                if (_gd.PipelineInternal != null)
                {
                    cbs = _gd.PipelineInternal.GetPreloadCommandBuffer();
                }
                else
                {
                    cbs = _gd.CommandBufferPool.Rent();
                    useTempCbs = true;
                }
            }

            ImageAspectFlags aspectFlags = _info.Format.ConvertAspectFlags();

            ImageSubresourceRange subresourceRange = new(aspectFlags, 0, (uint)_info.Levels, 0, (uint)_info.GetLayers());

            ImageMemoryBarrier barrier = new()
            {
                SType = StructureType.ImageMemoryBarrier,
                SrcAccessMask = 0,
                DstAccessMask = DefaultAccessMask,
                OldLayout = srcLayout,
                NewLayout = dstLayout,
                SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
                DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
                Image = _imageAuto.Get(cbs).Value,
                SubresourceRange = subresourceRange,
            };

            _gd.Api.CmdPipelineBarrier(
                cbs.CommandBuffer,
                PipelineStageFlags.TopOfPipeBit,
                PipelineStageFlags.AllCommandsBit,
                0,
                0,
                null,
                0,
                null,
                1,
                in barrier);

            if (useTempCbs)
            {
                cbs.Dispose();
            }
        }

        public static ImageUsageFlags GetImageUsage(Format format, in HardwareCapabilities capabilities, bool isMsImageStorageSupported)
        {
            ImageUsageFlags usage = DefaultUsageFlags;

            if (format.IsDepthOrStencil)
            {
                usage |= ImageUsageFlags.DepthStencilAttachmentBit;
            }
            else if (format.IsRtColorCompatible)
            {
                usage |= ImageUsageFlags.ColorAttachmentBit;
            }

            if (format.IsImageCompatible && isMsImageStorageSupported)
            {
                usage |= ImageUsageFlags.StorageBit;
            }

            if (capabilities.SupportsAttachmentFeedbackLoop &&
                (usage & (ImageUsageFlags.DepthStencilAttachmentBit | ImageUsageFlags.ColorAttachmentBit)) != 0)
            {
                usage |= ImageUsageFlags.AttachmentFeedbackLoopBitExt;
            }

            return usage;
        }

        public static SampleCountFlags ConvertToSampleCountFlags(SampleCountFlags supportedSampleCounts, uint samples)
        {
            if (samples is 0 or > ((uint)SampleCountFlags.Count64Bit))
            {
                return SampleCountFlags.Count1Bit;
            }

            // Round up to the nearest power of two.
            SampleCountFlags converted = (SampleCountFlags)(1u << (31 - BitOperations.LeadingZeroCount(samples)));

            // Pick nearest sample count that the host actually supports.
            while (converted != SampleCountFlags.Count1Bit && (converted & supportedSampleCounts) == 0)
            {
                converted = (SampleCountFlags)((uint)converted >> 1);
            }

            return converted;
        }

        public TextureView CreateView(TextureCreateInfo info, int firstLayer, int firstLevel)
        {
            return new TextureView(_gd, _device, info, this, firstLayer, firstLevel);
        }

        public void CopyFromOrToBuffer(
            CommandBuffer commandBuffer,
            VkBuffer buffer,
            Image image,
            int size,
            bool to,
            int x,
            int y,
            int dstLayer,
            int dstLevel,
            int dstLayers,
            int dstLevels,
            bool singleSlice,
            ImageAspectFlags aspectFlags,
            bool forFlush)
        {
            bool is3D = Info.Target == Target.Texture3D;
            int width = Info.Width;
            int height = Info.Height;
            int depth = is3D && !singleSlice ? Info.Depth : 1;
            int layer = is3D ? 0 : dstLayer;
            int layers = dstLayers;
            int levels = dstLevels;

            int offset = 0;

            for (int level = 0; level < levels; level++)
            {
                int mipSize = Info.GetMipSize(level);

                if (forFlush)
                {
                    mipSize = GetBufferDataLength(mipSize);
                }

                int endOffset = offset + mipSize;

                if ((uint)endOffset > (uint)size)
                {
                    break;
                }

                int rowLength = (Info.GetMipStride(level) / Info.BytesPerPixel) * Info.BlockWidth;

                ImageSubresourceLayers sl = new(
                    aspectFlags,
                    (uint)(dstLevel + level),
                    (uint)layer,
                    (uint)layers);

                Extent3D extent = new((uint)width, (uint)height, (uint)depth);

                int z = is3D ? dstLayer : 0;

                BufferImageCopy region = new(
                    (ulong)offset,
                    (uint)BitUtils.AlignUp(rowLength, Info.BlockWidth),
                    (uint)BitUtils.AlignUp(height, Info.BlockHeight),
                    sl,
                    new Offset3D(x, y, z),
                    extent);

                if (to)
                {
                    _gd.Api.CmdCopyImageToBuffer(commandBuffer, image, ImageLayout.General, buffer, 1, in region);
                }
                else
                {
                    _gd.Api.CmdCopyBufferToImage(commandBuffer, buffer, image, ImageLayout.General, 1, in region);
                }

                offset += mipSize;

                width = Math.Max(1, width >> 1);
                height = Math.Max(1, height >> 1);

                if (Info.Target == Target.Texture3D)
                {
                    depth = Math.Max(1, depth >> 1);
                }
            }
        }

        private int GetBufferDataLength(int length)
        {
            if (NeedsD24S8Conversion())
            {
                return length * 2;
            }

            return length;
        }

        private bool NeedsD24S8Conversion()
        {
            return FormatCapabilities.IsD24S8(Info.Format) && VkFormat == VkFormat.D32SfloatS8Uint;
        }

        public void AddStoreOpUsage(bool depthStencil)
        {
            _lastModificationStage = depthStencil ?
                PipelineStageFlags.LateFragmentTestsBit :
                PipelineStageFlags.ColorAttachmentOutputBit;

            _lastModificationAccess = depthStencil ?
                AccessFlags.DepthStencilAttachmentWriteBit :
                AccessFlags.ColorAttachmentWriteBit;
        }

        public void QueueLoadOpBarrier(CommandBufferScoped cbs, bool depthStencil)
        {
            PipelineStageFlags srcStageFlags = _lastReadStage | _lastModificationStage;
            PipelineStageFlags dstStageFlags = depthStencil ?
                PipelineStageFlags.EarlyFragmentTestsBit | PipelineStageFlags.LateFragmentTestsBit :
                PipelineStageFlags.ColorAttachmentOutputBit;

            AccessFlags srcAccessFlags = _lastModificationAccess | _lastReadAccess;
            AccessFlags dstAccessFlags = depthStencil ?
                AccessFlags.DepthStencilAttachmentWriteBit | AccessFlags.DepthStencilAttachmentReadBit :
                AccessFlags.ColorAttachmentWriteBit | AccessFlags.ColorAttachmentReadBit;

            if (srcAccessFlags != AccessFlags.None)
            {
                ImageAspectFlags aspectFlags = Info.Format.ConvertAspectFlags();
                ImageMemoryBarrier barrier = TextureView.GetImageBarrier(
                    _imageAuto.Get(cbs).Value,
                    srcAccessFlags,
                    dstAccessFlags,
                    aspectFlags,
                    0,
                    0,
                    _info.GetLayers(),
                    _info.Levels);

                _gd.Barriers.QueueBarrier(barrier, this, srcStageFlags, dstStageFlags);

                _lastReadStage = PipelineStageFlags.None;
                _lastReadAccess = AccessFlags.None;
            }

            _lastModificationStage = depthStencil ?
                PipelineStageFlags.LateFragmentTestsBit :
                PipelineStageFlags.ColorAttachmentOutputBit;

            _lastModificationAccess = depthStencil ?
                AccessFlags.DepthStencilAttachmentWriteBit :
                AccessFlags.ColorAttachmentWriteBit;
        }

        public void QueueWriteToReadBarrier(CommandBufferScoped cbs, AccessFlags dstAccessFlags, PipelineStageFlags dstStageFlags)
        {
            _lastReadAccess |= dstAccessFlags;
            _lastReadStage |= dstStageFlags;

            if (_lastModificationAccess != AccessFlags.None)
            {
                ImageAspectFlags aspectFlags = Info.Format.ConvertAspectFlags();
                ImageMemoryBarrier barrier = TextureView.GetImageBarrier(
                    _imageAuto.Get(cbs).Value,
                    _lastModificationAccess,
                    dstAccessFlags,
                    aspectFlags,
                    0,
                    0,
                    _info.GetLayers(),
                    _info.Levels);

                _gd.Barriers.QueueBarrier(barrier, this, _lastModificationStage, dstStageFlags);

                _lastModificationAccess = AccessFlags.None;
            }
        }

        public void AddBinding(TextureView view)
        {
            // Assumes a view only has a first level.

            int index = view.FirstLevel * _depthOrLayers + view.FirstLayer;
            int layers = view.Layers;

            for (int i = 0; i < layers; i++)
            {
                ref TextureSliceInfo info = ref _slices[index++];

                info.BindCount++;
            }

            _bindCount++;
        }

        public void ClearBindings()
        {
            if (_bindCount != 0)
            {
                Array.Clear(_slices, 0, _slices.Length);

                _bindCount = 0;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool IsBound(TextureView view)
        {
            if (_bindCount != 0)
            {
                int index = view.FirstLevel * _depthOrLayers + view.FirstLayer;
                int layers = view.Layers;

                for (int i = 0; i < layers; i++)
                {
                    ref TextureSliceInfo info = ref _slices[index++];

                    if (info.BindCount != 0)
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        public void IncrementViewsCount()
        {
            _viewsCount++;
        }

        public void DecrementViewsCount()
        {
            if (--_viewsCount == 0)
            {
                _gd.PipelineInternal?.FlushCommandsIfWeightExceeding(_imageAuto, _size);

                Dispose();
            }
        }

        // Switch 2 virtual textures (TextureCreateInfo.Nx2Sparse): a sparse image whose blocks get memory as
        // data is uploaded to them, as Lichii's Image::BindTiles. Blocks never uploaded read as zero.
        private Dictionary<(int Layer, int Level, int X, int Y), MemoryAllocation> _nx2Tiles;
        private Dictionary<int, MemoryAllocation> _nx2Tails;
        private SparseImageMemoryRequirements _nx2Sparse;
        private MemoryRequirements _nx2Requirements;
        private int _nx2TailLevel;

        public bool IsNx2Sparse => _nx2Tiles != null;

        public ulong Nx2BoundBytes { get; private set; }

        private unsafe void Nx2InitSparse(uint levels)
        {
            uint count = 0;

            _gd.Api.GetImageSparseMemoryRequirements(_device, _image, &count, null);

            SparseImageMemoryRequirements[] requirements = new SparseImageMemoryRequirements[count];

            fixed (SparseImageMemoryRequirements* pRequirements = requirements)
            {
                _gd.Api.GetImageSparseMemoryRequirements(_device, _image, &count, pRequirements);
            }

            bool found = false;

            foreach (SparseImageMemoryRequirements requirement in requirements)
            {
                if (requirement.FormatProperties.AspectMask.HasFlag(ImageAspectFlags.ColorBit))
                {
                    _nx2Sparse = requirement;
                    found = true;
                    break;
                }
            }

            if (!found)
            {
                throw new Exception("Sparse image without color sparse requirements.");
            }

            _gd.Api.GetImageMemoryRequirements(_device, _image, out _nx2Requirements);

            _nx2TailLevel = (int)Math.Min(_nx2Sparse.ImageMipTailFirstLod, levels);
            _nx2Tiles = [];
            _nx2Tails = [];

            Ryujinx.Common.Logging.Logger.Info?.Print(Ryujinx.Common.Logging.LogClass.Gpu,
                $"Switch 2 sparse image {_info.Width}x{_info.Height}x{_info.GetLayers()} {_info.Format} levels {levels}: block " +
                $"{_nx2Sparse.FormatProperties.ImageGranularity.Width}x{_nx2Sparse.FormatProperties.ImageGranularity.Height} " +
                $"({_nx2Requirements.Alignment} bytes), mip tail from {_nx2TailLevel} ({_nx2Sparse.ImageMipTailSize} bytes)");
        }

        /// <summary>
        /// Gives memory to the blocks of a sparse image that a region of one level and layer covers.
        /// </summary>
        /// <returns>The regions (layer, level, x, y, width, height) that just got memory: their content is undefined</returns>
        public unsafe List<(int Layer, int Level, int X, int Y, int Width, int Height)> Nx2BindSparse(int layer, int level, int x, int y, int width, int height)
        {
            List<(int Layer, int Level, int X, int Y, int Width, int Height)> fresh = [];

            if (_nx2Tiles == null)
            {
                return fresh;
            }

            List<SparseImageMemoryBind> imageBinds = [];
            List<SparseMemoryBind> opaqueBinds = [];
            ulong tileSize = _nx2Requirements.Alignment;

            MemoryAllocation Allocate(ulong size)
            {
                MemoryRequirements requirements = new()
                {
                    Size = size,
                    Alignment = tileSize,
                    MemoryTypeBits = _nx2Requirements.MemoryTypeBits,
                };

                MemoryAllocation allocation = _gd.MemoryAllocator.AllocateDeviceMemory(requirements, DefaultImageMemoryFlags);

                if (allocation.Memory.Handle == 0UL)
                {
                    throw new Exception("Out of memory for a sparse image block.");
                }

                Nx2BoundBytes += size;

                return allocation;
            }

            lock (_nx2Tiles)
            {
                if (level >= _nx2TailLevel)
                {
                    bool singleTail = _nx2Sparse.FormatProperties.Flags.HasFlag(SparseImageFormatFlags.SingleMiptailBit);
                    int tailLayer = singleTail ? 0 : layer;

                    if (_nx2Tails.ContainsKey(tailLayer) || _nx2Sparse.ImageMipTailSize == 0)
                    {
                        return fresh;
                    }

                    ulong tailSize = BitUtils.AlignUp(_nx2Sparse.ImageMipTailSize, tileSize);
                    MemoryAllocation tail = Allocate(tailSize);

                    _nx2Tails[tailLayer] = tail;

                    int firstLayer = singleTail ? 0 : tailLayer;
                    int lastLayer = singleTail ? _info.GetLayers() - 1 : tailLayer;

                    for (int tailLevel = _nx2TailLevel; tailLevel < _info.Levels; tailLevel++)
                    {
                        for (int fillLayer = firstLayer; fillLayer <= lastLayer; fillLayer++)
                        {
                            fresh.Add((fillLayer, tailLevel, 0, 0, Math.Max(_info.Width >> tailLevel, 1), Math.Max(_info.Height >> tailLevel, 1)));
                        }
                    }

                    opaqueBinds.Add(new SparseMemoryBind
                    {
                        ResourceOffset = _nx2Sparse.ImageMipTailOffset + (ulong)tailLayer * _nx2Sparse.ImageMipTailStride,
                        Size = _nx2Sparse.ImageMipTailSize,
                        Memory = tail.Memory,
                        MemoryOffset = tail.Offset,
                    });
                }
                else
                {
                    int tileWidth = (int)_nx2Sparse.FormatProperties.ImageGranularity.Width;
                    int tileHeight = (int)_nx2Sparse.FormatProperties.ImageGranularity.Height;
                    int levelWidth = Math.Max(_info.Width >> level, 1);
                    int levelHeight = Math.Max(_info.Height >> level, 1);

                    for (int ty = y / tileHeight; ty <= (y + height - 1) / tileHeight; ty++)
                    {
                        for (int tx = x / tileWidth; tx <= (x + width - 1) / tileWidth; tx++)
                        {
                            if (_nx2Tiles.ContainsKey((layer, level, tx, ty)))
                            {
                                continue;
                            }

                            MemoryAllocation tile = Allocate(tileSize);

                            _nx2Tiles[(layer, level, tx, ty)] = tile;

                            int tileX = tx * tileWidth;
                            int tileY = ty * tileHeight;

                            fresh.Add((layer, level, tileX, tileY, Math.Min(tileWidth, levelWidth - tileX), Math.Min(tileHeight, levelHeight - tileY)));

                            imageBinds.Add(new SparseImageMemoryBind
                            {
                                Subresource = new ImageSubresource(ImageAspectFlags.ColorBit, (uint)level, (uint)layer),
                                Offset = new Offset3D(tileX, tileY, 0),
                                Extent = new Extent3D((uint)Math.Min(tileWidth, levelWidth - tileX), (uint)Math.Min(tileHeight, levelHeight - tileY), 1),
                                Memory = tile.Memory,
                                MemoryOffset = tile.Offset,
                            });
                        }
                    }
                }

                if (imageBinds.Count == 0 && opaqueBinds.Count == 0)
                {
                    return fresh;
                }

                SparseImageMemoryBind[] imageBindArray = imageBinds.ToArray();
                SparseMemoryBind[] opaqueBindArray = opaqueBinds.ToArray();

                fixed (SparseImageMemoryBind* pImageBinds = imageBindArray)
                fixed (SparseMemoryBind* pOpaqueBinds = opaqueBindArray)
                {
                    SparseImageMemoryBindInfo imageBindInfo = new()
                    {
                        Image = _image,
                        BindCount = (uint)imageBindArray.Length,
                        PBinds = pImageBinds,
                    };

                    SparseImageOpaqueMemoryBindInfo opaqueBindInfo = new()
                    {
                        Image = _image,
                        BindCount = (uint)opaqueBindArray.Length,
                        PBinds = pOpaqueBinds,
                    };

                    BindSparseInfo bindInfo = new()
                    {
                        SType = StructureType.BindSparseInfo,
                        ImageBindCount = imageBindArray.Length != 0 ? 1u : 0u,
                        PImageBinds = imageBindArray.Length != 0 ? &imageBindInfo : null,
                        ImageOpaqueBindCount = opaqueBindArray.Length != 0 ? 1u : 0u,
                        PImageOpaqueBinds = opaqueBindArray.Length != 0 ? &opaqueBindInfo : null,
                    };

                    FenceCreateInfo fenceInfo = new() { SType = StructureType.FenceCreateInfo };

                    _gd.Api.CreateFence(_device, in fenceInfo, null, out Fence fence).ThrowOnError();

                    try
                    {
                        lock (_gd.QueueLock)
                        {
                            _gd.Api.QueueBindSparse(_gd.Queue, 1, in bindInfo, fence).ThrowOnError();
                        }

                        _gd.Api.WaitForFences(_device, 1, in fence, true, ulong.MaxValue).ThrowOnError();
                    }
                    finally
                    {
                        _gd.Api.DestroyFence(_device, fence, null);
                    }
                }
            }

            return fresh;
        }

        public void Dispose()
        {
            Disposed = true;

            if (_aliasedStorages != null)
            {
                foreach (TextureStorage storage in _aliasedStorages.Values)
                {
                    storage.Dispose();
                }

                _aliasedStorages.Clear();
            }

            _imageAuto.Dispose();
            _allocationAuto?.Dispose();

            if (_nx2Tiles != null)
            {
                lock (_nx2Tiles)
                {
                    foreach (MemoryAllocation tile in _nx2Tiles.Values)
                    {
                        tile.Dispose();
                    }

                    foreach (MemoryAllocation tail in _nx2Tails.Values)
                    {
                        tail.Dispose();
                    }

                    _nx2Tiles.Clear();
                    _nx2Tails.Clear();
                }
            }
            _foreignAllocationAuto?.DecrementReferenceCount();
            _foreignAllocationAuto = null;
        }
    }
}
