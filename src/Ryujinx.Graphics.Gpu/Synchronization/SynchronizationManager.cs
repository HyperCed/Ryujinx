using Ryujinx.Common.Logging;
using Ryujinx.Graphics.Device;
using System;
using System.Threading;

namespace Ryujinx.Graphics.Gpu.Synchronization
{
    /// <summary>
    /// GPU synchronization manager.
    /// </summary>
    public class SynchronizationManager : ISynchronizationManager
    {
        /// <summary>
        /// The maximum number of syncpoints supported by the GM20B.
        /// </summary>
        public const int MaxHardwareSyncpoints = 192;

        /// <summary>
        /// Array containing all hardware syncpoints.
        /// </summary>
        private readonly Syncpoint[] _syncpoints;

        public SynchronizationManager()
        {
            _syncpoints = new Syncpoint[MaxHardwareSyncpoints];

            for (uint i = 0; i < _syncpoints.Length; i++)
            {
                _syncpoints[i] = new Syncpoint(i);
            }
        }

        /// <inheritdoc/>
        public uint IncrementSyncpoint(uint id)
        {
            ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(id, (uint)MaxHardwareSyncpoints);

            return _syncpoints[id].Increment();
        }

        /// <inheritdoc/>
        public uint GetSyncpointValue(uint id)
        {
            ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(id, (uint)MaxHardwareSyncpoints);

            return _syncpoints[id].Value;
        }

        /// <summary>
        /// Register a new callback on a syncpoint with a given id at a target threshold.
        /// The callback will be called once the threshold is reached and will automatically be unregistered.
        /// </summary>
        /// <param name="id">The id of the syncpoint</param>
        /// <param name="threshold">The target threshold</param>
        /// <param name="callback">The callback to call when the threshold is reached</param>
        /// <exception cref="System.ArgumentOutOfRangeException">Thrown when id >= MaxHardwareSyncpoints</exception>
        /// <returns>The created SyncpointWaiterHandle object or null if already past threshold</returns>
        public SyncpointWaiterHandle RegisterCallbackOnSyncpoint(uint id, uint threshold, Action<SyncpointWaiterHandle> callback)
        {
            ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(id, (uint)MaxHardwareSyncpoints);

            return _syncpoints[id].RegisterCallback(threshold, callback);
        }

        /// <summary>
        /// Unregister a callback on a given syncpoint.
        /// </summary>
        /// <param name="id">The id of the syncpoint</param>
        /// <param name="waiterInformation">The waiter information to unregister</param>
        /// <exception cref="System.ArgumentOutOfRangeException">Thrown when id >= MaxHardwareSyncpoints</exception>
        public void UnregisterCallback(uint id, SyncpointWaiterHandle waiterInformation)
        {
            ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(id, (uint)MaxHardwareSyncpoints);

            _syncpoints[id].UnregisterCallback(waiterInformation);
        }

        /// <inheritdoc/>
        public bool WaitOnSyncpoint(uint id, uint threshold, TimeSpan timeout) => WaitOnSyncpoint(id, threshold, timeout, quiet: false);

        /// <summary>
        /// Waits on a syncpoint until it reaches a threshold.
        /// </summary>
        /// <param name="id">The id of the syncpoint</param>
        /// <param name="threshold">The threshold to wait for</param>
        /// <param name="timeout">The timeout</param>
        /// <param name="quiet">True if a timeout is expected and should not be logged</param>
        /// <returns>True if timed out</returns>
        public bool WaitOnSyncpoint(uint id, uint threshold, TimeSpan timeout, bool quiet)
        {
            ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(id, (uint)MaxHardwareSyncpoints);

            // TODO: Remove this when GPU channel scheduling will be implemented.
            if (timeout == Timeout.InfiniteTimeSpan)
            {
                timeout = TimeSpan.FromSeconds(1);
            }

            ManualResetEvent waitEvent = new(false);
            SyncpointWaiterHandle info = _syncpoints[id].RegisterCallback(threshold, _ => waitEvent.Set());

            if (info == null)
            {
                waitEvent.Dispose();

                return false;
            }

            bool signaled = waitEvent.WaitOne(timeout);

            // On a timeout, an increment may still run the callback after it is unregistered,
            // so the event is then left to the finalizer instead of being disposed under it.
            if (signaled)
            {
                waitEvent.Dispose();
            }

            if (!signaled)
            {
                if (!quiet)
                {
                    Logger.Error?.Print(LogClass.Gpu, $"Wait on syncpoint {id} for threshold {threshold} took more than {timeout.TotalMilliseconds}ms, resuming execution...");
                }

                _syncpoints[id].UnregisterCallback(info);
            }

            return !signaled;
        }
    }
}
