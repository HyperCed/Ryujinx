namespace Ryujinx.Graphics.Shader
{
    public enum ShaderStage : byte
    {
        Compute,
        Vertex,
        TessellationControl,
        TessellationEvaluation,
        Geometry,
        Fragment,

        Count,

        // Switch 2 (NVN) task and mesh programs (VK_EXT_mesh_shader). After Count: arrays sized by stage
        // count keep their size; the GPU binds them in the vertex and tessellation evaluation slots.
        Task,
        Mesh,
    }

    public static class ShaderStageExtensions
    {
        extension(ShaderStage shaderStage)
        {
            /// <summary>
            /// Checks if the shader stage supports render scale.
            /// </summary>
            public bool SupportsRenderScale =>
                shaderStage is ShaderStage.Vertex or ShaderStage.Fragment or ShaderStage.Compute;
            
            /// <summary>
            /// Checks if the shader stage is vertex, tessellation or geometry.
            /// </summary>
            public bool IsVtg => 
                shaderStage is ShaderStage.Vertex or
                    ShaderStage.TessellationControl or
                    ShaderStage.TessellationEvaluation or
                    ShaderStage.Geometry;
        }
        
    }
}
