using Ryujinx.Graphics.Gpu.Memory;
using System.Diagnostics;

namespace Ryujinx.Graphics.Gpu.Engine.GPFifo
{
    /// <summary>
    /// A semaphore acquire a Switch 2 channel is suspended on.
    /// </summary>
    readonly struct Nx2PendingAcquire
    {
        public readonly ulong Address;
        public readonly uint Value;
        public readonly SemaphoredOperation Operation;
        public readonly long Since;

        public Nx2PendingAcquire(ulong address, uint value, SemaphoredOperation operation)
        {
            Address = address;
            Value = value;
            Operation = operation;
            Since = Stopwatch.GetTimestamp();
        }

        public bool IsMet(MemoryManager memoryManager)
        {
            if (!memoryManager.IsMapped(Address))
            {
                return true;
            }

            uint current = memoryManager.Read<uint>(Address);

            return Operation switch
            {
                SemaphoredOperation.Acquire => current == Value,
                SemaphoredOperation.AcqGeq => current >= Value,
                SemaphoredOperation.AcqAnd => (current & Value) != 0,
                _ => true,
            };
        }
    }
}
