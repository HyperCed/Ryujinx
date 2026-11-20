using System;
using System.Collections.Generic;

namespace Ryujinx.Graphics.Shader.Translation
{
    /// <summary>
    /// What a Switch 2 (NVN) draw binds while SET_MESH_CONTROL is on: the vertex slot holds the task
    /// program (or the mesh program when there is no task), the tessellation evaluation slot the mesh
    /// program, and the geometry slot a header-only pass-through program whose output map names the
    /// per-primitive outputs.
    /// </summary>
    public readonly struct Nx2MeshState
    {
        /// <summary>
        /// Stage the program is translated as: <see cref="ShaderStage.Task"/>, <see cref="ShaderStage.Mesh"/>,
        /// or anything else for a program that is neither.
        /// </summary>
        public ShaderStage Stage { get; }

        /// <summary>
        /// SET_MESH_SHADER_A (0x1150): topology 3:0, max vertex 12:4, max primitive 22:13.
        /// </summary>
        public uint ShaderA { get; }

        /// <summary>
        /// SET_MESH_SHADER_B (0x1154): mesh threads 25:20.
        /// </summary>
        public uint ShaderB { get; }

        /// <summary>
        /// SET_MESH_INIT_SHADER (0x1158): task threads 5:0, local buffer lines 20:12, lines passed to the
        /// mesh stage 30:22 (a line is 128 bytes).
        /// </summary>
        public uint InitShader { get; }

        /// <summary>
        /// The pass-through geometry program's header (80 bytes), for mesh programs.
        /// </summary>
        public byte[] PrimitiveHeader { get; }

        /// <summary>
        /// Indicates that the host must execute the task/mesh programs as compute shaders.
        /// </summary>
        public bool ComputeFallback { get; }

        public Nx2MeshState(
            ShaderStage stage,
            uint shaderA,
            uint shaderB,
            uint initShader,
            byte[] primitiveHeader,
            bool computeFallback = false)
        {
            Stage = stage;
            ShaderA = shaderA;
            ShaderB = shaderB;
            InitShader = initShader;
            PrimitiveHeader = primitiveHeader;
            ComputeFallback = computeFallback;
        }

        public Nx2MeshState WithComputeFallback(bool computeFallback) =>
            new(Stage, ShaderA, ShaderB, InitShader, PrimitiveHeader, computeFallback);

        public bool IsTaskOrMesh => Stage is ShaderStage.Task or ShaderStage.Mesh;
    }

    /// <summary>
    /// Layout of a task or mesh program's emulated stage buffer (ISBE). A task program writes the number
    /// of mesh workgroups at byte 4 of its output region and its payload after it; each mesh workgroup
    /// reads that region (its own index at byte 0) and writes its vertices and primitives to an attribute
    /// region in groups of 32, one 32-word line per attribute, plus the primitive count and 8-bit vertex
    /// indices to a map region. Port of Lichii's MeshInfo (shader_recompiler/shader_info.h).
    /// </summary>
    class Nx2MeshInfo
    {
        // Attribute numbering of the lines (as Lichii's IR::Attribute): system values, then generics.
        public const int PrimitiveId = 24;
        public const int Layer = 25;
        public const int ViewportIndex = 26;
        public const int PointSize = 27;
        public const int PositionX = 28;
        public const int PositionW = 31;
        public const int Generic0X = 32;

        public const int PointsTopology = 0;
        public const int LinesTopology = 1;
        public const int TrianglesTopology = 4;

        /// <summary>
        /// Task output region / mesh input region, in 32-bit words.
        /// </summary>
        public int PayloadWords { get; }
        public ShaderStage Stage { get; }
        public int MaxVertices { get; }
        public int MaxPrimitives { get; }
        public int Topology { get; }
        public int Threads { get; }
        public bool ComputeFallback { get; }

        /// <summary>
        /// OpenGL-style clip space to convert (z = (z + w) / 2), as vertex programs do without depth clip control.
        /// </summary>
        public bool ConvertDepth { get; set; }

        /// <summary>
        /// Attribute held by each line of a 32-vertex group (from the mesh program's output map).
        /// </summary>
        public int[] VertexLines { get; }

        /// <summary>
        /// Attribute held by each line of a 32-primitive group (from the pass-through geometry header).
        /// </summary>
        public int[] PrimitiveLines { get; }

        /// <summary>
        /// First word of the per-primitive groups in the attribute region.
        /// </summary>
        public int PrimitiveBase => DivCeil(MaxVertices, 32) * VertexLines.Length * 32;

        public int AttributeWords => Math.Max(PrimitiveBase + DivCeil(MaxPrimitives, 32) * PrimitiveLines.Length * 32, 1);

        public int PrimitiveVertices => Topology switch
        {
            PointsTopology => 1,
            LinesTopology => 2,
            TrianglesTopology => 3,
            _ => throw new NotSupportedException($"Mesh output topology {Topology}"),
        };

        /// <summary>
        /// Words required for one mesh workgroup when its output is stored for a conventional vertex draw.
        /// </summary>
        public int EmulationBlockWords =>
            MaxVertices * VertexLines.Length +
            MaxPrimitives * (PrimitiveVertices + PrimitiveLines.Length);

        public int MapBytes => 4 + PrimitiveVertices * MaxPrimitives;

        /// <summary>
        /// Generic attributes the mesh program writes per primitive (fragment inputs to decorate PerPrimitiveEXT).
        /// </summary>
        public int PerPrimitiveGenerics
        {
            get
            {
                int mask = 0;

                foreach (int line in PrimitiveLines)
                {
                    if (line >= Generic0X)
                    {
                        mask |= 1 << ((line - Generic0X) / 4);
                    }
                }

                return mask;
            }
        }

        public Nx2MeshInfo(ShaderStage stage, Nx2MeshState state, ReadOnlySpan<byte> header)
        {
            Stage = stage;
            uint lines = Math.Max((state.InitShader >> 12) & 0x1ff, (state.InitShader >> 22) & 0x1ff);

            PayloadWords = (int)Math.Max(lines, 1) * 32;
            Topology = (int)(state.ShaderA & 0xf);
            MaxVertices = (int)((state.ShaderA >> 4) & 0x1ff);
            MaxPrimitives = (int)((state.ShaderA >> 13) & 0x3ff);

            uint threads = stage == ShaderStage.Task ? state.InitShader & 0x3f : (state.ShaderB >> 20) & 0x3f;

            Threads = (int)Math.Max(threads, 1);
            ComputeFallback = state.ComputeFallback;

            if (stage == ShaderStage.Mesh)
            {
                if (Topology != TrianglesTopology)
                {
                    throw new NotSupportedException($"Mesh output topology {Topology} (SET_MESH_SHADER_A 0x{state.ShaderA:x})");
                }

                VertexLines = GetLines(header);
                PrimitiveLines = state.PrimitiveHeader != null ? GetLines(state.PrimitiveHeader) : [];
            }
            else
            {
                VertexLines = [];
                PrimitiveLines = [];
            }
        }

        /// <summary>
        /// The lines of a 32-element group: one per attribute an output map enables, in attribute order
        /// (system values, then generics).
        /// </summary>
        private static int[] GetLines(ReadOnlySpan<byte> sph)
        {
            // VTG output map: OmapSystemValuesB at 0x35, OmapGenericVector at 0x36 (4 bits per generic),
            // OmapSystemValuesC at 0x48 (clip distances, point sprite, fog, tessellation point, ids).
            if ((sph[0x48] | sph[0x49]) != 0)
            {
                throw new NotSupportedException("Mesh outputs past the generic attributes");
            }

            List<int> lines = [];

            for (int bit = 0; bit < 8; bit++)
            {
                if ((sph[0x35] & (1 << bit)) != 0)
                {
                    lines.Add(PrimitiveId + bit);
                }
            }

            for (int generic = 0; generic < 32; generic++)
            {
                int bits = sph[0x36 + generic / 2] >> ((generic % 2) * 4);

                for (int element = 0; element < 4; element++)
                {
                    if ((bits & (1 << element)) != 0)
                    {
                        lines.Add(Generic0X + generic * 4 + element);
                    }
                }
            }

            return lines.ToArray();
        }

        private static int DivCeil(int value, int divisor) => (value + divisor - 1) / divisor;
    }
}
