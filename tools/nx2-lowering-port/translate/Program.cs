using Ryujinx.Graphics.Gpu.Shader.Nx2;
using Ryujinx.Graphics.Shader;
using Ryujinx.Graphics.Shader.Translation;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;

// xlate <shader dir> [name filter]: lowers every dumped program and translates it to SPIR-V.
string dir = args[0];
string filter = args.Length > 1 ? args[1] : null;
var messages = new Dictionary<string, int>();
int lowered = 0, translated = 0, failed = 0, rejected = 0;
foreach (string path in Directory.GetFiles(dir, "*.bin").Where(p => !p.EndsWith(".header.bin")).OrderBy(p => p))
{
    string name = Path.GetFileNameWithoutExtension(path);
    if (filter != null && !name.Contains(filter)) continue;
    string[] parts = name.Split('-');
    string stage = parts[1];
    Nx2Instruction[] code = MemoryMarshal.Cast<byte, Nx2Instruction>(File.ReadAllBytes(path)).ToArray();
    var r = Nx2Lowering.LowerToMaxwell(code, new Nx2LoweringOptions
    {
        Geometry = stage == "gs",
        Tensor = true,
        TessellationStage = stage == "tcs" ? Nx2TessellationStage.Control : stage == "tes" ? Nx2TessellationStage.Evaluation : Nx2TessellationStage.None,
        NativeCodeAddress = Convert.ToUInt64(parts[2], 16),
        VerifiedTraps = Enumerable.Range(0, code.Length).Where(i => code[i].Bits(0, 12) == 0x95c).Select(i => (uint)i * 16).ToArray(),
    });
    if (!r.Ok) { rejected++; continue; }
    lowered++;
    byte[] header = stage == "cs" ? [] : File.ReadAllBytes(Path.Combine(dir, name + ".header.bin"));
    byte[] buffer = Nx2LoweredCode.Build(header, r);
    var accessor = new Accessor(buffer, r.RequiredTextureBuffer);
    try
    {
        var options = new TranslationOptions(TargetLanguage.Spirv, TargetApi.Vulkan, stage == "cs" ? TranslationFlags.Compute : TranslationFlags.None);
        TranslatorContext context = Translator.CreateContext(0, accessor, options);
        ShaderProgram program = context.Translate();
        translated++;
    }
    catch (Exception e)
    {
        failed++;
        string key = "exception " + e.GetType().Name + ": " + e.Message;
        messages[key] = messages.GetValueOrDefault(key) + 1;
        if (Environment.GetEnvironmentVariable("VERBOSE") != null) Console.WriteLine($"{name}: {e}");
    }
    foreach (string m in accessor.Messages.Distinct())
    {
        messages[m] = messages.GetValueOrDefault(m) + 1;
        if (Environment.GetEnvironmentVariable("VERBOSE") != null) Console.WriteLine($"{name}: {m}");
    }
}
Console.WriteLine($"rejected by lowering {rejected}, lowered {lowered}, translated {translated}, exceptions {failed}");
foreach (var kv in messages.OrderByDescending(kv => kv.Value).Take(60)) Console.WriteLine($"{kv.Value,5} {kv.Key}");

sealed class Accessor : IGpuAccessor
{
    private readonly byte[] _code;
    private readonly uint _textureBuffer;
    private int _bindings;
    public readonly List<string> Messages = [];

    public Accessor(byte[] code, uint textureBuffer)
    {
        _code = code;
        _textureBuffer = textureBuffer;
    }

    public ReadOnlySpan<ulong> GetCode(ulong address, int minimumSize)
    {
        int start = (int)Math.Min(address, (ulong)_code.Length) & ~7;
        return MemoryMarshal.Cast<byte, ulong>(_code.AsSpan(start));
    }

    public void Log(string message) => Messages.Add(System.Text.RegularExpressions.Regex.Replace(message, "0x[0-9A-Fa-f]+|\\d+", "N"));
    public SetBindingPair CreateConstantBufferBinding(int index) => new(0, 1 + index);
    public SetBindingPair CreateImageBinding(int count, bool isBuffer) => new(3, _bindings++);
    public SetBindingPair CreateStorageBufferBinding(int index) => new(1, 1 + index);
    public SetBindingPair CreateTextureBinding(int count, bool isBuffer) => new(2, _bindings++);
    public int QuerySamplerArrayLengthFromPool() => 1;
    public int QueryTextureArrayLengthFromBuffer(int slot) => 1;
    public int QueryTextureArrayLengthFromPool() => 1;
    public int QueryTextureBufferIndex() => _textureBuffer != ~0U ? (int)_textureBuffer : 2;
}
