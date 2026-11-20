using System;

namespace Ryujinx.Graphics.Gpu.Engine.Threed
{
    /// <summary>
    /// Thrown while a Switch 2 draw is prepared, when it can't run (its shaders can't be translated yet).
    /// The draw is skipped; the methods around it still apply.
    /// </summary>
    class Nx2DrawSkippedException : Exception
    {
        public Nx2DrawSkippedException() : base("Switch 2 draw skipped") { }
    }
}
