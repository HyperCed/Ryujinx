using Ryujinx.Graphics.Shader;
using Ryujinx.Graphics.Shader.Translation;
using System;

namespace Ryujinx.Graphics.Gpu.Shader.Nx2
{
    /// <summary>
    /// A Switch 2 draw with SET_MESH_CONTROL on: the vertex slot holds the task program (or the mesh program
    /// when there is no task), the tessellation evaluation slot the mesh program, and the geometry slot a
    /// header-only pass-through program naming the per-primitive outputs.
    /// </summary>
    sealed class Nx2MeshDraw
    {
        public readonly uint ShaderA;
        public readonly uint ShaderB;
        public readonly uint InitShader;

        /// <summary>
        /// The pass-through geometry header (80 bytes), or null.
        /// </summary>
        public readonly byte[] PrimitiveHeader;

        public readonly bool HasTask;

        /// <summary>
        /// Hash of the state task and mesh programs are translated with (pipelines are keyed by it too).
        /// </summary>
        public readonly ulong Key;

        public Nx2MeshDraw(uint shaderA, uint shaderB, uint initShader, byte[] primitiveHeader, bool hasTask)
        {
            ShaderA = shaderA;
            ShaderB = shaderB;
            InitShader = initShader;
            PrimitiveHeader = primitiveHeader;
            HasTask = hasTask;

            HashCode hash = new();
            hash.Add(shaderA);
            hash.Add(shaderB);
            hash.Add(initShader);
            hash.Add(hasTask);
            hash.AddBytes(primitiveHeader ?? []);
            Key = (ulong)(uint)hash.ToHashCode() | ((ulong)(shaderA ^ initShader) << 32);
        }

        /// <summary>
        /// The task/mesh state of the program in a graphics slot (stage index 0 vertex, 2 tessellation evaluation).
        /// </summary>
        public Nx2MeshState GetState(int stageIndex)
        {
            ShaderStage stage = stageIndex switch
            {
                0 => HasTask ? ShaderStage.Task : ShaderStage.Mesh,
                2 => ShaderStage.Mesh,
                _ => ShaderStage.Count,
            };

            return new Nx2MeshState(stage, ShaderA, ShaderB, InitShader, PrimitiveHeader);
        }

        /// <summary>
        /// Generic attributes the mesh program writes per primitive (the geometry header's output map).
        /// </summary>
        public int PerPrimitiveGenerics
        {
            get
            {
                if (PrimitiveHeader == null)
                {
                    return 0;
                }

                int mask = 0;

                for (int generic = 0; generic < 32; generic++)
                {
                    if (((PrimitiveHeader[0x36 + generic / 2] >> ((generic % 2) * 4)) & 0xf) != 0)
                    {
                        mask |= 1 << generic;
                    }
                }

                return mask;
            }
        }
    }
}
