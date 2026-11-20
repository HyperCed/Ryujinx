using Ryujinx.Common.Logging;
using Ryujinx.Graphics.GAL;
using Ryujinx.Graphics.Gpu.Image;
using System;
using System.Buffers.Binary;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Threading;

namespace Ryujinx.Graphics.Gpu
{
    /// <summary>
    /// RYUJINX_NX2_PASS_DUMP=&lt;dir&gt; (with RYUJINX_NX2_PASS_DUMP_AT=&lt;seconds&gt;, default 100): for the frame
    /// after that time, saves every colour render target as the game moves past it, and every image a compute
    /// dispatch wrote, as a PNG plus per-channel statistics (min, max, mean, NaN and Inf counts) in
    /// passes.txt. Finds the first pass of a frame whose output is wrong (a small Lichii frame dump).
    /// </summary>
    static class Nx2PassDumper
    {
        private static readonly string _directory = Environment.GetEnvironmentVariable("RYUJINX_NX2_PASS_DUMP");
        private static readonly double _at = double.TryParse(Environment.GetEnvironmentVariable("RYUJINX_NX2_PASS_DUMP_AT"), out double at) ? at : 100;
        private static readonly Stopwatch _clock = Stopwatch.StartNew();

        // 0 waiting, 1 dumping the frame, 2 done.
        private static int _state;
        private static int _passes;
        private static StreamWriter _summary;

        public static bool Enabled => _directory != null;

        // RYUJINX_NX2_PASS_DUMP_NOPNG=1: statistics only.
        private static readonly bool _noPng = Environment.GetEnvironmentVariable("RYUJINX_NX2_PASS_DUMP_NOPNG") == "1";

        // RYUJINX_NX2_PASS_DUMP_PROGRAM=<hex address>: also save the textures that program's draws sample.
        private static readonly string _targetProgram = Environment.GetEnvironmentVariable("RYUJINX_NX2_PASS_DUMP_PROGRAM")?.ToUpperInvariant();
        private static int _targetDraws;

        // RYUJINX_NX2_PASS_DUMP_RT=<hex GPU address>[-<hex end>]: save the render targets at that address (or in
        // that range) after every draw into them.
        private static readonly string[] _targetRange = Environment.GetEnvironmentVariable("RYUJINX_NX2_PASS_DUMP_RT")?.Split('-');
        public static readonly ulong TargetRenderTarget = _targetRange != null ? System.Convert.ToUInt64(_targetRange[0], 16) : 0;
        private static readonly ulong _targetRenderTargetEnd = _targetRange?.Length == 2 ? System.Convert.ToUInt64(_targetRange[1], 16) : TargetRenderTarget + 1;

        public static bool IsTargetRenderTarget(ulong address) => address >= TargetRenderTarget && address < _targetRenderTargetEnd;

        /// <summary>
        /// Saves raw bytes next to the passes (constant buffers of a traced draw).
        /// </summary>
        public static void WriteRaw(string name, ReadOnlySpan<byte> data)
        {
            if (Active)
            {
                File.WriteAllBytes(Path.Combine(_directory, name), data.ToArray());
            }
        }

        public static void Note(string text)
        {
            if (Active)
            {
                _summary?.WriteLine($"     {text}");
            }
        }

        public static bool IsTargetProgram(string program)
        {
            bool match = _targetProgram != null && program != null && program.Contains("0x" + _targetProgram);

            Note($"target {_targetProgram ?? "none"} match {match}");

            return match && Interlocked.Increment(ref _targetDraws) <= 4;
        }

        public static bool Active => Volatile.Read(ref _state) == 1;

        /// <summary>
        /// A frame was presented.
        /// </summary>
        public static void OnPresent()
        {
            if (_directory == null || _clock.Elapsed.TotalSeconds < _at)
            {
                return;
            }

            if (Interlocked.CompareExchange(ref _state, 1, 0) == 0)
            {
                Directory.CreateDirectory(_directory);
                _summary = new StreamWriter(Path.Combine(_directory, "passes.txt")) { AutoFlush = true };
                Logger.Info?.Print(LogClass.Gpu, $"Switch 2 pass dump started into {_directory}");
            }
            else if (Interlocked.CompareExchange(ref _state, 2, 1) == 1)
            {
                Logger.Info?.Print(LogClass.Gpu, $"Switch 2 pass dump done: {_passes} passes");
            }
        }

        /// <summary>
        /// Saves a texture's level 0, layer 0 (on the GPU thread, while dumping).
        /// </summary>
        public static void Dump(Image.Texture texture, string what, string program = null)
        {
            if (!Active)
            {
                return;
            }

            int index = Interlocked.Increment(ref _passes);

            if (texture?.HostTexture == null)
            {
                _summary?.WriteLine($"{index:D4}_{what}: no texture{(program != null ? $" [{program}]" : "")}");

                return;
            }

            try
            {
                int width = texture.Info.Width;
                int height = texture.Info.Height;
                Format format = texture.Format;

                using PinnedSpan<byte> data = texture.HostTexture.GetData(0, 0);

                string name = $"{index:D4}_{what}_{texture.Info.GpuAddress:x}_{width}x{height}_{format}";
                string stats = Convert(data.Get().ToArray(), width, height, format, texture.Info.FormatInfo.BytesPerPixel, out byte[] rgba);

                _summary.WriteLine($"{name}: {stats}{(program != null ? $" [{program}]" : "")}");

                if (rgba != null && !_noPng)
                {
                    WritePng(Path.Combine(_directory, name + ".png"), width, height, rgba);
                }
            }
            catch (Exception exception)
            {
                _summary?.WriteLine($"{index:D4}_{what}: {exception.GetType().Name}: {exception.Message}");
            }
        }

        private static string Convert(byte[] data, int width, int height, Format format, int bytesPerPixel, out byte[] rgba)
        {
            int components;
            Func<int, int, float> read; // (pixel, component) -> value

            switch (format)
            {
                case Format.R8G8B8A8Unorm:
                case Format.R8G8B8A8Srgb:
                case Format.B8G8R8A8Unorm:
                case Format.B8G8R8A8Srgb:
                    components = 4;
                    read = (p, c) => data[p * 4 + c] / 255f;
                    break;
                case Format.R8Unorm:
                    components = 1;
                    read = (p, c) => data[p] / 255f;
                    break;
                case Format.R8G8Unorm:
                    components = 2;
                    read = (p, c) => data[p * 2 + c] / 255f;
                    break;
                case Format.R16Unorm:
                case Format.D16Unorm:
                    components = 1;
                    read = (p, c) => BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(p * 2)) / 65535f;
                    break;
                case Format.R16G16Unorm:
                    components = 2;
                    read = (p, c) => BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(p * 4 + c * 2)) / 65535f;
                    break;
                case Format.R16Float:
                    components = 1;
                    read = (p, c) => (float)BinaryPrimitives.ReadHalfLittleEndian(data.AsSpan(p * 2));
                    break;
                case Format.R16G16Float:
                    components = 2;
                    read = (p, c) => (float)BinaryPrimitives.ReadHalfLittleEndian(data.AsSpan(p * 4 + c * 2));
                    break;
                case Format.R16G16B16A16Float:
                    components = 4;
                    read = (p, c) => (float)BinaryPrimitives.ReadHalfLittleEndian(data.AsSpan(p * 8 + c * 2));
                    break;
                case Format.R32Float:
                case Format.D32Float:
                    components = 1;
                    read = (p, c) => BinaryPrimitives.ReadSingleLittleEndian(data.AsSpan(p * 4));
                    break;
                case Format.R32G32Float:
                    components = 2;
                    read = (p, c) => BinaryPrimitives.ReadSingleLittleEndian(data.AsSpan(p * 8 + c * 4));
                    break;
                case Format.R32G32B32A32Float:
                    components = 4;
                    read = (p, c) => BinaryPrimitives.ReadSingleLittleEndian(data.AsSpan(p * 16 + c * 4));
                    break;
                case Format.R11G11B10Float:
                    components = 3;
                    read = (p, c) => UnpackR11G11B10(BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(p * 4)), c);
                    break;
                case Format.R10G10B10A2Unorm:
                    components = 4;
                    read = (p, c) =>
                    {
                        uint v = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(p * 4));
                        return c == 3 ? (v >> 30) / 3f : ((v >> (c * 10)) & 0x3ff) / 1023f;
                    };
                    break;
                default:
                    rgba = null;
                    return $"format not decoded ({data.Length} bytes)";
            }

            int pixels = Math.Min(width * height, data.Length / Math.Max(1, bytesPerPixel));
            double[] min = new double[components], max = new double[components], sum = new double[components];
            long nan = 0, inf = 0;

            Array.Fill(min, double.MaxValue);
            Array.Fill(max, double.MinValue);

            rgba = new byte[width * height * 4];

            for (int p = 0; p < pixels; p++)
            {
                for (int c = 0; c < 4; c++)
                {
                    float value = c < components ? read(p, c) : (c == 3 ? 1f : (components == 1 ? read(p, 0) : 0f));

                    if (c < components)
                    {
                        if (float.IsNaN(value))
                        {
                            nan++;
                        }
                        else if (float.IsInfinity(value))
                        {
                            inf++;
                        }
                        else
                        {
                            min[c] = Math.Min(min[c], value);
                            max[c] = Math.Max(max[c], value);
                            sum[c] += value;
                        }
                    }

                    // HDR values are shown with a simple x / (1 + x) curve.
                    float shown = float.IsNaN(value) ? 1f : value > 1f && format.IsFloat() ? value / (1f + value) : value;
                    rgba[p * 4 + c] = (byte)Math.Clamp(shown * 255f + 0.5f, 0f, 255f);
                }
            }

            StringBuilder text = new();

            for (int c = 0; c < components; c++)
            {
                text.Append($"c{c} min {min[c]:G4} max {max[c]:G4} mean {sum[c] / Math.Max(1, pixels):G4}; ");
            }

            text.Append($"NaN {nan} Inf {inf}");

            return text.ToString();
        }

        private static bool IsFloat(this Format format)
        {
            return format is Format.R16Float or Format.R16G16Float or Format.R16G16B16A16Float or Format.R32Float or
                Format.R32G32Float or Format.R32G32B32A32Float or Format.R11G11B10Float or Format.D32Float;
        }

        private static float UnpackR11G11B10(uint value, int component)
        {
            (int shift, int mantissaBits) = component switch
            {
                0 => (0, 6),
                1 => (11, 6),
                _ => (22, 5),
            };

            uint bits = value >> shift;
            uint mantissa = bits & ((1u << mantissaBits) - 1);
            uint exponent = (bits >> mantissaBits) & 0x1f;

            if (exponent == 0x1f)
            {
                return mantissa == 0 ? float.PositiveInfinity : float.NaN;
            }

            if (exponent == 0)
            {
                return mantissa / (float)(1 << mantissaBits) * MathF.Pow(2, -14);
            }

            return (1 + mantissa / (float)(1 << mantissaBits)) * MathF.Pow(2, (int)exponent - 15);
        }

        private static void WritePng(string path, int width, int height, byte[] rgba)
        {
            using MemoryStream raw = new();

            using (ZLibStream zlib = new(raw, CompressionLevel.Fastest, leaveOpen: true))
            {
                for (int y = 0; y < height; y++)
                {
                    zlib.WriteByte(0);
                    zlib.Write(rgba, y * width * 4, width * 4);
                }
            }

            using FileStream file = File.Create(path);

            file.Write([0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a]);

            byte[] header = new byte[13];
            BinaryPrimitives.WriteInt32BigEndian(header, width);
            BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), height);
            header[8] = 8;
            header[9] = 6;

            WriteChunk(file, "IHDR", header);
            WriteChunk(file, "IDAT", raw.ToArray());
            WriteChunk(file, "IEND", []);
        }

        private static void WriteChunk(Stream stream, string type, byte[] data)
        {
            byte[] length = new byte[4];
            BinaryPrimitives.WriteInt32BigEndian(length, data.Length);
            stream.Write(length);

            byte[] typeBytes = Encoding.ASCII.GetBytes(type);
            stream.Write(typeBytes);
            stream.Write(data);

            uint crc = Crc(typeBytes, data);
            byte[] crcBytes = new byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(crcBytes, crc);
            stream.Write(crcBytes);
        }

        private static uint Crc(byte[] type, byte[] data)
        {
            uint crc = 0xffffffff;

            void Update(byte[] bytes)
            {
                foreach (byte b in bytes)
                {
                    crc ^= b;

                    for (int k = 0; k < 8; k++)
                    {
                        crc = (crc & 1) != 0 ? 0xedb88320 ^ (crc >> 1) : crc >> 1;
                    }
                }
            }

            Update(type);
            Update(data);

            return ~crc;
        }
    }
}
