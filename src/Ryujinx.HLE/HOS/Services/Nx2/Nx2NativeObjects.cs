using System.Collections.Generic;

namespace Ryujinx.HLE.HOS.Services.Nx2
{
    // Native memories and images a graphics system session created, shared with its vi2 session.
    class Nx2NativeObjects
    {
        public class Memory
        {
            public ulong Size;
            public ulong Address; // CPU (== GPU) address once bound by 114/423
            public uint Parent;
            public ulong ParentOffset;
        }

        public class Image
        {
            public uint MemoryId;
            public uint[] Preference = new uint[16];
            public ulong Size;
            public uint Count = 1; // Images in the pool; handle id + i is image i
            public ulong Offset;
            public bool WindowPool;
            public bool Decoder; // A video decoder output image (gsv 911), one image, no pool
        }

        public readonly object Lock = new();
        public readonly Dictionary<uint, Memory> Memories = [];
        public readonly SortedDictionary<uint, Image> Images = [];
        public uint NextMemory = 1;
        public uint NextImage = 1;

        // Finds the pool that holds an image handle; returns its first handle through `first`.
        public Image FindImage(uint handle, out uint first)
        {
            first = 0;
            Image found = null;

            foreach ((uint id, Image image) in Images)
            {
                if (id > handle)
                {
                    break;
                }

                if (handle - id < image.Count)
                {
                    first = id;
                    found = image;
                }
            }

            return found;
        }
    }
}
