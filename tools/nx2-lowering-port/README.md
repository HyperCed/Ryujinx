# Switch 2 shader lowering port

`src/Ryujinx.Graphics.Gpu/Shader/Nx2/Nx2Lowering.cs` is a translation of Lichii's
`src/shader_recompiler/frontend/ampere/lower_to_maxwell.h` (Ampere SM86 instructions to Maxwell
instructions), made by these scripts and checked against the C++ original. To carry over later
changes of that file, translate it again rather than editing the C# by hand.

## Translating

From this directory, with `LICHII` pointing at a Lichii checkout:

    LICHII=/path/to/Lichii python3 tr.py      # mechanical C++ -> C# (body.cs)
    python3 tr2.py                            # C# file around it (Nx2Lowering.Main.cs)
    python3 fix.py                            # compiler-driven fixes (int/bool, shifts, casts)
    python3 manual.py                         # the hand-made fixes, by exact text
    cp Nx2Lowering.Main.cs ../../src/Ryujinx.Graphics.Gpu/Shader/Nx2/Nx2Lowering.cs

`fix.py` builds `compare/compare.csproj` repeatedly and rewrites what the errors point at. If the
C++ changed in ways the scripts don't cover, add the fix to `manual.py` so it stays repeatable.
`register_pressure.h` and `geometry_exit.h` were ported by hand (`Nx2RegisterPressure.cs`,
`Nx2GeometryExit.cs`).

## Checking

1. Dump a game's programs: run it with `RYUJINX_NX2_MAXWELL=1 RYUJINX_NX2_SHADER_DUMP=<dir>`.
2. Build the reference (needs only a C++20 compiler):

       clang++ -std=c++20 -O2 -Ireference/inc -I$LICHII/src reference/lowerref.cpp -o reference/lowerref

3. Lower every dump with it (`lowerref <program.bin> <geometry 0/1> <tessellation 0/1/2> <address>`,
   with the stage and address from the dump's name `<key>-<stage>-<address>.bin`) into
   `<ref>/<name>.txt`.
4. `dotnet run -c Release --project compare -- <dir> <ref>` must report every program the same.
   Mutated programs (random bits, injected opcodes) exercise the paths a game doesn't.
5. `dotnet run -c Release --project translate -- <dir>` lowers the dumps and runs them through the
   shader translator to SPIR-V, listing exceptions and translator messages.
