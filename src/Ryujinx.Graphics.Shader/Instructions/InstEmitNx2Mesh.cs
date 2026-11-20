using Ryujinx.Graphics.Shader.IntermediateRepresentation;
using Ryujinx.Graphics.Shader.Translation;
using static Ryujinx.Graphics.Shader.Instructions.InstEmitHelper;
using static Ryujinx.Graphics.Shader.IntermediateRepresentation.OperandHelper;

namespace Ryujinx.Graphics.Shader.Instructions
{
    static partial class InstEmit
    {
        /// <summary>
        /// Kind of a stage buffer write (source 0 of <see cref="Instruction.Nx2IsbeWrite"/>).
        /// </summary>
        public const int Nx2IsbeAttribute = 0;
        public const int Nx2IsbeMapU8 = 1;
        public const int Nx2IsbeMapU32 = 2;

        // nx2 pseudo instruction (Maxwell 0xefc0) for the ISBERD/ISBEWR of Switch 2 task and mesh programs
        // (see Nx2Lowering): data/dest 0-7, Ra 8-15, offset 20-43, .32 44, .ATTR 45, .SKEW 46, .O 47,
        // write 48. Addresses are linear bytes in the stage buffer: reads come from the input attribute
        // region, writes go to the output attribute or map region. Port of Lichii's ISBE_nx2.
        public static void IsbeNx2(EmitterContext context)
        {
            ulong raw = context.CurrOp.RawOpCode;

            int reg = (int)(raw & 0xff);
            int addressReg = (int)((raw >> 8) & 0xff);
            int offset = (int)((raw >> 20) & 0xffffff);
            bool is32 = ((raw >> 44) & 1) != 0;
            bool attribute = ((raw >> 45) & 1) != 0;
            bool write = ((raw >> 48) & 1) != 0;

            ShaderStage stage = context.TranslatorContext.Definitions.Stage;

            if (stage is not (ShaderStage.Task or ShaderStage.Mesh))
            {
                context.TranslatorContext.GpuAccessor.Log("Stage buffer access outside task and mesh programs.");

                return;
            }

            Operand address = context.IAdd(GetSrcReg(context, addressReg), Const(offset));

            if (!write)
            {
                context.Copy(GetDest(reg), context.Add(Instruction.Nx2IsbeRead, Local(), address));

                return;
            }

            Operand value = GetSrcReg(context, reg);

            int kind;

            if (attribute)
            {
                if (!is32)
                {
                    context.TranslatorContext.GpuAccessor.Log("U8 stage buffer attribute write.");

                    return;
                }

                kind = Nx2IsbeAttribute;
            }
            else
            {
                kind = is32 ? Nx2IsbeMapU32 : Nx2IsbeMapU8;
            }

            context.Add(Instruction.Nx2IsbeWrite, null, Const(kind), address, value);
        }
    }
}
