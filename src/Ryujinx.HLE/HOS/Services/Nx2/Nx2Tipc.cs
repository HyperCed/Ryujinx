using Ryujinx.HLE.HOS.Ipc;
using Ryujinx.HLE.HOS.Kernel.Common;
using Ryujinx.HLE.HOS.Kernel.Ipc;
using Ryujinx.HLE.HOS.Kernel.Threading;
using Ryujinx.Horizon.Common;
using System;
using System.Runtime.InteropServices;

namespace Ryujinx.HLE.HOS.Services.Nx2
{
    // Helpers for the Switch 2 graphics sessions, which all speak raw TIPC.
    static class Nx2Tipc
    {
        public const int ZeroedWords = 32;

        // Raw data words of the request (the words after the header, handles and buffer descriptors),
        // padded so that handlers can index past what short requests carry.
        public static uint[] Words(ServiceCtx context, int minimumCount = 40)
        {
            ReadOnlySpan<uint> raw = MemoryMarshal.Cast<byte, uint>(context.Request.RawData ?? []);
            uint[] words = new uint[Math.Max(raw.Length, minimumCount)];
            raw.CopyTo(words);

            return words;
        }

        public static ulong Pair(uint[] words, int index) => words[index] | ((ulong)words[index + 1] << 32);

        public static void Reply(ServiceCtx context, params ReadOnlySpan<uint> words)
        {
            foreach (uint word in words)
            {
                context.ResponseData.Write(word);
            }
        }

        public static void Reply64(ServiceCtx context, params ReadOnlySpan<ulong> values)
        {
            foreach (ulong value in values)
            {
                context.ResponseData.Write(value);
            }
        }

        // TIPC 0(u64 token): a handshake newer SDKs send first; the reply echoes the token.
        public static ResultCode Handshake(ServiceCtx context)
        {
            uint[] w = Words(context);
            Reply(context, w[0], w[1]);

            return ResultCode.Success;
        }

        // Replies with a handle to a new session served by `service`.
        public static ResultCode ReturnSession(ServiceCtx context, IpcService owner, IpcService service)
        {
            service.TrySetServer(owner.Server);

            KSession session = new(context.Device.System.KernelContext);

            Result result = context.Process.HandleTable.GenerateHandle(session.ClientSession, out int clientSessionHandle);

            if (result != Result.Success)
            {
                session.ServerSession.DecrementReferenceCount();
                session.ClientSession.DecrementReferenceCount();

                return ResultCode.NotAllocated;
            }

            service.Server.AddSessionObj(session.ServerSession, service);
            session.ServerSession.DecrementReferenceCount();
            session.ClientSession.DecrementReferenceCount();

            context.Response.HandleDesc = IpcHandleDesc.MakeMove(clientSessionHandle);

            return ResultCode.Success;
        }

        public static ResultCode ReturnHandle(ServiceCtx context, KAutoObject obj)
        {
            if (context.Process.HandleTable.GenerateHandle(obj, out int handle) != Result.Success)
            {
                return ResultCode.NotAllocated;
            }

            context.Response.HandleDesc = IpcHandleDesc.MakeMove(handle);

            return ResultCode.Success;
        }

        public static ResultCode ReturnHandles(ServiceCtx context, KAutoObject first, KAutoObject second)
        {
            if (context.Process.HandleTable.GenerateHandle(first, out int firstHandle) != Result.Success ||
                context.Process.HandleTable.GenerateHandle(second, out int secondHandle) != Result.Success)
            {
                return ResultCode.NotAllocated;
            }

            context.Response.HandleDesc = new IpcHandleDesc([], [firstHandle, secondHandle]);

            return ResultCode.Success;
        }

        public static ResultCode ReturnEvent(ServiceCtx context, KEvent evt) => ReturnHandle(context, evt.ReadableEvent);
    }
}
