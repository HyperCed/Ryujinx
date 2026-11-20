namespace Ryujinx.Graphics.GAL.Multithreading.Commands
{
    struct DispatchComputeIndirectCommand : IGALCommand, IGALCommand<DispatchComputeIndirectCommand>
    {
        public readonly CommandType CommandType => CommandType.DispatchComputeIndirect;
        private BufferRange _indirectBuffer;

        public void Set(BufferRange indirectBuffer)
        {
            _indirectBuffer = indirectBuffer;
        }

        public static void Run(ref DispatchComputeIndirectCommand command, ThreadedRenderer threaded, IRenderer renderer)
        {
            renderer.Pipeline.DispatchComputeIndirect(threaded.Buffers.MapBufferRange(command._indirectBuffer));
        }
    }
}
