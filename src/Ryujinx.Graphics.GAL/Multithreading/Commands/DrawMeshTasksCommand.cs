namespace Ryujinx.Graphics.GAL.Multithreading.Commands
{
    struct DrawMeshTasksCommand : IGALCommand, IGALCommand<DrawMeshTasksCommand>
    {
        public readonly CommandType CommandType => CommandType.DrawMeshTasks;
        private int _groupCountX;
        private int _groupCountY;
        private int _groupCountZ;

        public void Set(int groupCountX, int groupCountY, int groupCountZ)
        {
            _groupCountX = groupCountX;
            _groupCountY = groupCountY;
            _groupCountZ = groupCountZ;
        }

        public static void Run(ref DrawMeshTasksCommand command, ThreadedRenderer threaded, IRenderer renderer)
        {
            renderer.Pipeline.DrawMeshTasks(command._groupCountX, command._groupCountY, command._groupCountZ);
        }
    }
}
