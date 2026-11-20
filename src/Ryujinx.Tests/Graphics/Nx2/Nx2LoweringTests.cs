using NUnit.Framework;
using Ryujinx.Graphics.Gpu.Shader.Nx2;
using System;
using System.Runtime.InteropServices;

namespace Ryujinx.Tests.Graphics.Nx2
{
    // Expected words come from Lichii's C++ lowering (tools/nx2-lowering-port/reference).
    public class Nx2LoweringTests
    {
        private static readonly Nx2Instruction _exit = new(0x794d, 0, 0x03800000, 0);

        private static Nx2LoweredProgram Lower(params Nx2Instruction[] code)
        {
            return Nx2Lowering.LowerToMaxwell(code, new Nx2LoweringOptions { Tensor = true, VerifiedTraps = [] });
        }

        [Test]
        public void ExitLowersToMaxwellExit()
        {
            Nx2LoweredProgram lowered = Lower(_exit);

            Assert.That(lowered.Ok, Is.True, lowered.Error);
            Assert.That(lowered.Instructions, Is.EqualTo(new[] { 0xe30000000007000fUL }));
            Assert.That(lowered.SourceSize, Is.EqualTo(0x10));
            Assert.That(lowered.RequiredTextureBuffer, Is.EqualTo(~0U));
        }

        [Test]
        public void MoveImmediateLowersToMaxwellMove()
        {
            Nx2LoweredProgram lowered = Lower(new Nx2Instruction(0x00017802, 0x3f800000, 0x00000f00, 0), _exit);

            Assert.That(lowered.Ok, Is.True, lowered.Error);
            Assert.That(lowered.Instructions, Is.EqualTo(new[] { 0x0103f8000007f001UL, 0xe30000000007000fUL }));
            Assert.That(lowered.SourceInstructionOffsets, Is.EqualTo(new[] { 0U, 1U }));
        }

        [Test]
        public void SurfaceReductionsPreserveThreeDimensionalCoordinates()
        {
            Nx2Instruction sured = new(0x0000759f, 0xa0002203, 0x0010e000, 0x0011e600);

            foreach (uint dimension in new uint[] { 3, 5 })
            {
                sured.W1 = (sured.W1 & 0x1fffffffU) | (dimension << 29);
                Nx2LoweredProgram lowered = Lower(sured, _exit);

                Assert.That(lowered.Ok, Is.True, lowered.Error);
                Assert.That(lowered.RequiredTextureBuffer, Is.EqualTo(0U));
                Assert.That(lowered.Instructions, Has.Count.EqualTo(2));

                ulong atomic = lowered.Instructions[0];
                Assert.That(atomic & 0xfff0000000000000UL, Is.EqualTo(0xeb40000000000000UL));
                Assert.That(atomic & 0xff, Is.EqualTo(3UL));
                Assert.That((atomic >> 8) & 0xff, Is.EqualTo(0UL));
                Assert.That((atomic >> 16) & 15, Is.EqualTo(7UL));
                Assert.That((atomic >> 20) & 7, Is.EqualTo(0UL));
                Assert.That((atomic >> 24) & 7, Is.EqualTo(0UL));
                Assert.That((atomic >> 33) & 7, Is.EqualTo(dimension));
                Assert.That((atomic >> 36) & 0x1fff, Is.EqualTo(0x22UL));
                Assert.That((atomic >> 51) & 1, Is.EqualTo(1UL));
            }

            sured = new Nx2Instruction(0x0000759f, 0xa0002403, 0x0310e000, 0x000fe200);
            Nx2LoweredProgram orReduction = Lower(sured, _exit);

            Assert.That(orReduction.Ok, Is.True, orReduction.Error);
            Assert.That((orReduction.Instructions[0] >> 24) & 7, Is.EqualTo(6UL));
            Assert.That((orReduction.Instructions[0] >> 33) & 7, Is.EqualTo(5UL));
        }

        [Test]
        public void SurfaceAtomicAddReturnsTheOldThreeDimensionalTexelValue()
        {
            Nx2Instruction atomic = new(0x00087593, 0xa0002407, 0x001ee000, 0x000ea400);
            Nx2LoweredProgram lowered = Lower(atomic, _exit);

            Assert.That(lowered.Ok, Is.True, lowered.Error);
            Assert.That(lowered.RequiredTextureBuffer, Is.EqualTo(0U));
            Assert.That(lowered.Instructions, Has.Count.EqualTo(2));

            ulong word = lowered.Instructions[0];
            Assert.That(word & 0xff80000000000000UL, Is.EqualTo(0xea00000000000000UL));
            Assert.That(word & 255, Is.EqualTo(8UL));
            Assert.That((word >> 8) & 255, Is.EqualTo(0UL));
            Assert.That((word >> 20) & 255, Is.EqualTo(7UL));
            Assert.That((word >> 29) & 15, Is.EqualTo(0UL));
            Assert.That((word >> 33) & 7, Is.EqualTo(5UL));
            Assert.That((word >> 36) & 0x1fff, Is.EqualTo(0x24UL));
            Assert.That((word >> 51) & 7, Is.EqualTo(0UL));
            Assert.That((word >> 54) & 1, Is.EqualTo(0UL));

            atomic.W2 &= ~(7U << 17);
            Assert.That(Lower(atomic, _exit).Ok, Is.False);
        }

        [Test]
        public void GradientTextureSupportsUniformArrayHandles()
        {
            Nx2Instruction txd = new(0x14127f6c, 0xa0000410, 0x081e0f10, 0x001f4200);
            Nx2LoweredProgram lowered = Lower(txd, _exit);

            Assert.That(lowered.Ok, Is.True, lowered.Error);
            Assert.That(lowered.RequiredTextureBuffer, Is.EqualTo(~0U));
            Assert.That(lowered.Instructions, Has.Count.EqualTo(10));
            Assert.That(lowered.Instructions[0], Is.EqualTo(0x5c9807800dc700f8UL));

            ulong texture = lowered.Instructions[4];
            Assert.That(texture & 0xffc0000000000000UL, Is.EqualTo(0xde40000000000000UL));
            Assert.That((texture >> 8) & 0xff, Is.EqualTo(248UL));
            Assert.That((texture >> 20) & 0xff, Is.EqualTo(16UL));
            Assert.That((texture >> 28) & 7, Is.EqualTo(3UL));
            Assert.That((texture >> 31) & 15, Is.EqualTo(15UL));
            Assert.That(lowered.Instructions[5] & 0xff, Is.EqualTo(18UL));
            Assert.That(lowered.Instructions[7] & 0xff, Is.EqualTo(16UL));

            txd = new Nx2Instruction(0x18320f6c, 0xa000041c, 0x0c1e0f34, 0x00414200);
            lowered = Lower(txd, _exit);

            Assert.That(lowered.Ok, Is.True, lowered.Error);
            Assert.That((lowered.Instructions[4] >> 49) & 1, Is.EqualTo(1UL));
            Assert.That((lowered.Instructions[4] >> 16) & 15, Is.EqualTo(0UL));

            txd.W1 = 0xa0004020;
            Assert.That(Lower(txd, _exit).Ok, Is.False);
        }

        [Test]
        public void ArrayGatherPreservesLayerAndSplitCoordinates()
        {
            Nx2Instruction gather = new(0x201c7b63, 0xb0000a2b, 0x041e0f16, 0x000f4200);
            Nx2LoweredProgram lowered = Lower(gather, _exit);

            Assert.That(lowered.Ok, Is.True, lowered.Error);
            Assert.That((lowered.Instructions[0] >> 20) & 0xff, Is.EqualTo(32UL));
            Assert.That((lowered.Instructions[1] >> 20) & 0xff, Is.EqualTo(33UL));
            Assert.That((lowered.Instructions[2] >> 20) & 0xff, Is.EqualTo(43UL));
            Assert.That((lowered.Instructions[3] >> 28) & 7, Is.EqualTo(3UL));
        }

        [Test]
        public void UniformMultiplyAcceptsImmediateAddend()
        {
            Nx2Instruction multiply = new(0x040474a4, 0x00000001, 0x0f8e0205, 0x000fe200);
            Nx2LoweredProgram lowered = Lower(multiply, _exit);

            Assert.That(lowered.Ok, Is.True, lowered.Error);
            Assert.That(lowered.Instructions[^2] & 255, Is.EqualTo(220UL));
        }

        [Test]
        public void ProgramWithoutExitIsRejected()
        {
            Nx2LoweredProgram lowered = Lower(new Nx2Instruction(0x00017802, 0x3f800000, 0x00000f00, 0));

            Assert.That(lowered.Ok, Is.False);
            Assert.That(lowered.Error, Is.EqualTo("native program has no unconditional EXIT"));
        }

        [Test]
        public void CaptureEndsAtTheLastExit()
        {
            Nx2ProgramCapture capture = new();

            Assert.That(capture.Observe(new Nx2Instruction(0x00017802, 0x3f800000, 0x00000f00, 0), 0), Is.EqualTo(Nx2ProgramCapture.Result.Continue));
            Assert.That(capture.Observe(_exit, 1), Is.EqualTo(Nx2ProgramCapture.Result.Complete));
        }

        [Test]
        public void LoweredCodeKeepsSchedulingWordsWhereTheTranslatorSkipsThem()
        {
            Nx2LoweredProgram lowered = new();

            for (ulong i = 1; i <= 7; i++)
            {
                lowered.Instructions.Add(i);
            }

            byte[] header = new byte[Nx2LoweredCode.HeaderSize];
            header[0] = 0x42;

            ReadOnlySpan<ulong> code = MemoryMarshal.Cast<byte, ulong>(Nx2LoweredCode.Build(header, lowered));
            ReadOnlySpan<ulong> body = code[(Nx2LoweredCode.HeaderSize / 8)..];

            Assert.That(code[0], Is.EqualTo(0x42UL));

            // A scheduling word every 32 bytes from the start of the code, a NOP, then the lowered
            // instructions as the lowering places them (scheduling words at 32-byte program offsets).
            Assert.That(body.ToArray(), Is.EqualTo(new ulong[] { 0, 0x50b0000000070f00UL, 1, 2, 0, 3, 4, 5, 0, 6, 7, 0 }));
        }
    }
}
