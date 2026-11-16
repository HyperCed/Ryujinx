using System;
using System.Collections.Generic;

namespace Ryujinx.Graphics.Gpu.Engine.Nx2
{
    /// <summary>
    /// QMD 3.0 dependency scheduling for the Switch 2 compute class. A queue meta data block launches once
    /// it is scheduled and its dependency count reaches zero; completing it fires its releases, which other
    /// queues and the CPU wait on, and may signal a dependent QMD.
    /// </summary>
    class Nx2QmdScheduler
    {
        public const int DescriptorWords = 64;

        // Release slots (address low, flags | address high, payload). NVN2 patches a third one at word 52
        // for queue syncs.
        private static readonly int[] _releaseSlots = [24, 28, 52];

        private sealed class State
        {
            public int Dependencies;
            public int Deferred;
            public bool Loaded;
            public bool Scheduled;
            public bool Completed;
        }

        // One table for every channel: a QMD's dependency counter is state of the QMD itself, and a QMD is
        // scheduled on one channel while another decrements it.
        private readonly Dictionary<ulong, State> _states = [];

        /// <summary>
        /// Semaphore reduction operations (ADD, MIN, MAX, INC, DEC, AND, OR, XOR) shared by QMD releases
        /// and the report semaphore.
        /// </summary>
        public static uint ReduceSemaphore(uint op, bool isSigned, uint value, uint payload)
        {
            return op switch
            {
                0 => value + payload,
                1 => isSigned ? (uint)Math.Min((int)value, (int)payload) : Math.Min(value, payload),
                2 => isSigned ? (uint)Math.Max((int)value, (int)payload) : Math.Max(value, payload),
                3 => value >= payload ? 0 : value + 1,
                4 => value == 0 || value > payload ? payload : value - 1,
                5 => value & payload,
                6 => value | payload,
                _ => value ^ payload,
            };
        }

        // QMD V03_00 release flags: 7:0 address high, 19 membar, 22:20 reduction op, 23 enable, 25:24
        // reduction format, 26 reduction enable, 28:27 non-blocking interrupt, 29 64-bit payload, 31:30
        // structure size (0 four words with timestamp, 1 one word, 2 two words).
        private static bool IsDecodableRelease(uint flags)
        {
            uint size = flags >> 30;

            if ((flags & (1u << 29)) != 0)
            {
                return false;
            }

            if ((flags & (1u << 26)) != 0)
            {
                return size == 1 && ((flags >> 24) & 3) < 2;
            }

            return size <= 1;
        }

        /// <summary>
        /// Handles a PCAS signal on a QMD.
        /// </summary>
        /// <param name="address">GPU virtual address of the QMD</param>
        /// <param name="argument">The PCAS method argument: action in bits 3:0, count - 1 in bits 23:14</param>
        /// <param name="read">Reads a QMD; returns false if it is not mapped</param>
        /// <param name="release">Fires a release (address, payload, flags); returns false if it is not mapped</param>
        /// <param name="dispatch">Runs a QMD with a non-empty grid; returns false if it can't</param>
        /// <returns>Null on success, or what went wrong</returns>
        public string Signal(ulong address, uint argument, Func<ulong, uint[], bool> read, Func<ulong, uint, uint, bool> release, Func<uint[], bool> dispatch)
        {
            lock (_states)
            {
                uint[] qmd = new uint[DescriptorWords];
                string error = null;
                List<(ulong Address, uint Action, uint Count)> pending = [(address, argument & 15, ((argument >> 14) & 0x3ff) + 1)];

                for (int i = 0; i < pending.Count; i++)
                {
                    (ulong requestAddress, uint action, uint count) = pending[i];

                    if (_states.Count >= 4096 && !_states.ContainsKey(requestAddress))
                    {
                        // Finished QMDs with nothing deferred are only history.
                        List<ulong> finished = [];

                        foreach ((ulong key, State kept) in _states)
                        {
                            if (kept.Completed && kept.Deferred == 0)
                            {
                                finished.Add(key);
                            }
                        }

                        foreach (ulong key in finished)
                        {
                            _states.Remove(key);
                        }

                        if (_states.Count >= 4096)
                        {
                            return "QMD cache limit";
                        }
                    }

                    if (!_states.TryGetValue(requestAddress, out State state))
                    {
                        _states[requestAddress] = state = new State();
                    }

                    if (action is 3 or 2)
                    {
                        if (action == 3 || !state.Loaded)
                        {
                            if (!read(requestAddress, qmd))
                            {
                                return "unmapped QMD";
                            }

                            if ((qmd[18] & 0xff) != 0x30 || (qmd[4] & 0x100) != 0)
                            {
                                return "unsupported QMD version or queue";
                            }

                            state.Dependencies = (int)((qmd[4] >> 14) & 0xffff) + state.Deferred;
                            state.Deferred = 0;
                            state.Loaded = true;
                            state.Completed = false;
                        }

                        state.Scheduled = true;
                    }
                    else if (action is 7 or 12)
                    {
                        int delta = action == 7 ? -(int)count : (int)count;

                        if (!state.Loaded || state.Completed)
                        {
                            state.Deferred += delta;

                            continue;
                        }

                        state.Dependencies += delta;
                    }
                    else if (action is 0 or 1)
                    {
                        continue;
                    }
                    else
                    {
                        return "unsupported PCAS action";
                    }

                    if (!state.Loaded || !state.Scheduled || state.Completed || state.Dependencies > 0)
                    {
                        continue;
                    }

                    if (state.Dependencies < 0)
                    {
                        return "QMD dependency underflow";
                    }

                    // The SDK patches release payloads while a scheduled QMD is waiting for its remaining
                    // dependencies: fetch them at execution.
                    if (!read(requestAddress, qmd))
                    {
                        return "unmapped QMD";
                    }

                    if ((qmd[18] & 0xff) != 0x30 || (qmd[4] & 0x100) != 0)
                    {
                        return "unsupported QMD version or queue";
                    }

                    // A program that can't run is skipped, but the QMD still completes: its releases are
                    // what other queues and the CPU wait on (the console never fails a launch).
                    bool hasGrid = qmd[12] != 0 && (qmd[13] & 0xffff) != 0 && (qmd[14] & 0xffff) != 0;

                    if (hasGrid && !dispatch(qmd))
                    {
                        error = "native shader dispatch required";
                    }

                    state.Completed = true;

                    foreach (int slot in _releaseSlots)
                    {
                        uint flags = qmd[slot + 1];

                        if ((flags & (1u << 23)) == 0)
                        {
                            continue;
                        }

                        // Skip only this slot: the others are what other queues and the CPU wait on.
                        if (!IsDecodableRelease(flags))
                        {
                            error ??= "unsupported QMD release format";

                            continue;
                        }

                        ulong destination = ((ulong)(flags & 0xff) << 32) | qmd[slot];

                        if (!release(destination, qmd[slot + 2], flags))
                        {
                            return "unmapped QMD release";
                        }
                    }

                    if ((qmd[16] & 1) != 0)
                    {
                        uint dependentAction = (qmd[16] >> 1) & 7;

                        if (dependentAction is not (4 or 3 or 1))
                        {
                            return "unsupported dependent QMD action";
                        }

                        if (pending.Count == 256)
                        {
                            return "QMD chain limit";
                        }

                        pending.Add(((ulong)qmd[15] << 8, dependentAction == 4 ? 7u : dependentAction == 3 ? 3u : 2u, 1));
                    }

                    if ((qmd[16] & (1u << 5)) != 0)
                    {
                        return "second dependent QMD unsupported";
                    }
                }

                return error;
            }
        }
    }
}
