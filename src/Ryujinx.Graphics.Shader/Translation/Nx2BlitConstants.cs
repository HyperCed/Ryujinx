using Ryujinx.Graphics.Shader.IntermediateRepresentation;
using System.Collections.Generic;
using static Ryujinx.Graphics.Shader.IntermediateRepresentation.OperandHelper;

namespace Ryujinx.Graphics.Shader.Translation
{
    // Port of Lichii's BlitConstantsPass (shader_recompiler/ir_opt/position_pass.cpp).

    /// <summary>
    /// Switch 2 fullscreen blit shaders read a parameter block from constant bank 24 (lowered to bank
    /// 17, which games never bind) that the console provides itself. For a blit covering the whole
    /// render target it holds: texel size (0x0/0x4, again at 0x10/0x14), size (0x8/0xc), a UV scale of
    /// 1 (0x18/0x1c), the UV clamp rectangle 0,0..1,1 (0x30-0x3c), a zero dither bias (0x40-0x48) and
    /// the integer source rectangle 0,0..W-1,H-1 (0x50-0x5c). The render area is the viewport size.
    /// </summary>
    static class Nx2BlitConstants
    {
        private const int Bank = 17;

        private static bool InBlitBlock(int offset) => offset < 0x20 || (offset >= 0x30 && offset < 0x4c) || (offset >= 0x50 && offset < 0x60);

        private static bool IsNonZeroWord(int offset) => offset < 0x20 || offset == 0x38 || offset == 0x3c || offset == 0x58 || offset == 0x5c;

        private static bool IsBlitRead(Operand operand, out int offset)
        {
            offset = 0;

            if (operand.Type != OperandType.ConstantBuffer || operand.GetCbufSlot() != Bank)
            {
                return false;
            }

            offset = operand.GetCbufOffset() * 4;

            return true;
        }

        public static void RunPass(BasicBlock[] blocks, ShaderStage stage)
        {
            if (stage != ShaderStage.Vertex && stage != ShaderStage.Fragment)
            {
                return;
            }

            // Bank 17 may also be a table the game loads itself: a shader reading it outside the blit
            // block keeps its reads, as does one reading only words the block leaves zero.
            bool readsNonZeroWord = false;

            foreach (BasicBlock block in blocks)
            {
                foreach (INode node in block.Operations)
                {
                    for (int index = 0; index < node.SourcesCount; index++)
                    {
                        if (IsBlitRead(node.GetSource(index), out int offset))
                        {
                            if (!InBlitBlock(offset))
                            {
                                return;
                            }

                            readsNonZeroWord |= IsNonZeroWord(offset);
                        }
                    }
                }
            }

            if (!readsNonZeroWord)
            {
                return;
            }

            foreach (BasicBlock block in blocks)
            {
                for (LinkedListNode<INode> node = block.Operations.First; node != null; node = node.Next)
                {
                    INode operation = node.Value;

                    for (int index = 0; index < operation.SourcesCount; index++)
                    {
                        if (IsBlitRead(operation.GetSource(index), out int offset))
                        {
                            operation.SetSource(index, EmitWord(node, offset));
                        }
                    }
                }
            }
        }

        private static Operand EmitWord(LinkedListNode<INode> node, int offset)
        {
            Operand Add(Operation operation)
            {
                node.List.AddBefore(node, operation);

                return operation.Dest;
            }

            Operand RenderArea(int component)
            {
                return Add(new Operation(
                    Instruction.Load,
                    StorageKind.ConstantBuffer,
                    Local(),
                    Const(SupportBuffer.Binding),
                    Const((int)SupportBufferField.ViewportSize),
                    Const(component)));
            }

            Operand Reciprocal(int component)
            {
                return Add(new Operation(Instruction.FP32 | Instruction.Divide, Local(), ConstF(1f), RenderArea(component)));
            }

            Operand LastTexel(int component)
            {
                Operand size = Add(new Operation(Instruction.FP32 | Instruction.Add, Local(), RenderArea(component), ConstF(-1f)));

                return Add(new Operation(Instruction.ConvertFP32ToU32, Local(), size));
            }

            return offset switch
            {
                0x0 or 0x10 => Reciprocal(0),
                0x4 or 0x14 => Reciprocal(1),
                0x8 => RenderArea(0),
                0xc => RenderArea(1),
                0x18 or 0x1c or 0x38 or 0x3c => ConstF(1f),
                0x58 => LastTexel(0),
                0x5c => LastTexel(1),
                _ => Const(0),
            };
        }
    }
}
