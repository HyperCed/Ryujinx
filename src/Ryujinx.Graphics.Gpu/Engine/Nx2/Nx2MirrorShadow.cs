using System;
using System.Collections.Generic;

namespace Ryujinx.Graphics.Gpu.Engine.Nx2
{
    /// <summary>
    /// Switch 2 queues mirror their releases from the second page of a semaphore region into the first
    /// (see <see cref="GpuContext.Nx2TryGetSemaphoreMirror"/>). Some first-page words also get direct
    /// releases of their own counter (a QMD ring's completion count): a mirrored release into such a
    /// word would show the guest CPU another counter's progress, so its waits would end before the
    /// work they wait for has run (the title then resets semaphores channels still wait on). Mirrored
    /// releases into those words go to this table instead, and only GPU acquires read it. Words only
    /// the mirror writes keep getting guest memory writes. Keyed by GPU virtual address.
    /// Port of Lichii's Nx2MirrorShadow (video_core/nx2.h).
    /// </summary>
    static class Nx2MirrorShadow
    {
        private const int MaxDirectWords = 1024;

        private readonly struct Word
        {
            public readonly uint Value;
            public readonly uint Guest;

            public Word(uint value, uint guest)
            {
                Value = value;
                Guest = guest;
            }
        }

        private static readonly object _lock = new();
        private static readonly HashSet<ulong> _directWords = [];
        private static readonly Dictionary<ulong, Word> _words = [];
        private static volatile bool _hasDirect;
        private static volatile bool _hasWords;

        /// <summary>
        /// A release that is not the mirror's landed on a first-page word.
        /// </summary>
        public static void NoteDirectRelease(ulong address)
        {
            ulong word = address & ~3UL;

            lock (_lock)
            {
                if (_directWords.Count < MaxDirectWords)
                {
                    _directWords.Add(word);
                    _hasDirect = true;
                }

                _words.Remove(word);
            }
        }

        /// <summary>
        /// A direct release that combines with the word it lands on (a QMD max or a reduction) took
        /// guest memory from <paramref name="guestBefore"/> to <paramref name="guestAfter"/>. While the
        /// word holds a mirrored value, GPU acquires see what the word would hold without the table.
        /// </summary>
        /// <returns>What GPU acquires see</returns>
        public static uint NoteCombinedRelease(ulong address, uint guestBefore, uint guestAfter, Func<uint, uint> combine)
        {
            uint? mirrored = Read(address, guestBefore);

            NoteDirectRelease(address);

            if (mirrored is not uint value)
            {
                return guestAfter;
            }

            uint seen = combine(value);

            Set(address, seen, guestAfter);

            return seen;
        }

        /// <summary>
        /// Whether a mirrored release into this word goes to the table instead of guest memory.
        /// </summary>
        public static bool Diverts(ulong address)
        {
            if (!_hasDirect)
            {
                return false;
            }

            lock (_lock)
            {
                return _directWords.Contains(address & ~3UL);
            }
        }

        /// <param name="address">Address of the word</param>
        /// <param name="value">Value GPU acquires see</param>
        /// <param name="guest">What guest memory holds at the word now</param>
        public static void Set(ulong address, uint value, uint guest)
        {
            lock (_lock)
            {
                _words[address & ~3UL] = new Word(value, guest);
                _hasWords = true;
            }
        }

        /// <summary>
        /// The mirrored value of a word whose guest memory holds <paramref name="guest"/>, or null.
        /// Once the guest word changed (a direct release or a CPU write), it is newer than the mirror.
        /// </summary>
        public static uint? Read(ulong address, uint guest)
        {
            if (!_hasWords)
            {
                return null;
            }

            lock (_lock)
            {
                ulong word = address & ~3UL;

                if (!_words.TryGetValue(word, out Word entry))
                {
                    return null;
                }

                if (entry.Guest != guest)
                {
                    _words.Remove(word);

                    return null;
                }

                return entry.Value;
            }
        }
    }
}
