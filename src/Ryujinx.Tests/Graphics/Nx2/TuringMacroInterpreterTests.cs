using NUnit.Framework;
using Ryujinx.Graphics.Gpu.Engine.Nx2;
using System;
using System.Collections.Generic;

namespace Ryujinx.Tests.Graphics.Nx2
{
    class TuringMacroInterpreterTests
    {
        // Register numbers of the MME64 sources.
        private const uint Zero = 24;
        private const uint Imm = 25;
        private const uint Load0 = 28;

        // Output selectors.
        private const uint OutAlu0 = 1;
        private const uint OutImm1 = 6;

        private sealed class Lane
        {
            public uint Op;
            public uint Dst = Zero;
            public uint SrcA = Zero;
            public uint SrcB = Zero;
            public uint Imm;
            public uint OutMethod;
            public uint OutEmit;
        }

        // Builds one 96-bit instruction as the three words the GPU uploads (highest bits first).
        private static uint[] Instruction(Lane lane0, Lane lane1 = null, bool end = false)
        {
            lane1 ??= new Lane();

            UInt128 bits = 0;

            void Set(int low, uint value) => bits |= (UInt128)value << low;

            Set(0, end ? 1u : 0u);
            Set(10, lane0.Op);
            Set(15, lane0.Dst);
            Set(20, lane0.SrcA);
            Set(25, lane0.SrcB);
            Set(30, lane0.Imm);
            Set(46, lane1.Op);
            Set(51, lane1.Dst);
            Set(56, lane1.SrcA);
            Set(61, lane1.SrcB);
            Set(66, lane1.Imm);
            Set(82, lane0.OutMethod);
            Set(85, lane0.OutEmit);
            Set(89, lane1.OutMethod);
            Set(92, lane1.OutEmit);

            return [(uint)(bits >> 64), (uint)(bits >> 32), (uint)bits];
        }

        private static (TuringMacroInterpreter.Result, List<(uint, uint)>) Run(List<uint[]> program, List<uint> parameters, uint[] dataRam = null)
        {
            List<uint> code = [];

            foreach (uint[] instruction in program)
            {
                code.AddRange(instruction);
            }

            List<(uint, uint)> writes = [];
            TuringMacroInterpreter interpreter = new();

            TuringMacroInterpreter.Result result = interpreter.Execute(
                code.ToArray(),
                0,
                parameters,
                dataRam ?? new uint[TuringMacroInterpreter.DataRamSize],
                address => 0,
                (address, value) =>
                {
                    writes.Add((address, value));

                    return true;
                });

            return (result, writes);
        }

        private static readonly uint[] _nop = Instruction(new Lane());

        [Test]
        public void EmitsAluResultToImmediateMethod()
        {
            // r0 = 5 + 0, method = 0x123, emit r0; END runs one more instruction.
            List<uint[]> program =
            [
                Instruction(new Lane { Op = 0, Dst = 0, SrcA = Imm, Imm = 5, OutMethod = OutImm1, OutEmit = OutAlu0 }, new Lane { Imm = 0x123 }, end: true),
                _nop,
            ];

            (TuringMacroInterpreter.Result result, List<(uint, uint)> writes) = Run(program, []);

            Assert.That(result.Success, Is.True, result.Error);
            Assert.That(writes, Is.EqualTo(new List<(uint, uint)> { (0x123, 5) }));
        }

        [Test]
        public void ReadsParametersFromTheLoadFifo()
        {
            List<uint[]> program =
            [
                Instruction(new Lane { Op = 0, Dst = 0, SrcA = Load0, OutMethod = OutImm1, OutEmit = OutAlu0 }, new Lane { Imm = 0x200 }),
                Instruction(new Lane { Op = 0, Dst = 1, SrcA = Load0, SrcB = 0, OutEmit = OutAlu0 }, end: true),
                _nop,
            ];

            (TuringMacroInterpreter.Result result, List<(uint, uint)> writes) = Run(program, [7, 10]);

            Assert.That(result.Success, Is.True, result.Error);
            Assert.That(result.Consumed, Is.EqualTo(2));

            // The second emit goes to the method after the first one (increment 0 keeps it).
            Assert.That(writes, Is.EqualTo(new List<(uint, uint)> { (0x200, 7), (0x200, 17) }));
        }

        [Test]
        public void UnderflowingTheFifoFails()
        {
            List<uint[]> program =
            [
                Instruction(new Lane { Op = 0, Dst = 0, SrcA = Load0 }, end: true),
                _nop,
            ];

            (TuringMacroInterpreter.Result result, _) = Run(program, []);

            Assert.That(result.Success, Is.False);
            Assert.That(result.Error, Is.EqualTo("parameter FIFO underflow"));
        }

        [Test]
        public void DataRamWritesAndReadsBack()
        {
            uint[] dataRam = new uint[TuringMacroInterpreter.DataRamSize];

            // DWRITE dram[0x27] = 0x1234, then DREAD it into r0 and emit it.
            List<uint[]> program =
            [
                Instruction(new Lane { Op = 0, Dst = 1, SrcA = Imm, Imm = 0x1234 }),
                Instruction(new Lane { Op = 31, SrcA = Imm, SrcB = 1, Imm = 0x27 }),
                Instruction(new Lane { Op = 30, Dst = 0, SrcA = Imm, Imm = 0x27, OutMethod = OutImm1, OutEmit = OutAlu0 }, new Lane { Imm = 0x10 }, end: true),
                _nop,
            ];

            (TuringMacroInterpreter.Result result, List<(uint, uint)> writes) = Run(program, [], dataRam);

            Assert.That(result.Success, Is.True, result.Error);
            Assert.That(dataRam[0x27], Is.EqualTo(0x1234u));
            Assert.That(writes, Is.EqualTo(new List<(uint, uint)> { (0x10, 0x1234) }));
        }

        [Test]
        public void ZeroCountLoopSkipsItsBody()
        {
            // LOOP with a count of 0 over the next instruction, which would emit.
            List<uint[]> program =
            [
                Instruction(new Lane { Op = 23, SrcA = Zero, Imm = 2 }),
                Instruction(new Lane { Op = 0, Dst = 0, SrcA = Imm, Imm = 1, OutMethod = OutImm1, OutEmit = OutAlu0 }, new Lane { Imm = 0x10 }),
                Instruction(new Lane(), end: true),
                _nop,
            ];

            (TuringMacroInterpreter.Result result, List<(uint, uint)> writes) = Run(program, []);

            Assert.That(result.Success, Is.True, result.Error);
            Assert.That(writes, Is.Empty);
        }
    }
}
