using Ryujinx.Common.Logging;
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;

namespace Ryujinx.HLE.HOS.Services.Nx2
{
    // Output layout of a video decoder image (nn::codec image pools, gsv 991/911/961). The preference
    // (MakeDecodeImagePreference) holds a layout code at word 1 and (width - 1) | (height - 1) << 16 at
    // word 2. Codes come in pairs, one per CodecMode: 1/4 NV12 (what movie players use), 2/5 BGRA, the
    // others RGBA. Pitch-linear rows, NV12's half-height chroma plane below the luma, each plane padded
    // to 128 rows (Lichii's Nx2DecoderImageLayout).
    readonly struct Nx2DecoderImageLayout
    {
        public readonly uint Code;
        public readonly uint Width;
        public readonly uint Height;
        public readonly bool Nv12;
        public readonly bool Bgra;
        public readonly ulong Pitch;
        public readonly ulong ChromaOffset;
        public readonly ulong Size;

        public Nx2DecoderImageLayout(ReadOnlySpan<uint> preference)
        {
            const ulong BlockRows = 128;

            Code = preference[1];
            Width = (preference[2] & 0xffff) + 1;
            Height = (preference[2] >> 16) + 1;
            Nv12 = Code is 1 or 4;
            Bgra = Code is 2 or 5;
            Pitch = Common.BitUtils.AlignUp(Width * (Nv12 ? 1UL : 4UL), 64UL);
            ChromaOffset = Pitch * Common.BitUtils.AlignUp((ulong)Height, BlockRows);
            Size = ChromaOffset + (Nv12 ? Pitch * Common.BitUtils.AlignUp((Height + 1UL) / 2, BlockRows) : 0);
        }
    }

    // A decoded picture: 8-bit 4:2:0 planes copied out of FFmpeg's frame.
    class Nx2Picture
    {
        public int Width;
        public int Height;
        public byte[] Y;
        public byte[] U; // NV12 input: the interleaved UV plane, V null
        public byte[] V;
        public int YPitch;
        public int UvPitch;
        public ulong Pts;
    }

    // FFmpeg decoding for the Switch 2 video decoder service, through the public send/receive API only so
    // that any libavcodec from 58 on works (Ryujinx's own binding is limited to 59/60 and reaches into
    // private codec structures). The libraries are found next to Ryujinx, in RYUJINX_NX2_FFMPEG_DIR, or in
    // Homebrew's lib directories.
    unsafe class Nx2VideoDecoder : IDisposable
    {
        private const int AvErrorEagain = -35; // AVERROR(EAGAIN) on macOS
        private const int PacketPadding = 64;

        // Stable since libavcodec 57: AVFrame data[8] at 0, linesize[8] at 64, width/height at 104/108,
        // format at 116; AVPacket pts at 8, data at 24, size at 32.
        private const int FrameLinesize = 64;
        private const int FrameWidth = 104;
        private const int FrameHeight = 108;
        private const int FrameFormat = 116;
        private const int PacketPts = 8;
        private const int PacketData = 24;
        private const int PacketSize = 32;

        private const int PixYuv420p = 0;
        private const int PixYuvj420p = 12;
        private const int PixNv12 = 23;

        private static readonly object _loadLock = new();
        private static bool _loadTried;
        private static bool _loaded;

        private static delegate* unmanaged<byte*, nint> _findDecoderByName;
        private static delegate* unmanaged<nint, nint> _allocContext;
        private static delegate* unmanaged<nint, nint, nint, int> _open;
        private static delegate* unmanaged<nint*, void> _freeContext;
        private static delegate* unmanaged<nint, nint, int> _sendPacket;
        private static delegate* unmanaged<nint, nint, int> _receiveFrame;
        private static delegate* unmanaged<nint, void> _flushBuffers;
        private static delegate* unmanaged<nint> _packetAlloc;
        private static delegate* unmanaged<nint*, void> _packetFree;
        private static delegate* unmanaged<nint> _frameAlloc;
        private static delegate* unmanaged<nint*, void> _frameFree;
        private static delegate* unmanaged<nint, void> _frameUnref;
        private static delegate* unmanaged<nint, byte*, byte*, int, int> _optSet;

        private nint _context;
        private nint _packet;
        private nint _frame;
        private readonly List<ulong> _pendingPts = [];
        private bool _loggedFormat;

        public string Name { get; }
        public bool IsOpen => _context != 0;

        public Nx2VideoDecoder(string codec)
        {
            Name = codec;

            string ffmpegName = codec switch
            {
                "video/vp9" => "vp9",
                "video/vp8" => "vp8",
                "video/avc" => "h264",
                "video/hevc" => "hevc",
                _ => null,
            };

            if (ffmpegName == null)
            {
                Logger.Warning?.Print(LogClass.ServiceNv, $"nx2:vdec unsupported codec \"{codec}\"");

                return;
            }

            if (!Load())
            {
                return;
            }

            nint avCodec;

            fixed (byte* name = NullTerminated(ffmpegName))
            {
                avCodec = _findDecoderByName(name);
            }

            if (avCodec == 0)
            {
                Logger.Error?.Print(LogClass.ServiceNv, $"nx2:vdec FFmpeg has no {ffmpegName} decoder");

                return;
            }

            nint context = _allocContext(avCodec);

            if (context == 0)
            {
                return;
            }

            fixed (byte* key = NullTerminated("threads"))
            fixed (byte* value = NullTerminated("auto"))
            {
                _optSet(context, key, value, 0);
            }

            if (_open(context, avCodec, 0) < 0)
            {
                Logger.Error?.Print(LogClass.ServiceNv, $"nx2:vdec couldn't open the {ffmpegName} decoder");
                _freeContext(&context);

                return;
            }

            _context = context;
            _packet = _packetAlloc();
            _frame = _frameAlloc();

            Logger.Info?.Print(LogClass.ServiceNv, $"nx2:vdec FFmpeg {ffmpegName} decoder open for \"{codec}\"");
        }

        private static byte[] NullTerminated(string text)
        {
            byte[] bytes = new byte[text.Length + 1];
            System.Text.Encoding.ASCII.GetBytes(text, bytes);

            return bytes;
        }

        private static bool Load()
        {
            lock (_loadLock)
            {
                if (_loadTried)
                {
                    return _loaded;
                }

                _loadTried = true;

                List<string> directories = [];

                if (Environment.GetEnvironmentVariable("RYUJINX_NX2_FFMPEG_DIR") is { Length: > 0 } custom)
                {
                    directories.Add(custom);
                }

                directories.Add(AppContext.BaseDirectory);
                directories.Add("/opt/homebrew/lib");
                directories.Add("/usr/local/lib");
                directories.Add("/opt/homebrew/opt/ffmpeg/lib");

                nint avcodec = 0;
                nint avutil = 0;

                foreach (string directory in directories)
                {
                    if (TryLoad(directory, "avcodec", out avcodec) && TryLoad(directory, "avutil", out avutil))
                    {
                        Logger.Info?.Print(LogClass.ServiceNv, $"nx2:vdec FFmpeg loaded from {directory}");

                        break;
                    }

                    avcodec = 0;
                }

                if (avcodec == 0 && NativeLibrary.TryLoad(OperatingSystem.IsMacOS() ? "libavcodec.dylib" : "avcodec", out avcodec))
                {
                    NativeLibrary.TryLoad(OperatingSystem.IsMacOS() ? "libavutil.dylib" : "avutil", out avutil);
                }

                if (avcodec == 0 || avutil == 0)
                {
                    Logger.Error?.Print(LogClass.ServiceNv, "nx2:vdec FFmpeg (libavcodec/libavutil) not found: movies play without picture. Install it (brew install ffmpeg) or set RYUJINX_NX2_FFMPEG_DIR");

                    return false;
                }

                try
                {
                    _findDecoderByName = (delegate* unmanaged<byte*, nint>)NativeLibrary.GetExport(avcodec, "avcodec_find_decoder_by_name");
                    _allocContext = (delegate* unmanaged<nint, nint>)NativeLibrary.GetExport(avcodec, "avcodec_alloc_context3");
                    _open = (delegate* unmanaged<nint, nint, nint, int>)NativeLibrary.GetExport(avcodec, "avcodec_open2");
                    _freeContext = (delegate* unmanaged<nint*, void>)NativeLibrary.GetExport(avcodec, "avcodec_free_context");
                    _sendPacket = (delegate* unmanaged<nint, nint, int>)NativeLibrary.GetExport(avcodec, "avcodec_send_packet");
                    _receiveFrame = (delegate* unmanaged<nint, nint, int>)NativeLibrary.GetExport(avcodec, "avcodec_receive_frame");
                    _flushBuffers = (delegate* unmanaged<nint, void>)NativeLibrary.GetExport(avcodec, "avcodec_flush_buffers");
                    _packetAlloc = (delegate* unmanaged<nint>)NativeLibrary.GetExport(avcodec, "av_packet_alloc");
                    _packetFree = (delegate* unmanaged<nint*, void>)NativeLibrary.GetExport(avcodec, "av_packet_free");
                    _frameAlloc = (delegate* unmanaged<nint>)NativeLibrary.GetExport(avutil, "av_frame_alloc");
                    _frameFree = (delegate* unmanaged<nint*, void>)NativeLibrary.GetExport(avutil, "av_frame_free");
                    _frameUnref = (delegate* unmanaged<nint, void>)NativeLibrary.GetExport(avutil, "av_frame_unref");
                    _optSet = (delegate* unmanaged<nint, byte*, byte*, int, int>)NativeLibrary.GetExport(avutil, "av_opt_set");
                }
                catch (EntryPointNotFoundException e)
                {
                    Logger.Error?.Print(LogClass.ServiceNv, $"nx2:vdec FFmpeg is missing {e.Message}");

                    return false;
                }

                _loaded = true;

                return true;
            }
        }

        private static bool TryLoad(string directory, string name, out nint handle)
        {
            handle = 0;

            if (!Directory.Exists(directory))
            {
                return false;
            }

            List<string> candidates = [];

            if (OperatingSystem.IsMacOS())
            {
                candidates.Add($"lib{name}.dylib");

                for (int version = 70; version >= 55; version--)
                {
                    candidates.Add($"lib{name}.{version}.dylib");
                }
            }
            else if (OperatingSystem.IsWindows())
            {
                for (int version = 70; version >= 55; version--)
                {
                    candidates.Add($"{name}-{version}.dll");
                }
            }
            else
            {
                candidates.Add($"lib{name}.so");

                for (int version = 70; version >= 55; version--)
                {
                    candidates.Add($"lib{name}.so.{version}");
                }
            }

            foreach (string candidate in candidates)
            {
                string path = Path.Combine(directory, candidate);

                if (File.Exists(path) && NativeLibrary.TryLoad(path, out handle))
                {
                    return true;
                }
            }

            return false;
        }

        // Decodes one compressed frame; appends the pictures it completes.
        public void Decode(byte[] data, ulong pts, List<Nx2Picture> pictures)
        {
            if (_context == 0 || data.Length == 0)
            {
                return;
            }

            byte[] padded = new byte[data.Length + PacketPadding];
            data.CopyTo(padded, 0);

            fixed (byte* bytes = padded)
            {
                *(byte**)(_packet + PacketData) = bytes;
                *(int*)(_packet + PacketSize) = data.Length;
                *(long*)(_packet + PacketPts) = (long)pts;

                int result = _sendPacket(_context, _packet);

                *(byte**)(_packet + PacketData) = null;
                *(int*)(_packet + PacketSize) = 0;

                if (result < 0 && result != AvErrorEagain)
                {
                    Logger.Debug?.Print(LogClass.ServiceNv, $"nx2:vdec {Name} send packet failed ({result})");

                    return;
                }
            }

            AddPts(pts);
            Receive(pictures);
        }

        // End of stream: returns the frames the decoder still holds.
        public void Drain(List<Nx2Picture> pictures)
        {
            if (_context == 0)
            {
                return;
            }

            _sendPacket(_context, 0);
            Receive(pictures);

            // A drained decoder only takes packets again after a flush.
            _flushBuffers(_context);
            _pendingPts.Clear();
        }

        public void Reset()
        {
            if (_context != 0)
            {
                _flushBuffers(_context);
            }

            _pendingPts.Clear();
        }

        private void AddPts(ulong pts)
        {
            int index = _pendingPts.BinarySearch(pts);
            _pendingPts.Insert(index < 0 ? ~index : index, pts);

            // Packets without a picture of their own (hidden VP9 frames) leave their time stamp behind.
            if (_pendingPts.Count > 16)
            {
                _pendingPts.RemoveAt(0);
            }
        }

        private void Receive(List<Nx2Picture> pictures)
        {
            while (_receiveFrame(_context, _frame) >= 0)
            {
                Nx2Picture picture = Copy();

                if (picture != null)
                {
                    // Pictures come out in presentation order: each takes the earliest time stamp left.
                    if (_pendingPts.Count > 0)
                    {
                        picture.Pts = _pendingPts[0];
                        _pendingPts.RemoveAt(0);
                    }
                    else if (pictures.Count > 0)
                    {
                        picture.Pts = pictures[^1].Pts;
                    }

                    pictures.Add(picture);
                }

                _frameUnref(_frame);
            }
        }

        private Nx2Picture Copy()
        {
            byte* frame = (byte*)_frame;
            int width = *(int*)(frame + FrameWidth);
            int height = *(int*)(frame + FrameHeight);
            int format = *(int*)(frame + FrameFormat);
            byte** data = (byte**)frame;
            int* linesize = (int*)(frame + FrameLinesize);

            if (width <= 0 || height <= 0)
            {
                return null;
            }

            if (format != PixYuv420p && format != PixYuvj420p && format != PixNv12)
            {
                if (!_loggedFormat)
                {
                    _loggedFormat = true;
                    Logger.Warning?.Print(LogClass.ServiceNv, $"nx2:vdec {Name} pixel format {format} isn't handled, frames left blank");
                }

                return null;
            }

            int chromaHeight = (height + 1) / 2;
            Nx2Picture picture = new()
            {
                Width = width,
                Height = height,
                YPitch = linesize[0],
                UvPitch = linesize[1],
                Y = new ReadOnlySpan<byte>(data[0], linesize[0] * height).ToArray(),
                U = new ReadOnlySpan<byte>(data[1], linesize[1] * chromaHeight).ToArray(),
                V = format == PixNv12 ? null : new ReadOnlySpan<byte>(data[2], linesize[2] * chromaHeight).ToArray(),
            };

            if (format != PixNv12 && linesize[2] != linesize[1])
            {
                return null;
            }

            return picture;
        }

        // Writes a picture as NV12 (or RGBA/BGRA) in an image's layout, scaled to `width` x `height`.
        public static void Render(Nx2Picture picture, in Nx2DecoderImageLayout layout, int width, int height, Span<byte> output)
        {
            int pitch = (int)layout.Pitch;
            bool scale = width != picture.Width || height != picture.Height;

            if (layout.Nv12)
            {
                for (int y = 0; y < height; y++)
                {
                    int sy = scale ? (int)((long)y * picture.Height / height) : y;
                    Span<byte> row = output.Slice(y * pitch, width);
                    ReadOnlySpan<byte> source = picture.Y.AsSpan(sy * picture.YPitch);

                    if (!scale)
                    {
                        source[..width].CopyTo(row);
                    }
                    else
                    {
                        for (int x = 0; x < width; x++)
                        {
                            row[x] = source[(int)((long)x * picture.Width / width)];
                        }
                    }
                }

                int chromaWidth = (width + 1) / 2;
                int chromaHeight = (height + 1) / 2;
                int sourceChromaWidth = (picture.Width + 1) / 2;
                int sourceChromaHeight = (picture.Height + 1) / 2;
                Span<byte> chroma = output[(int)layout.ChromaOffset..];

                for (int y = 0; y < chromaHeight; y++)
                {
                    int sy = scale ? (int)((long)y * sourceChromaHeight / chromaHeight) : y;
                    Span<byte> row = chroma.Slice(y * pitch, chromaWidth * 2);

                    for (int x = 0; x < chromaWidth; x++)
                    {
                        int sx = scale ? (int)((long)x * sourceChromaWidth / chromaWidth) : x;

                        if (picture.V == null)
                        {
                            row[x * 2] = picture.U[sy * picture.UvPitch + sx * 2];
                            row[x * 2 + 1] = picture.U[sy * picture.UvPitch + sx * 2 + 1];
                        }
                        else
                        {
                            row[x * 2] = picture.U[sy * picture.UvPitch + sx];
                            row[x * 2 + 1] = picture.V[sy * picture.UvPitch + sx];
                        }
                    }
                }

                return;
            }

            // RGBA / BGRA (BT.709 limited range).
            for (int y = 0; y < height; y++)
            {
                int sy = (int)((long)y * picture.Height / height);
                Span<byte> row = output.Slice(y * pitch, width * 4);

                for (int x = 0; x < width; x++)
                {
                    int sx = (int)((long)x * picture.Width / width);
                    int luma = picture.Y[sy * picture.YPitch + sx] - 16;
                    int u, v;

                    if (picture.V == null)
                    {
                        u = picture.U[(sy / 2) * picture.UvPitch + (sx / 2) * 2] - 128;
                        v = picture.U[(sy / 2) * picture.UvPitch + (sx / 2) * 2 + 1] - 128;
                    }
                    else
                    {
                        u = picture.U[(sy / 2) * picture.UvPitch + sx / 2] - 128;
                        v = picture.V[(sy / 2) * picture.UvPitch + sx / 2] - 128;
                    }

                    int c = 298 * luma;
                    byte r = (byte)Math.Clamp((c + 459 * v + 128) >> 8, 0, 255);
                    byte g = (byte)Math.Clamp((c - 55 * u - 136 * v + 128) >> 8, 0, 255);
                    byte b = (byte)Math.Clamp((c + 541 * u + 128) >> 8, 0, 255);

                    row[x * 4] = layout.Bgra ? b : r;
                    row[x * 4 + 1] = g;
                    row[x * 4 + 2] = layout.Bgra ? r : b;
                    row[x * 4 + 3] = 255;
                }
            }
        }

        public void Dispose()
        {
            if (_context != 0)
            {
                nint context = _context;
                nint packet = _packet;
                nint frame = _frame;
                _freeContext(&context);
                _packetFree(&packet);
                _frameFree(&frame);
                _context = 0;
            }
        }
    }
}
