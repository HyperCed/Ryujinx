namespace Ryujinx.HLE.HOS.Services
{
    interface INx2StubService
    {
        string InterfaceName { get; }

        // Commands whose reply has to be zeroed instead of keeping the request's words.
        bool IsZeroedCommand(int commandId) => false;
    }

    // Placeholder for Switch 2-only interfaces whose command set is not implemented yet.
    // IpcService accepts unknown commands on this interface and returns empty success.
    class Nx2StubInterface : IpcService, INx2StubService
    {
        public string InterfaceName { get; }

        public Nx2StubInterface(string interfaceName, bool registerTipc = false) : base(registerTipc: registerTipc)
        {
            InterfaceName = interfaceName;
        }
    }
}
