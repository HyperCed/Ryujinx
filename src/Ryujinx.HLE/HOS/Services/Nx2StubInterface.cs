namespace Ryujinx.HLE.HOS.Services
{
    using System.Collections.Generic;
    using Ryujinx.HLE.HOS.Ipc;
    using Ryujinx.HLE.HOS.Kernel.Ipc;
    using Ryujinx.HLE.HOS.Kernel.Process;
    using Ryujinx.HLE.HOS.Kernel.Threading;
    using Ryujinx.HLE.HOS.Services.Nv;
    using Ryujinx.Common.Logging;
    using Ryujinx.Horizon.Common;
    using Ryujinx.Memory;

    interface INx2StubService
    {
        string InterfaceName { get; }
    }

    // Placeholder for Switch 2-only interfaces whose command set is not implemented yet.
    // IpcService accepts unknown CMIF commands on this interface and returns empty success.
    class Nx2StubInterface : IpcService, INx2StubService
    {
        private IVirtualMemoryManager _clientMemory;
        private ulong _owner;

        public string InterfaceName { get; }

        public Nx2StubInterface(string interfaceName, bool registerTipc = false) : base(registerTipc: registerTipc)
        {
            InterfaceName = interfaceName;
        }

        [CommandTipc(11)]
        // SetCurrentProcessHandle(handle<copy, process>)
        public ResultCode SetCurrentProcessHandle(ServiceCtx context)
        {
            if (context.Request.HandleDesc == null || context.Request.HandleDesc.ToCopy.Length < 1)
            {
                return ResultCode.NotAllocated;
            }

            int processHandle = context.Request.HandleDesc.ToCopy[0];
            KProcess process = context.Process.HandleTable.GetKProcess(processHandle);

            if (process == null)
            {
                context.Device.System.KernelContext.Syscall.CloseHandle(processHandle);

                return ResultCode.NotAllocated;
            }

            _clientMemory = process.CpuMemory;
            _owner = process.Pid;
            context.Device.System.KernelContext.Syscall.CloseHandle(processHandle);

            Logger.Stub?.PrintStub(LogClass.ServiceAm, new { commandId = 11, processId = _owner });

            return ResultCode.Success;
        }

        [CommandTipc(202)]
        // InitializeSyncptContext() -> handle<move, INx2SyncptSession>
        public ResultCode InitializeSyncptContext(ServiceCtx context)
        {
            Nx2SyncptSession service = new();
            service.TrySetServer(Server);

            KSession session = new(context.Device.System.KernelContext);

            Result createClientHandleResult = context.Process.HandleTable.GenerateHandle(session.ClientSession, out int clientSessionHandle);

            if (createClientHandleResult != Result.Success)
            {
                Logger.Error?.Print(LogClass.ServiceAm, $"GSV TIPC 202 failed to create a syncpt session handle: {createClientHandleResult}");

                return ResultCode.NotAllocated;
            }

            service.Server.AddSessionObj(session.ServerSession, service);
            session.ServerSession.DecrementReferenceCount();
            session.ClientSession.DecrementReferenceCount();

            context.Response.HandleDesc = IpcHandleDesc.MakeMove(clientSessionHandle);

            Logger.Stub?.PrintStub(LogClass.ServiceAm, new { commandId = 202, clientSessionHandle });

            return ResultCode.Success;
        }

        [CommandTipc(497)]
        // CreateGpuSessionWithRemoteMemory(u64 version) -> handle<move, INx2GpuSession>
        public ResultCode CreateGpuSessionWithRemoteMemory(ServiceCtx context)
        {
            Nx2GpuSession service = new();
            service.TrySetServer(Server);

            KSession session = new(context.Device.System.KernelContext);

            Result createClientHandleResult = context.Process.HandleTable.GenerateHandle(session.ClientSession, out int clientSessionHandle);

            if (createClientHandleResult != Result.Success)
            {
                Logger.Error?.Print(LogClass.ServiceAm, $"GSV TIPC 497 failed to create a GPU session handle: {createClientHandleResult}");

                return ResultCode.NotAllocated;
            }

            service.Server.AddSessionObj(session.ServerSession, service);
            session.ServerSession.DecrementReferenceCount();
            session.ClientSession.DecrementReferenceCount();

            context.Response.HandleDesc = IpcHandleDesc.MakeMove(clientSessionHandle);

            Logger.Stub?.PrintStub(LogClass.ServiceAm, new { commandId = 497, clientSessionHandle });

            return ResultCode.Success;
        }

        [CommandTipc(305)]
        // InitializeDisplayContext(u64 version) -> handle<move, INx2Vi2Session>
        public ResultCode InitializeDisplayContext(ServiceCtx context)
        {
            Nx2Vi2Session service = new();
            service.TrySetServer(Server);

            KSession session = new(context.Device.System.KernelContext);

            Result createClientHandleResult = context.Process.HandleTable.GenerateHandle(session.ClientSession, out int clientSessionHandle);

            if (createClientHandleResult != Result.Success)
            {
                Logger.Error?.Print(LogClass.ServiceAm, $"GSV TIPC 305 failed to create a VI2 session handle: {createClientHandleResult}");

                return ResultCode.NotAllocated;
            }

            service.Server.AddSessionObj(session.ServerSession, service);
            session.ServerSession.DecrementReferenceCount();
            session.ClientSession.DecrementReferenceCount();

            context.Response.HandleDesc = IpcHandleDesc.MakeMove(clientSessionHandle);

            Logger.Stub?.PrintStub(LogClass.ServiceAm, new { commandId = 305, clientSessionHandle });

            return ResultCode.Success;
        }

        [CommandTipc(1402)]
        // CreateNvDrv2UserSessionWithRemoteMemory() -> handle<move, INvDrv2User>
        public ResultCode CreateNvDrv2UserSessionWithRemoteMemory(ServiceCtx context)
        {
            if (_clientMemory == null)
            {
                Logger.Error?.Print(LogClass.ServiceAm, "GSV TIPC 1402 called before SetCurrentProcessHandle.");

                return ResultCode.NotAllocated;
            }

            INvDrvServices service = new(context, _clientMemory, _owner);
            KSession session = new(context.Device.System.KernelContext);

            Result createClientHandleResult = context.Process.HandleTable.GenerateHandle(session.ClientSession, out int clientSessionHandle);

            if (createClientHandleResult != Result.Success)
            {
                Logger.Error?.Print(LogClass.ServiceAm, $"GSV TIPC 1402 failed to create a client session handle: {createClientHandleResult}");

                return ResultCode.NotAllocated;
            }

            service.Server.AddSessionObj(session.ServerSession, service);
            session.ServerSession.DecrementReferenceCount();
            session.ClientSession.DecrementReferenceCount();

            context.Response.HandleDesc = IpcHandleDesc.MakeMove(clientSessionHandle);

            Logger.Stub?.PrintStub(LogClass.ServiceAm, new { commandId = 1402, clientSessionHandle });

            return ResultCode.Success;
        }
    }

    class Nx2SyncptSession : IpcService, INx2StubService
    {
        public string InterfaceName => "nn::syncpt::INx2SyncptSession";

        public Nx2SyncptSession() : base(registerTipc: true) { }

        [CommandTipc(0)]
        // Ping(u64 tick) -> u64 tick
        public ResultCode Ping(ServiceCtx context)
        {
            ulong tick = context.RequestData.ReadUInt64();
            context.ResponseData.Write(tick);

            Logger.Stub?.PrintStub(LogClass.ServiceAm, new { interfaceName = InterfaceName, commandId = 0, tick });

            return ResultCode.Success;
        }

        [CommandTipc(1)]
        // GetCount() -> u32
        public ResultCode GetCount(ServiceCtx context)
        {
            context.ResponseData.Write(32u);

            Logger.Stub?.PrintStub(LogClass.ServiceAm, new { interfaceName = InterfaceName, commandId = 1, count = 32 });

            return ResultCode.Success;
        }
    }

    class Nx2GpuSession : IpcService, INx2StubService
    {
        private readonly Dictionary<uint, KEvent> _events = [];

        public string InterfaceName => "nn::gpu::INx2GpuSession";

        public Nx2GpuSession() : base(registerTipc: true) { }

        [CommandTipc(11)]
        // GetEventIdRange() -> (u32 min, u32 max)
        public ResultCode GetEventIdRange(ServiceCtx context)
        {
            context.ResponseData.Write(0u);
            context.ResponseData.Write(31u);

            return ResultCode.Success;
        }

        [CommandTipc(12)]
        // CreateEvent(u32 eventId) -> handle<copy, event>
        public ResultCode CreateEvent(ServiceCtx context)
        {
            uint eventId = context.RequestData.ReadUInt32();
            KEvent evt = new(context.Device.System.KernelContext);
            _events[eventId] = evt;

            Result result = context.Process.HandleTable.GenerateHandle(evt.ReadableEvent, out int eventHandle);

            if (result != Result.Success)
            {
                return ResultCode.NotAllocated;
            }

            context.Response.HandleDesc = IpcHandleDesc.MakeCopy(eventHandle);

            return ResultCode.Success;
        }

        [CommandTipc(21)]
        // RequestKUserWorkerSession(u32 eventId)
        public ResultCode RequestKUserWorkerSession(ServiceCtx context)
        {
            uint eventId = context.RequestData.ReadUInt32();

            if (_events.TryGetValue(eventId, out KEvent evt))
            {
                evt.WritableEvent.Signal();
            }

            return ResultCode.Success;
        }

        [CommandTipc(22)]
        // GetKUserWorkerSession() -> handle<move, KUserWorkerSession>
        public ResultCode GetKUserWorkerSession(ServiceCtx context)
        {
            Nx2GpuWorkerSession service = new();
            service.TrySetServer(Server);

            KSession session = new(context.Device.System.KernelContext);

            Result result = context.Process.HandleTable.GenerateHandle(session.ClientSession, out int clientSessionHandle);

            if (result != Result.Success)
            {
                return ResultCode.NotAllocated;
            }

            service.Server.AddSessionObj(session.ServerSession, service);
            session.ServerSession.DecrementReferenceCount();
            session.ClientSession.DecrementReferenceCount();

            context.Response.HandleDesc = IpcHandleDesc.MakeMove(clientSessionHandle);

            return ResultCode.Success;
        }
    }

    class Nx2GpuWorkerSession : IpcService, INx2StubService
    {
        public string InterfaceName => "nn::gpuk::impl::kuser::KUserWorkerSession";

        public Nx2GpuWorkerSession() : base(registerTipc: true) { }
    }

    class Nx2Vi2Session : IpcService, INx2StubService
    {
        public string InterfaceName => "nn::vi2::INx2Vi2Session";

        public Nx2Vi2Session() : base(registerTipc: true) { }

        [CommandTipc(1)]
        public ResultCode GetDisplayCount(ServiceCtx context)
        {
            context.ResponseData.Write(1u);

            return ResultCode.Success;
        }

        [CommandTipc(3)]
        public ResultCode GetDisplayState(ServiceCtx context)
        {
            context.ResponseData.Write(1u);

            return ResultCode.Success;
        }

        [CommandTipc(5)]
        public ResultCode GetTimestampRecorderCount(ServiceCtx context)
        {
            context.ResponseData.Write(7u);

            return ResultCode.Success;
        }

        [CommandTipc(11)]
        // GetNativeEvent() -> handle<copy, event>
        public ResultCode GetNativeEvent(ServiceCtx context)
        {
            KEvent evt = new(context.Device.System.KernelContext);

            Result result = context.Process.HandleTable.GenerateHandle(evt.ReadableEvent, out int eventHandle);

            if (result != Result.Success)
            {
                return ResultCode.NotAllocated;
            }

            // No physical display is driven by this stub, so make the event
            // immediately available instead of leaving the game waiting.
            evt.WritableEvent.Signal();
            context.Response.HandleDesc = IpcHandleDesc.MakeCopy(eventHandle);

            Logger.Stub?.PrintStub(LogClass.ServiceAm, new { interfaceName = InterfaceName, commandId = 11, eventHandle });

            return ResultCode.Success;
        }
    }
}

