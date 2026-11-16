using ARMeilleure.Decoders;
using ARMeilleure.Instructions;
using NUnit.Framework;

namespace Ryujinx.Tests.Cpu
{
    class Aarch64DecodeTests
    {
        [TestCase(unchecked((int)0xD65F0BFF))]
        [TestCase(unchecked((int)0xD65F0FFF))]
        public void RetAuthEncoding(int instruction)
        {
            (InstDescriptor descriptor, OpCodeTable.MakeOp makeOp) = OpCodeTable.GetInstA64(instruction);

            Assert.AreEqual(InstName.RetAuth, descriptor.Name);
            Assert.IsNotNull(makeOp);

            OpCode opCode = makeOp(descriptor, 0, instruction);

            Assert.AreEqual(InstName.RetAuth, opCode.Instruction.Name);
        }

        [Test]
        public void LdaprRegisterEncoding()
        {
            const int instruction = unchecked((int)0xF8BFC020);

            (InstDescriptor descriptor, OpCodeTable.MakeOp makeOp) = OpCodeTable.GetInstA64(instruction);

            Assert.AreEqual(InstName.Ldapr, descriptor.Name);
            Assert.IsNotNull(makeOp);

            OpCodeMemEx opCode = (OpCodeMemEx)makeOp(descriptor, 0, instruction);

            Assert.AreEqual(3, opCode.Size);
            Assert.AreEqual(1, opCode.Rn);
            Assert.AreEqual(0, opCode.Rt);
        }

        [Test]
        public void LduminalRegisterEncoding()
        {
            const int instruction = unchecked((int)0x88E87C14);

            (InstDescriptor descriptor, OpCodeTable.MakeOp makeOp) = OpCodeTable.GetInstA64(instruction);

            Assert.AreEqual(InstName.Lduminal, descriptor.Name);
            Assert.IsNotNull(makeOp);

            OpCodeMemEx opCode = (OpCodeMemEx)makeOp(descriptor, 0, instruction);

            Assert.AreEqual(2, opCode.Size);
            Assert.AreEqual(0, opCode.Rn);
            Assert.AreEqual(8, opCode.Rs);
            Assert.AreEqual(20, opCode.Rt);
        }
    }
}
