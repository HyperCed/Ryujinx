using Ryujinx.Graphics.Gpu.Shader.Nx2;
using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;

// diff harness: proj <shader dir> <ref dir>
string dir = args[0], refDir = args[1];
int same = 0, diff = 0, crash = 0;
foreach (string path in Directory.GetFiles(dir, "*.bin").Where(p => !p.EndsWith(".header.bin")).OrderBy(p => p))
{
    string name = Path.GetFileNameWithoutExtension(path);
    string[] parts = name.Split('-');
    Nx2Instruction[] code = MemoryMarshal.Cast<byte, Nx2Instruction>(File.ReadAllBytes(path)).ToArray();
    var options = new Nx2LoweringOptions
    {
        Geometry = parts[1] == "gs",
        Tensor = true,
        TessellationStage = parts[1] == "tcs" ? Nx2TessellationStage.Control : parts[1] == "tes" ? Nx2TessellationStage.Evaluation : Nx2TessellationStage.None,
        NativeCodeAddress = Convert.ToUInt64(parts[2], 16),
        VerifiedTraps = Enumerable.Range(0, code.Length).Where(i => code[i].Bits(0, 12) == 0x95c).Select(i => (uint)i * 16).ToArray(),
    };
    string output;
    try
    {
        var r = Nx2Lowering.LowerToMaxwell(code, options);
        output = r.Ok
            ? $"ok {r.SourceSize:x} {r.RequiredTextureBuffer:x} {r.Instructions.Count}\n" + string.Concat(r.Instructions.Select(w => $"{w:x16}\n")) + string.Concat(r.SourceInstructionOffsets.Select(w => $"m {w:x}\n"))
            : $"error {r.ErrorOffset:x} {r.Error}\n";
    }
    catch (Exception e)
    {
        output = $"crash {e.GetType().Name} {e.Message}\n{e.StackTrace}\n";
        crash++;
    }
    string expected = File.ReadAllText(Path.Combine(refDir, name + ".txt"));
    if (output == expected) same++;
    else
    {
        diff++;
        if (diff <= 5 || Environment.GetEnvironmentVariable("ALL") != null)
        {
            string[] a = expected.Split('\n'), b = output.Split('\n');
            int first = 0;
            while (first < a.Length && first < b.Length && a[first] == b[first]) first++;
            Console.WriteLine($"{name}: line {first}: expected '{(first < a.Length ? a[first] : "")}' got '{(first < b.Length ? b[first] : "")}' (ref head '{a[0]}', ours '{b[0]}')");
            if (output.StartsWith("crash")) Console.WriteLine(output.Split('\n').Take(4).Aggregate((x, y) => x + "\n" + y));
        }
    }
}
Console.WriteLine($"same {same}, different {diff} (crashes {crash})");
