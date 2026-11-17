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
