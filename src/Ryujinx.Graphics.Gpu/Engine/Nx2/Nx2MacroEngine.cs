namespace Ryujinx.Graphics.Gpu.Engine.Nx2
{
    /// <summary>
    /// Macro code a Switch 2 channel uploaded. The method macro expander belongs to the channel: its 3D
    /// and compute classes run the same code and share the data RAM.
    /// </summary>
    class Nx2MacroEngine
    {
        public const int CodeSize = 0x8000;
        public const int MacroCount = 0x80;

        private int _writeOffset;
        private int _startAddressPointer;

        public uint[] Code { get; } = new uint[CodeSize];
        public uint[] Positions { get; } = new uint[MacroCount];
        public TuringMacroInterpreter Interpreter { get; } = new();

        /// <summary>
        /// LOAD_MME_INSTRUCTION_RAM_POINTER: the pointer counts 96-bit instructions.
        /// </summary>
        public void SetInstructionPointer(uint pointer)
        {
            _writeOffset = (ulong)pointer * 3 < CodeSize ? (int)pointer * 3 : CodeSize;
        }

        /// <summary>
        /// LOAD_MME_INSTRUCTION_RAM
        /// </summary>
        public void AddCode(uint data)
        {
            if (_writeOffset < CodeSize)
            {
                Code[_writeOffset++] = data;
            }
        }

        /// <summary>
        /// LOAD_MME_START_ADDRESS_RAM_POINTER
        /// </summary>
        public void SetStartAddressPointer(uint pointer)
        {
            _startAddressPointer = (int)pointer;
        }

        /// <summary>
        /// LOAD_MME_START_ADDRESS_RAM
        /// </summary>
        public void Bind(uint position)
        {
            if ((uint)_startAddressPointer < MacroCount)
            {
                Positions[_startAddressPointer] = position;
            }

            _startAddressPointer++;
        }
    }
}
