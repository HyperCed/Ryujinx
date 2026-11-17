// Reference driver: lowerref <native.bin> [geometry 0/1] [tess 0/1/2] [code address hex]
// prints "ok <source_size> <texbuf> <n>" then n lowered words in hex, or "error <offset> <reason>".
#include <cstdio>
#include <vector>
#include <string>
#include "shader_recompiler/frontend/ampere/instruction.h"
#include "shader_recompiler/frontend/ampere/lower_to_maxwell.h"
using namespace Shader::Ampere;
int main(int argc, char** argv) {
    FILE* f = std::fopen(argv[1], "rb"); if (!f) return 2;
    std::vector<Instruction> code; Instruction in;
    while (std::fread(&in, sizeof in, 1, f) == 1) code.push_back(in);
    std::fclose(f);
    LoweringOptions o{};
    o.geometry = argc > 2 && std::atoi(argv[2]);
    o.tensor = true;
    o.tessellation_stage = static_cast<TessellationStage>(argc > 3 ? std::atoi(argv[3]) : 0);
    o.native_code_address = argc > 4 ? std::strtoull(argv[4], nullptr, 16) : 0;
    std::vector<u32> traps;
    for (u32 i = 0; i < code.size(); ++i) if (code[i].Bits(0, 12) == 0x95c) traps.push_back(i * 16);
    o.verified_traps = traps;
    auto r = LowerToMaxwell(code, o);
    if (!r) { std::printf("error %x %.*s\n", r.error_offset, (int)r.error.size(), r.error.data()); return 0; }
    std::printf("ok %x %x %zu\n", r.source_size, r.required_texture_buffer, r.instructions.size());
    for (auto w : r.instructions) std::printf("%016llx\n", (unsigned long long)w);
    for (auto w : r.source_instruction_offsets) std::printf("m %x\n", w);
    return 0;
}
