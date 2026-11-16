using NUnit.Framework;

namespace Ryujinx.Tests.Cpu
{
    [Category("Atomic")]
    public sealed class CpuTestAtomic : CpuTest
    {
        [TestCase(0x80000000ul, 0x7FFFFFFFul, 0x80000000ul)]
        [TestCase(0x00000005ul, 0xFFFFFFFFul, 0x00000005ul)]
        public void Lduminal_32bit(ulong initial, ulong value, ulong expectedOldValue)
        {
            SetWorkingMemory(0, System.BitConverter.GetBytes(initial));
            SetContext(x0: DataBaseAddress, x1: value);

            Opcode(0xAA0103E8); // MOV X8, X1
            Opcode(0x88E87C14); // LDUMINAL W20, W8, [X0]
            Opcode(0xD65F03C0); // RET
            ExecuteOpcodes();

            CompareAgainstUnicorn();
            Assert.That(GetContext().GetX(20), Is.EqualTo(expectedOldValue));
        }
    }
}
