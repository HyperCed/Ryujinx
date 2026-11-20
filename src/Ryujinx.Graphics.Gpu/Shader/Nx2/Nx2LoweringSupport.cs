using System;
using System.Collections.Generic;

namespace Ryujinx.Graphics.Gpu.Shader.Nx2
{
    public enum Nx2TessellationStage
    {
        None,
        Control,
        Evaluation,
    }

    public sealed class Nx2LoweringOptions
    {
        public bool Geometry;
        public bool Tensor;
        public ulong NativeCodeAddress;
        public uint[] VerifiedTraps;
        public Nx2TessellationStage TessellationStage;
    }

    /// <summary>
    /// A native program lowered to Maxwell instructions (without scheduling words), or the reason it
    /// could not be.
    /// </summary>
    public sealed class Nx2LoweredProgram
    {
        public readonly Nx2LoweringHelpers.Vec<ulong> Instructions = new();
        public readonly Nx2LoweringHelpers.Vec<uint> SourceInstructionOffsets = new();
        public uint SourceSize;
        public uint RequiredTextureBuffer = ~0U;

        // UR28 and up seen without a mirror: lowering again with this many registers below R216
        // reserved for them (even, so pairs stay adjacent) maps them.
        public uint HighUniformRegisters;
        public uint ErrorOffset;
        public string Error;

        public bool Ok => string.IsNullOrEmpty(Error);
    }

    public static partial class Nx2Lowering
    {
        sealed class Relocation
        {
            public uint output_index;
            public long target;
            public bool call;
            public uint output_target = ~0U;

            public Relocation(uint outputIndex, long target, bool call = false)
            {
                output_index = outputIndex;
                this.target = target;
                this.call = call;
            }
        }

        struct HalfSource
        {
            public uint reg, swizzle;
            public bool absolute, negate;

            public HalfSource(uint reg, uint swizzle, bool absolute, bool negate)
            {
                this.reg = reg;
                this.swizzle = swizzle;
                this.absolute = absolute;
                this.negate = negate;
            }
        }

        /// <summary>
        /// Lowers a native program; programs using uniform registers above UR27 are lowered again with
        /// mirrors for them.
        /// </summary>
        public static Nx2LoweredProgram LowerToMaxwell(Nx2Instruction[] code, Nx2LoweringOptions options)
        {
            uint highUniforms = 0;

            while (true)
            {
                Nx2LoweredProgram lowered = LowerToMaxwellWith(code, highUniforms, options);

                if (lowered.Ok || lowered.HighUniformRegisters <= highUniforms)
                {
                    return lowered;
                }

                highUniforms = lowered.HighUniformRegisters;
            }
        }
    }

    /// <summary>
    /// Shims that keep the C++ translation close to its source.
    /// </summary>
    public static class Nx2LoweringHelpers
    {
        public sealed class Vec<T> : List<T>
        {
            public Vec()
            {
            }

            public Vec(IEnumerable<T> items) : base(items)
            {
            }

            public T this[uint index]
            {
                get => base[(int)index];
                set => base[(int)index] = value;
            }

            public T this[long index]
            {
                get => base[(int)index];
                set => base[(int)index] = value;
            }

            public T this[ulong index]
            {
                get => base[(int)index];
                set => base[(int)index] = value;
            }

            public uint Size() => (uint)Count;
            public bool Empty() => Count == 0;
            public T Front() => this[0];
            public T Back() => this[Count - 1];
            public void PopBack() => RemoveAt(Count - 1);
        }

        public static uint Size<T>(this T[] array) => (uint)array.Length;
        public static bool Empty<T>(this T[] array) => array.Length == 0;
        public static T Front<T>(this T[] array) => array[0];
        public static T Back<T>(this T[] array) => array[^1];
        public static bool Empty(this string text) => string.IsNullOrEmpty(text);
        public static bool Empty<T>(this List<T> list) => list.Count == 0;

        public static bool AnyOf<T>(IEnumerable<T> items, Func<T, bool> predicate)
        {
            foreach (T item in items)
            {
                if (predicate(item))
                {
                    return true;
                }
            }

            return false;
        }

        public static bool NoneOf<T>(IEnumerable<T> items, Func<T, bool> predicate) => !AnyOf(items, predicate);

        public static bool AllOf<T>(IEnumerable<T> items, Func<T, bool> predicate)
        {
            foreach (T item in items)
            {
                if (!predicate(item))
                {
                    return false;
                }
            }

            return true;
        }

        public static ulong U64(ulong value) => value;
        public static ulong U64(long value) => (ulong)value;
        public static ulong U64(uint value) => value;
        public static ulong U64(int value) => (ulong)value;
        public static ulong U64(bool value) => value ? 1UL : 0UL;

        public static uint U32(ulong value) => (uint)value;
        public static uint U32(long value) => (uint)value;
        public static uint U32(uint value) => value;
        public static uint U32(int value) => (uint)value;
        public static uint U32(bool value) => value ? 1U : 0U;

        public static long S64(ulong value) => (long)value;
        public static long S64(long value) => value;
        public static long S64(uint value) => value;
        public static long S64(int value) => value;
        public static long S64(bool value) => value ? 1L : 0L;

        public static int S32(ulong value) => (int)value;
        public static int S32(long value) => (int)value;
        public static int S32(uint value) => (int)value;
        public static int S32(int value) => value;
        public static int S32(bool value) => value ? 1 : 0;

        public static int SZ(ulong value) => (int)value;
        public static int SZ(long value) => (int)value;
        public static int SZ(uint value) => (int)value;
        public static int SZ(int value) => value;

        public static ushort U16(uint value) => (ushort)value;
        public static ushort U16(int value) => (ushort)value;
        public static ushort U16(ulong value) => (ushort)value;
        public static byte U8(uint value) => (byte)value;
        public static byte U8(int value) => (byte)value;

        public static bool B(uint value) => value != 0;
        public static bool B(ulong value) => value != 0;
        public static bool B(int value) => value != 0;
        public static bool B(bool value) => value;
    }
}
