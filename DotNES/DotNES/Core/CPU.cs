﻿using DotNES.Core;
using DotNES.Utilities;
using System;
using System.Linq;
using System.Reflection;

namespace DotNES
{
    /// <summary>
    /// This is an emulator for the 6502 component of the Ricoh 2A03 microprocessor.
    /// Lots of great information is available or http://wiki.nesdev.com/w/index.php/CPU
    /// </summary>
    public class CPU
    {
        private Logger log = new Logger("CPU");
        public void setLoggerEnabled(bool enable)
        {
            this.log.setEnabled(enable);
        }

        public ushort getPC()
        {
            return _PC;
        }

        // Triggered by the PPU when NMI is fired.
        public bool nmi = false;

        private MethodInfo[] opcodeFunctions;
        private NESConsole console;

        #region Registers

        /// <summary>
        /// Accumulator Register - All arithmetic operations (Add, Subtract, etc.) act on this register.
        /// </summary>
        byte _A;

        /// <summary>
        /// Index Register X - Used as a pointer offset in several indirect addressing modes
        /// </summary>
        byte _X;

        /// <summary>
        /// Index Register Y - Used as a pointer offset in several indirect addressing modes
        /// </summary>
        byte _Y;

        /// <summary>
        /// Program Counter Register - 16-bit pointer to the currently executing piece of code.
        /// Incremented as most instructions are processed. It is manipulated by jump-type instructions, as well as interrupts.
        /// </summary>
        ushort _PC;

        /// <summary>
        /// Stack Pointer Register - The 2A03 can refernce 256 bytes of stack space. This register points to the current point in the stack.
        /// </summary>
        byte _S;

        /// <summary>
        /// Processor Status Register - This is a bitfield representing certain flags, each being set by various operations during execution.
        /// The various fields of the status register from most-significant to least-significant: 
        /// NVssDIZC
        /// 
        /// N : Negative:  1 if the previous operation resulted in a negative value
        /// V : Overflow:  1 if the previous caused a signed overflow
        /// s : (Unused):  These have effectively no use on the NES. They are written during certain stack operations.
        /// D : Decimal:   1 if Decimal Mode is enabled. This is ignored in the 2A03 and so it is of no concern on the NES.
        /// I : Interrupt: "Interrupt Inhibit" (0 : IRQ and NMI interrupts will get through, 1 : Only NMI will get through)
        /// Z : Zero:      Set if the last operation resulted in a zero
        /// C : Carry:     Set if the last addition or shift resulted in a carry, or last subtraction resulted in no borrow.
        /// </summary>
        byte _P;

        enum StatusFlag
        {
            Carry = 0,
            Zero = 1,
            InterruptDisable = 2,
            Decimal = 3,
            Unused1 = 4,
            Unused2 = 5,
            Overflow = 6,
            Negative = 7
        }

        // A couple of helpers for getting and setting bits for various registers
        void setFlag(StatusFlag flag, byte val)
        {
            int bit = (int)flag;

            // Get rid of the bit currently there, and put our bit there.
            _P = (byte)((_P & (~(1 << bit))) | ((val & 1) << bit));
        }

        byte getFlag(StatusFlag flag)
        {
            int bit = (int)flag;
            return (byte)((_P >> bit) & 1);
        }

        #endregion

        #region OpCodes

        private void initializeOpCodeTable()
        {
            // Initialize all functions to zero-cycle NOPs
            opcodeFunctions = new MethodInfo[256];
            for (int i = 0; i < 256; ++i)
                opcodeFunctions[i] = null;

            // Look at all the OpCodes implemented in this class and insert them into the array
            MethodInfo[] methods = GetType().GetMethods(BindingFlags.NonPublic | BindingFlags.Instance);
            foreach (var method in methods)
            {
                OpCodeAttribute attribute = Attribute.GetCustomAttribute(method, typeof(OpCodeAttribute), false) as OpCodeAttribute;
                if (attribute == null)
                    continue;

                opcodeFunctions[attribute.opcode] = method;
            }

            log.info("Initialized 6502 with {0} implemented OpCodes", opcodeFunctions.Where(x => x != null).Count());
        }

        // Implementation details : http://www.obelisk.demon.co.uk/6502/reference.html

        #region Jumps
        [OpCode(opcode = 0x4C, name = "JMP", bytes = 3)]
        private int JMP_Absolute()
        {
            _PC = argOne16();
            return 3;
        }

        [OpCode(opcode = 0x6C, name = "JMP", bytes = 3)]
        private int JMP_Indirect()
        {
            ushort indirectAddress = argOne16();

            // JMP Indirect has a bug where the indirect address wraps the page boundary
            _PC = console.memory.read16(indirectAddress, true);
            return 5;
        }

        [OpCode(opcode = 0x20, name = "JSR", bytes = 3)]
        private int JSR_Absolute()
        {
            pushStack16((ushort)(_PC + 2));
            _PC = argOne16();
            return 6;
        }

        [OpCode(opcode = 0x60, name = "RTS", bytes = 1)]
        private int RTS_Implied()
        {
            _PC = (ushort)(popStack16() + 1);
            return 6;
        }

        [OpCode(opcode = 0x40, name = "RTI", bytes = 1)]
        private int RTI_Implied()
        {
            _P = popStack8();
            _PC = popStack16();
            return 6;
        }

        [OpCode(opcode = 0x00, name = "BRK", bytes = 1)]
        private int BRK_Implied()
        {
            pushStack16(_PC);
            _P |= 0x20;
            pushStack8(_P);

            _PC = console.memory.read16(0xFFFE);
            return 7;
        }

        [OpCode(opcode = 0xEA, name = "NOP", bytes = 1)]
        private int NOP_Implied()
        {
            _PC += 1;
            return 2;
        }

        #endregion

        #region Arithmetic

        #region ADC
        [OpCode(opcode = 0x69, name = "ADC", bytes = 2)]
        private int ADC_Immediate()
        {
            byte A = _A;
            byte arg = argOne();
            byte carry = getFlag(StatusFlag.Carry);
            int result = _A + arg + carry;

            _A = (byte)(result & 0xFF);

            setOverflowForOperands(A, arg, result);
            setNegativeForOperand(_A);
            setCarryForResult(result);
            setZeroForOperand(_A);

            _PC += 2;
            return 2;
        }

        [OpCode(opcode = 0x65, name = "ADC", bytes = 2)]
        private int ADC_ZeroPage()
        {
            byte A = _A;
            byte arg = console.memory.read8(argOne());
            byte carry = getFlag(StatusFlag.Carry);
            int result = _A + arg + carry;

            _A = (byte)(result & 0xFF);

            setOverflowForOperands(A, arg, result);
            setNegativeForOperand(_A);
            setCarryForResult(result);
            setZeroForOperand(_A);

            _PC += 2;
            return 3;
        }

        [OpCode(opcode = 0x75, name = "ADC", bytes = 2)]
        private int ADC_ZeroPageX()
        {
            byte A = _A;
            ushort address = (ushort)((argOne() + _X) & 0xFF);
            byte arg = console.memory.read8(address);
            byte carry = getFlag(StatusFlag.Carry);
            int result = _A + arg + carry;

            _A = (byte)(result & 0xFF);

            setOverflowForOperands(A, arg, result);
            setNegativeForOperand(_A);
            setCarryForResult(result);
            setZeroForOperand(_A);

            _PC += 2;
            return 4;
        }

        [OpCode(opcode = 0x6D, name = "ADC", bytes = 3)]
        private int ADC_Absolute()
        {
            byte A = _A;
            byte arg = console.memory.read8(argOne16());
            byte carry = getFlag(StatusFlag.Carry);
            int result = _A + arg + carry;

            _A = (byte)(result & 0xFF);

            setOverflowForOperands(A, arg, result);
            setNegativeForOperand(_A);
            setCarryForResult(result);
            setZeroForOperand(_A);

            _PC += 3;
            return 4;
        }

        [OpCode(opcode = 0x7D, name = "ADC", bytes = 3)]
        private int ADC_AbsoluteX()
        {
            return ADC_AbsoluteWithRegister(_X);
        }

        [OpCode(opcode = 0x79, name = "ADC", bytes = 3)]
        private int ADC_AbsoluteY()
        {
            return ADC_AbsoluteWithRegister(_Y);
        }

        private int ADC_AbsoluteWithRegister(ushort registerValue)
        {
            byte A = _A;
            ushort address = (ushort)(argOne16() + registerValue);
            byte arg = console.memory.read8(address);
            byte carry = getFlag(StatusFlag.Carry);
            int result = _A + arg + carry;

            _A = (byte)(result & 0xFF);

            setOverflowForOperands(A, arg, result);
            setNegativeForOperand(_A);
            setCarryForResult(result);
            setZeroForOperand(_A);

            _PC += 3;

            if (samePage(arg, address))
            {
                return 4;
            }
            else
            {
                return 5;
            }
        }

        [OpCode(opcode = 0x61, name = "ADC", bytes = 2)]
        private int ADC_IndirectX()
        {
            byte A = _A;
            ushort address = (ushort)((argOne() + _X) & 0xFF);
            byte arg = console.memory.read8(console.memory.read16(address));
            byte carry = getFlag(StatusFlag.Carry);
            int result = _A + arg + carry;

            _A = (byte)(result & 0xFF);

            setOverflowForOperands(A, arg, result);
            setNegativeForOperand(_A);
            setCarryForResult(result);
            setZeroForOperand(_A);

            _PC += 2;
            return 6;
        }

        [OpCode(opcode = 0x71, name = "ADC", bytes = 2)]
        private int ADC_IndirectY()
        {
            byte A = _A;
            ushort addressWithoutY = console.memory.read16(argOne(), true);
            ushort addressWithY = (ushort)(addressWithoutY + _Y);

            byte arg = console.memory.read8(addressWithY);
            byte carry = getFlag(StatusFlag.Carry);
            int result = _A + arg + carry;

            _A = (byte)(result & 0xFF);

            setOverflowForOperands(A, arg, result);
            setNegativeForOperand(_A);
            setCarryForResult(result);
            setZeroForOperand(_A);

            _PC += 2;
            if (samePage(addressWithY, addressWithoutY))
            {
                return 5;
            }
            else
            {
                return 6;
            }
        }
        #endregion

        #region SBC

        // Really cool property! SBC is nearly identical to ADC:
        // SBC computes Result = A - M - (1-Carry), where M is the thing to subtract
        // R = A - M - (1-Carry)
        // R = A + (~M + 1) - 1 + Carry
        // R = A + ~M + Carry
        // Which is exactly the same logic as ADC with ~M instead of M

        [OpCode(opcode = 0xE9, name = "SBC", bytes = 2)]
        private int SBC_Immediate()
        {
            byte A = _A;
            byte arg = (byte)(argOne() ^ 0xFF);
            byte carry = getFlag(StatusFlag.Carry);
            int result = _A + arg + carry;

            _A = (byte)(result & 0xFF);

            setOverflowForOperands(A, arg, result);
            setNegativeForOperand(_A);
            setCarryForResult(result);
            setZeroForOperand(_A);

            _PC += 2;
            return 2;
        }

        [OpCode(opcode = 0xE5, name = "SBC", bytes = 2)]
        private int SBC_ZeroPage()
        {
            byte A = _A;
            byte arg = console.memory.read8(argOne());
            arg ^= 0xFF;
            byte carry = getFlag(StatusFlag.Carry);
            int result = _A + arg + carry;

            _A = (byte)(result & 0xFF);

            setOverflowForOperands(A, arg, result);
            setNegativeForOperand(_A);
            setCarryForResult(result);
            setZeroForOperand(_A);

            _PC += 2;
            return 3;
        }

        [OpCode(opcode = 0xF5, name = "SBC", bytes = 2)]
        private int SBC_ZeroPageX()
        {
            byte A = _A;
            ushort address = (ushort)((argOne() + _X) & 0xFF);
            byte arg = console.memory.read8(address);
            arg ^= 0xFF;
            byte carry = getFlag(StatusFlag.Carry);
            int result = _A + arg + carry;

            _A = (byte)(result & 0xFF);

            setOverflowForOperands(A, arg, result);
            setNegativeForOperand(_A);
            setCarryForResult(result);
            setZeroForOperand(_A);

            _PC += 2;
            return 4;
        }

        [OpCode(opcode = 0xED, name = "SBC", bytes = 3)]
        private int SBC_Absolute()
        {
            byte A = _A;
            byte arg = console.memory.read8(argOne16());
            arg ^= 0xFF;
            byte carry = getFlag(StatusFlag.Carry);
            int result = _A + arg + carry;

            _A = (byte)(result & 0xFF);

            setOverflowForOperands(A, arg, result);
            setNegativeForOperand(_A);
            setCarryForResult(result);
            setZeroForOperand(_A);

            _PC += 3;
            return 4;
        }

        [OpCode(opcode = 0xFD, name = "SBC", bytes = 3)]
        private int SBC_AbsoluteX()
        {
            return SBC_AbsoluteWithRegister(_X);
        }

        [OpCode(opcode = 0xF9, name = "SBC", bytes = 3)]
        private int SBC_AbsoluteY()
        {
            return SBC_AbsoluteWithRegister(_Y);
        }

        private int SBC_AbsoluteWithRegister(byte registerValue)
        {
            byte A = _A;
            ushort address = (ushort)(argOne16() + registerValue);
            byte arg = console.memory.read8(address);
            arg ^= 0xFF;
            byte carry = getFlag(StatusFlag.Carry);
            int result = _A + arg + carry;

            _A = (byte)(result & 0xFF);

            setOverflowForOperands(A, arg, result);
            setNegativeForOperand(_A);
            setCarryForResult(result);
            setZeroForOperand(_A);

            _PC += 3;

            if (samePage(arg, address))
            {
                return 4;
            }
            else
            {
                return 5;
            }
        }

        [OpCode(opcode = 0xE1, name = "SBC", bytes = 2)]
        private int SBC_IndirectX()
        {
            byte A = _A;
            ushort address = (ushort)((argOne() + _X) & 0xFF);
            byte arg = console.memory.read8(console.memory.read16(address));
            arg ^= 0xFF;
            byte carry = getFlag(StatusFlag.Carry);
            int result = _A + arg + carry;

            _A = (byte)(result & 0xFF);

            setOverflowForOperands(A, arg, result);
            setNegativeForOperand(_A);
            setCarryForResult(result);
            setZeroForOperand(_A);

            _PC += 2;
            return 6;
        }

        [OpCode(opcode = 0xF1, name = "SBC", bytes = 2)]
        private int SBC_IndirectY()
        {
            byte A = _A;
            ushort addressWithoutY = console.memory.read16(argOne(), true);
            ushort addressWithY = (ushort)(addressWithoutY + _Y);

            byte arg = console.memory.read8(addressWithY);
            arg ^= 0xFF;
            byte carry = getFlag(StatusFlag.Carry);
            int result = _A + arg + carry;

            _A = (byte)(result & 0xFF);

            setOverflowForOperands(A, arg, result);
            setNegativeForOperand(_A);
            setCarryForResult(result);
            setZeroForOperand(_A);

            _PC += 2;
            if (samePage(addressWithY, addressWithoutY))
            {
                return 5;
            }
            else
            {
                return 6;
            }
        }
        #endregion

        #region AND
        [OpCode(opcode = 0x29, name = "AND", bytes = 2)]
        private int AND_Immediate()
        {
            byte arg = argOne();
            _A = (byte)(_A & arg);

            setNegativeForOperand(_A);
            setZeroForOperand(_A);

            _PC += 2;
            return 2;
        }

        [OpCode(opcode = 0x25, name = "AND", bytes = 2)]
        private int AND_ZeroPage()
        {
            byte arg = console.memory.read8(argOne());
            _A = (byte)(_A & arg);

            setNegativeForOperand(_A);
            setZeroForOperand(_A);

            _PC += 2;
            return 3;
        }

        [OpCode(opcode = 0x35, name = "AND", bytes = 2)]
        private int AND_ZeroPageX()
        {
            ushort address = (ushort)((argOne() + _X) & 0xFF);
            byte arg = console.memory.read8(address);
            _A = (byte)(_A & arg);

            setNegativeForOperand(_A);
            setZeroForOperand(_A);

            _PC += 2;
            return 4;
        }

        [OpCode(opcode = 0x2D, name = "AND", bytes = 3)]
        private int AND_Absolute()
        {
            ushort address = argOne16();
            byte arg = console.memory.read8(address);
            _A = (byte)(_A & arg);

            setNegativeForOperand(_A);
            setZeroForOperand(_A);

            _PC += 3;
            return 4;
        }

        [OpCode(opcode = 0x3D, name = "AND", bytes = 3)]
        private int AND_AbsoluteX()
        {
            return AND_AbsoluteWithRegister(_X);
        }

        [OpCode(opcode = 0x39, name = "AND", bytes = 3)]
        private int AND_AbsoluteY()
        {
            return AND_AbsoluteWithRegister(_Y);
        }

        private int AND_AbsoluteWithRegister(ushort registerValue)
        {
            ushort address = (ushort)(argOne16() + registerValue);
            byte arg = console.memory.read8(address);
            _A = (byte)(_A & arg);

            setNegativeForOperand(_A);
            setZeroForOperand(_A);

            _PC += 3;

            if (samePage(arg, address))
            {
                return 4;
            }
            else
            {
                return 5;
            }
        }

        [OpCode(opcode = 0x21, name = "AND", bytes = 2)]
        private int AND_IndirectX()
        {
            ushort address = (ushort)((argOne() + _X) & 0xFF);
            byte arg = console.memory.read8(console.memory.read16(address));
            _A = (byte)(_A & arg);

            setNegativeForOperand(_A);
            setZeroForOperand(_A);

            _PC += 2;
            return 6;
        }

        [OpCode(opcode = 0x31, name = "AND", bytes = 2)]
        private int AND_IndirectY()
        {
            ushort addressWithoutY = console.memory.read16(argOne(), true);
            ushort addressWithY = (ushort)(addressWithoutY + _Y);
            byte arg = console.memory.read8(addressWithY);
            _A = (byte)(_A & arg);

            setNegativeForOperand(_A);
            setZeroForOperand(_A);

            _PC += 2;
            if (samePage(addressWithY, addressWithoutY))
            {
                return 5;
            }
            else
            {
                return 6;
            }
        }

        #endregion

        #region ASL
        [OpCode(opcode = 0x0A, name = "ASL", bytes = 1)]
        private int ASL_Accumulator()
        {
            byte newCarry = (byte)((_A >> 7) & 1);
            _A = (byte)(_A << 1);

            setFlag(StatusFlag.Carry, newCarry);
            setNegativeForOperand(_A);
            setZeroForOperand(_A);

            _PC += 1;
            return 2;
        }

        [OpCode(opcode = 0x06, name = "ASL", bytes = 2)]
        private int ASL_ZeroPage()
        {
            ushort address = argOne();
            byte val = console.memory.read8(address);
            byte newCarry = (byte)((val >> 7) & 1);
            val = (byte)(val << 1);
            console.memory.write8(address, val);

            setFlag(StatusFlag.Carry, newCarry);
            setNegativeForOperand(val);
            setZeroForOperand(val);

            _PC += 2;
            return 5;
        }

        [OpCode(opcode = 0x16, name = "ASL", bytes = 2)]
        private int ASL_ZeroPageX()
        {
            ushort address = (ushort)((argOne() + _X) & 0xFF);
            byte val = console.memory.read8(address);
            byte newCarry = (byte)((val >> 7) & 1);
            val = (byte)(val << 1);
            console.memory.write8(address, val);

            setFlag(StatusFlag.Carry, newCarry);
            setNegativeForOperand(val);
            setZeroForOperand(val);

            _PC += 2;
            return 6;
        }

        [OpCode(opcode = 0x0E, name = "ASL", bytes = 3)]
        private int ASL_Absolute()
        {
            ushort address = argOne16();
            byte val = console.memory.read8(address);
            byte newCarry = (byte)((val >> 7) & 1);
            val = (byte)(val << 1);
            console.memory.write8(address, val);

            setFlag(StatusFlag.Carry, newCarry);
            setNegativeForOperand(val);
            setZeroForOperand(val);

            _PC += 3;
            return 6;
        }

        [OpCode(opcode = 0x1E, name = "ASL", bytes = 3)]
        private int ASL_AbsoluteX()
        {
            ushort address = (ushort)((argOne16() + _X));
            byte val = console.memory.read8(address);
            byte newCarry = (byte)((val >> 7) & 1);
            val = (byte)(val << 1);
            console.memory.write8(address, val);

            setFlag(StatusFlag.Carry, newCarry);
            setNegativeForOperand(val);
            setZeroForOperand(val);

            _PC += 3;
            return 7;
        }
        #endregion

        #region LSR
        [OpCode(opcode = 0x4A, name = "LSR", bytes = 1)]
        private int LSR_Accumulator()
        {
            byte newCarry = (byte)(_A & 1);
            _A = (byte)(_A >> 1);

            setFlag(StatusFlag.Carry, newCarry);
            setNegativeForOperand(0);
            setZeroForOperand(_A);

            _PC += 1;
            return 2;
        }

        [OpCode(opcode = 0x46, name = "LSR", bytes = 2)]
        private int LSR_ZeroPage()
        {
            ushort address = argOne();
            byte val = console.memory.read8(address);
            byte newCarry = (byte)(val & 1);
            val = (byte)(val >> 1);
            console.memory.write8(address, val);

            setFlag(StatusFlag.Carry, newCarry);
            setNegativeForOperand(0);
            setZeroForOperand(val);

            _PC += 2;
            return 5;
        }

        [OpCode(opcode = 0x56, name = "LSR", bytes = 2)]
        private int LSR_ZeroPageX()
        {
            ushort address = (ushort)((argOne() + _X) & 0xFF);
            byte val = console.memory.read8(address);
            byte newCarry = (byte)(val & 1);
            val = (byte)(val >> 1);
            console.memory.write8(address, val);

            setFlag(StatusFlag.Carry, newCarry);
            setNegativeForOperand(0);
            setZeroForOperand(val);

            _PC += 2;
            return 6;
        }

        [OpCode(opcode = 0x4E, name = "LSR", bytes = 3)]
        private int LSR_Absolute()
        {
            ushort address = argOne16();
            byte val = console.memory.read8(address);
            byte newCarry = (byte)(val & 1);
            val = (byte)(val >> 1);
            console.memory.write8(address, val);

            setFlag(StatusFlag.Carry, newCarry);
            setNegativeForOperand(0);
            setZeroForOperand(val);

            _PC += 3;
            return 6;
        }

        [OpCode(opcode = 0x5E, name = "LSR", bytes = 3)]
        private int LSR_AbsoluteX()
        {
            ushort address = (ushort)(argOne16() + _X);
            byte val = console.memory.read8(address);
            byte newCarry = (byte)(val & 1);
            val = (byte)(val >> 1);
            console.memory.write8(address, val);

            setFlag(StatusFlag.Carry, newCarry);
            setNegativeForOperand(0);
            setZeroForOperand(val);

            _PC += 3;
            return 7;
        }
        #endregion

        #region Compare

        #region CMP

        [OpCode(opcode = 0xC9, name = "CMP", bytes = 2)]
        private int CMP_Immeditate()
        {
            compareValues(_A, argOne());
            _PC += 2;
            return 2;
        }

        [OpCode(opcode = 0xC5, name = "CMP", bytes = 2)]
        private int CMP_ZeroPage()
        {
            compareValues(_A, console.memory.read8(argOne()));
            _PC += 2;
            return 3;
        }

        [OpCode(opcode = 0xD5, name = "CMP", bytes = 2)]
        private int CMP_ZeroPageX()
        {
            compareValues(_A, console.memory.read8((byte)((argOne() + _X) & 0xFF)));
            _PC += 2;
            return 4;
        }

        [OpCode(opcode = 0xCD, name = "CMP", bytes = 3)]
        private int CMP_Absolute()
        {
            compareValues(_A, console.memory.read8(argOne16()));
            _PC += 3;
            return 4;
        }

        [OpCode(opcode = 0xDD, name = "CMP", bytes = 3)]
        private int CMP_AbsoluteX()
        {
            ushort address = argOne16();
            ushort addressWithX = (ushort)(argOne16() + _X);
            compareValues(_A, console.memory.read8(addressWithX));
            _PC += 3;
            if (samePage(address, addressWithX))
            {
                return 4;
            }
            else
            {
                return 5;
            }
        }

        [OpCode(opcode = 0xD9, name = "CMP", bytes = 3)]
        private int CMP_AbsoluteY()
        {
            ushort address = argOne16();
            ushort addressWithY = (ushort)(argOne16() + _Y);
            compareValues(_A, console.memory.read8(addressWithY));
            _PC += 3;
            if (samePage(address, addressWithY))
            {
                return 4;
            }
            else
            {
                return 5;
            }
        }

        [OpCode(opcode = 0xC1, name = "CMP", bytes = 2)]
        private int CMP_IndirectX()
        {
            ushort address = (ushort)((argOne() + _X) & 0xFF);
            ushort indirectAddress = console.memory.read16(address);
            compareValues(_A, console.memory.read8(indirectAddress));
            _PC += 2;
            return 6;
        }

        [OpCode(opcode = 0xD1, name = "CMP", bytes = 2)]
        private int CMP_IndirectY()
        {
            ushort address = console.memory.read16(argOne(), true);
            ushort addressWithY = (ushort)(address + _Y);
            compareValues(_A, console.memory.read8(addressWithY));
            _PC += 2;
            if (samePage(address, addressWithY))
            {
                return 5;
            }
            else
            {
                return 6;
            }
        }

        #endregion

        #region CPX

        [OpCode(opcode = 0xE0, name = "CMX", bytes = 2)]
        private int CMX_Immeditate()
        {
            compareValues(_X, argOne());
            _PC += 2;
            return 2;
        }

        [OpCode(opcode = 0xE4, name = "CMX", bytes = 2)]
        private int CMX_ZeroPage()
        {
            compareValues(_X, console.memory.read8(argOne()));
            _PC += 2;
            return 3;
        }

        [OpCode(opcode = 0xEC, name = "CMX", bytes = 3)]
        private int CMX_Absolute()
        {
            compareValues(_X, console.memory.read8(argOne16()));
            _PC += 3;
            return 4;
        }

        #endregion

        #region CPY

        [OpCode(opcode = 0xC0, name = "CPY", bytes = 2)]
        private int CPY_Immeditate()
        {
            compareValues(_Y, argOne());
            _PC += 2;
            return 2;
        }

        [OpCode(opcode = 0xC4, name = "CPY", bytes = 2)]
        private int CPY_ZeroPage()
        {
            compareValues(_Y, console.memory.read8(argOne()));
            _PC += 2;
            return 3;
        }

        [OpCode(opcode = 0xCC, name = "CPY", bytes = 3)]
        private int CPY_Absolute()
        {
            compareValues(_Y, console.memory.read8(argOne16()));
            _PC += 3;
            return 4;
        }

        #endregion

        private void compareValues(byte registerValue, byte argumentValue)
        {
            byte result = registerValue;
            result += 1;
            result += (byte)(~argumentValue);

            setZeroForOperand((byte)result);
            setNegativeForOperand((byte)result);
            setFlag(StatusFlag.Carry, (byte)(registerValue >= argumentValue ? 1 : 0));
        }

        #endregion

        #endregion

        #region Branch
        [OpCode(opcode = 0x90, name = "BCC", bytes = 2)]
        private int BCC_Relative()
        {
            return Branch(getFlag(StatusFlag.Carry) == 0);
        }

        [OpCode(opcode = 0xB0, name = "BCS", bytes = 2)]
        private int BCS_Relative()
        {
            return Branch(getFlag(StatusFlag.Carry) == 1);
        }

        [OpCode(opcode = 0xF0, name = "BEQ", bytes = 2)]
        private int BEQ_Relative()
        {
            return Branch(getFlag(StatusFlag.Zero) == 1);
        }

        [OpCode(opcode = 0xD0, name = "BNE", bytes = 2)]
        private int BNE_Relative()
        {
            return Branch(getFlag(StatusFlag.Zero) == 0);
        }

        [OpCode(opcode = 0x30, name = "BMI", bytes = 2)]
        private int BMI_Relative()
        {
            return Branch(getFlag(StatusFlag.Negative) == 1);
        }

        [OpCode(opcode = 0x10, name = "BPL", bytes = 2)]
        private int BPL_Relative()
        {
            return Branch(getFlag(StatusFlag.Negative) == 0);
        }

        [OpCode(opcode = 0x50, name = "BVC", bytes = 2)]
        private int BVC_Relative()
        {
            return Branch(getFlag(StatusFlag.Overflow) == 0);
        }

        [OpCode(opcode = 0x70, name = "BVS", bytes = 2)]
        private int BVS_Relative()
        {
            return Branch(getFlag(StatusFlag.Overflow) == 1);
        }

        private int Branch(bool condition)
        {
            // All branches are 2 Cycles...
            int cycles = 2;

            if (!condition)
            {
                _PC += 2;
                return cycles;
            }

            // ... +1 if the branch is taken
            cycles++;

            // The displacements for branches are signed (can go backwards and forwards [-128,127])
            sbyte offset = (sbyte)argOne();

            ushort oldPC = _PC;
            ushort newPC = (ushort)(_PC + 2 + offset);

            // ... And +1 more cycle if it crosses pages
            if (!samePage(oldPC, newPC))
                cycles++;

            _PC = newPC;
            return cycles;
        }
        #endregion

        #region Flag Manipulation
        [OpCode(opcode = 0xD8, name = "CLD", bytes = 1)]
        private int CLD_Implicit()
        {
            setFlag(StatusFlag.Decimal, 0);
            _PC += 1;
            return 2;
        }

        [OpCode(opcode = 0x18, name = "CLC", bytes = 1)]
        private int CLC_Implicit()
        {
            setFlag(StatusFlag.Carry, 0);
            _PC += 1;
            return 2;
        }

        [OpCode(opcode = 0x58, name = "CLI", bytes = 1)]
        private int CLI_Implicit()
        {
            setFlag(StatusFlag.InterruptDisable, 0);
            _PC += 1;
            return 2;
        }

        [OpCode(opcode = 0xB8, name = "CLV", bytes = 1)]
        private int CLV_Implicit()
        {
            setFlag(StatusFlag.Overflow, 0);
            _PC += 1;
            return 2;
        }

        [OpCode(opcode = 0x38, name = "SEC", bytes = 1)]
        private int SEC_Implicit()
        {
            setFlag(StatusFlag.Carry, 1);
            _PC += 1;
            return 2;
        }

        [OpCode(opcode = 0xF8, name = "SED", bytes = 1)]
        private int SED_Implicit()
        {
            setFlag(StatusFlag.Decimal, 1);
            _PC += 1;
            return 2;
        }

        [OpCode(opcode = 0x78, name = "SEI", bytes = 1)]
        private int SEI_Implicit()
        {
            setFlag(StatusFlag.InterruptDisable, 1);
            _PC += 1;
            return 2;
        }
        #endregion

        #region Stack
        [OpCode(opcode = 0x48, name = "PHA", bytes = 1)]
        private int PHA()
        {
            pushStack8(_A);
            _PC += 1;
            return 3;
        }

        [OpCode(opcode = 0x08, name = "PHP", bytes = 1)]
        private int PHP()
        {
            _P |= 0x20;
            pushStack8(_P);
            _PC += 1;
            return 3;
        }

        [OpCode(opcode = 0x68, name = "PLA", bytes = 1)]
        private int PLA()
        {
            _A = popStack8();
            setZeroForOperand(_A);
            setNegativeForOperand(_A);
            _PC += 1;
            return 4;
        }

        [OpCode(opcode = 0x28, name = "PLP", bytes = 1)]
        private int PLP()
        {
            _P = popStack8();
            _PC += 1;
            return 4;
        }
        #endregion

        #region Store

        #region STA
        [OpCode(opcode = 0x85, name = "STA", bytes = 2)]
        private int STA_ZeroPage()
        {
            console.memory.write8(argOne(), _A);
            _PC += 2;
            return 3;
        }

        [OpCode(opcode = 0x95, name = "STA", bytes = 2)]
        private int STA_ZeroPageX()
        {
            console.memory.write8((ushort)((argOne() + _X) & 0xFF), _A);
            _PC += 2;
            return 4;
        }

        [OpCode(opcode = 0x8D, name = "STA", bytes = 3)]
        private int STA_Absolute()
        {
            console.memory.write8(argOne16(), _A);
            _PC += 3;
            return 4;
        }

        [OpCode(opcode = 0x9D, name = "STA", bytes = 3)]
        private int STA_AbsoluteX()
        {
            console.memory.write8((ushort)(argOne16() + _X), _A);
            _PC += 3;
            return 5;
        }

        [OpCode(opcode = 0x99, name = "STA", bytes = 3)]
        private int STA_AbsoluteY()
        {
            console.memory.write8((ushort)(argOne16() + _Y), _A);
            _PC += 3;
            return 5;
        }

        [OpCode(opcode = 0x81, name = "STA", bytes = 2)]
        private int STA_IndirectX()
        {
            ushort address = (ushort)((argOne() + _X) & 0xFF);
            ushort indirectAddress = console.memory.read16(address, true);
            console.memory.write8(indirectAddress, _A);
            _PC += 2;
            return 6;
        }

        [OpCode(opcode = 0x91, name = "STA", bytes = 2)]
        private int STA_IndirectY()
        {
            ushort address = argOne();
            ushort indirectAddress = (ushort)(console.memory.read16(address, true) + _Y);
            console.memory.write8(indirectAddress, _A);
            _PC += 2;
            return 6;
        }


        #endregion

        #region STX

        [OpCode(opcode = 0x86, name = "STX", bytes = 2)]
        private int STX_ZeroPage()
        {
            console.memory.write8(argOne(), _X);
            _PC += 2;
            return 3;
        }

        [OpCode(opcode = 0x96, name = "STX", bytes = 2)]
        private int STX_ZeroPageY()
        {
            console.memory.write8((ushort)((argOne() + _Y) & 0xFF), _X);
            _PC += 2;
            return 4;
        }

        [OpCode(opcode = 0x8E, name = "STX", bytes = 3)]
        private int STX_Absolute()
        {
            console.memory.write8(argOne16(), _X);
            _PC += 3;
            return 4;
        }

        #endregion

        #region STY

        [OpCode(opcode = 0x84, name = "STY", bytes = 2)]
        private int STY_ZeroPage()
        {
            console.memory.write8(argOne(), _Y);
            _PC += 2;
            return 3;
        }

        [OpCode(opcode = 0x94, name = "STY", bytes = 2)]
        private int STY_ZeroPageX()
        {
            console.memory.write8((ushort)((argOne() + _X) & 0xFF), _Y);
            _PC += 2;
            return 4;
        }

        [OpCode(opcode = 0x8C, name = "STY", bytes = 3)]
        private int STY_Absolute()
        {
            console.memory.write8(argOne16(), _Y);
            _PC += 3;
            return 4;
        }

        #endregion

        #endregion

        #region Load

        #region LDA

        [OpCode(opcode = 0xA9, name = "LDA", bytes = 2)]
        private int LDA_Immediate()
        {
            _A = argOne();
            setZeroForOperand(_A);
            setNegativeForOperand(_A);
            _PC += 2;
            return 2;
        }

        [OpCode(opcode = 0xA5, name = "LDA", bytes = 2)]
        private int LDA_ZeroPage()
        {
            _A = console.memory.read8(argOne());
            setZeroForOperand(_A);
            setNegativeForOperand(_A);
            _PC += 2;
            return 3;
        }

        [OpCode(opcode = 0xB5, name = "LDA", bytes = 2)]
        private int LDA_ZeroPageX()
        {
            ushort address = (ushort)((argOne() + _X) & 0xFF);
            _A = console.memory.read8(address);
            setZeroForOperand(_A);
            setNegativeForOperand(_A);
            _PC += 2;
            return 4;
        }

        [OpCode(opcode = 0xAD, name = "LDA", bytes = 3)]
        private int LDA_Absolute()
        {
            _A = console.memory.read8(argOne16());
            setZeroForOperand(_A);
            setNegativeForOperand(_A);
            _PC += 3;
            return 4;
        }

        [OpCode(opcode = 0xBD, name = "LDA", bytes = 3)]
        private int LDA_AbsoluteX()
        {
            return LDA_AbsoluteWithRegister(_X);
        }

        [OpCode(opcode = 0xB9, name = "LDA", bytes = 3)]
        private int LDA_AbsoluteY()
        {
            return LDA_AbsoluteWithRegister(_Y);
        }

        private int LDA_AbsoluteWithRegister(byte registerValue)
        {
            ushort arg = argOne16();
            ushort address = (ushort)(arg + registerValue);
            _A = console.memory.read8(address);

            setZeroForOperand(_A);
            setNegativeForOperand(_A);
            _PC += 3;

            if (samePage(arg, address))
            {
                return 4;
            }
            else
            {
                return 5;
            }
        }

        [OpCode(opcode = 0xA1, name = "LDA", bytes = 2)]
        private int LDA_IndirectX()
        {
            ushort address = (ushort)((argOne() + _X) & 0xFF);
            _A = console.memory.read8(console.memory.read16(address, true));
            setZeroForOperand(_A);
            setNegativeForOperand(_A);
            _PC += 2;
            return 6;
        }

        [OpCode(opcode = 0xB1, name = "LDA", bytes = 2)]
        private int LDA_IndirectY()
        {
            ushort addressWithoutY = console.memory.read16(argOne(), true);
            ushort addressWithY = (ushort)(addressWithoutY + _Y);

            _A = console.memory.read8(addressWithY);

            setZeroForOperand(_A);
            setNegativeForOperand(_A);
            _PC += 2;

            if (samePage(addressWithY, addressWithoutY))
            {
                return 5;
            }
            else
            {
                return 6;
            }
        }
        #endregion

        #region LDX

        [OpCode(opcode = 0xA2, name = "LDX", bytes = 2)]
        private int LDX_Immediate()
        {
            _X = argOne();
            setZeroForOperand(_X);
            setNegativeForOperand(_X);
            _PC += 2;
            return 2;
        }

        [OpCode(opcode = 0xA6, name = "LDX", bytes = 2)]
        private int LDX_ZeroPage()
        {
            _X = console.memory.read8(argOne());
            setZeroForOperand(_X);
            setNegativeForOperand(_X);
            _PC += 2;
            return 3;
        }

        [OpCode(opcode = 0xB6, name = "LDX", bytes = 2)]
        private int LDX_ZeroPageY()
        {
            _X = console.memory.read8((ushort)((argOne() + _Y) & 0xFF));
            setZeroForOperand(_X);
            setNegativeForOperand(_X);
            _PC += 2;
            return 4;
        }

        [OpCode(opcode = 0xAE, name = "LDX", bytes = 3)]
        private int LDX_Absolute()
        {
            _X = console.memory.read8(argOne16());
            setZeroForOperand(_X);
            setNegativeForOperand(_X);
            _PC += 3;
            return 4;
        }

        [OpCode(opcode = 0xBE, name = "LDX", bytes = 3)]
        private int LDX_AbsoluteY()
        {
            ushort arg = argOne16();
            ushort address = (ushort)(arg + _Y);
            _X = console.memory.read8(address);

            setZeroForOperand(_X);
            setNegativeForOperand(_X);
            _PC += 3;

            if (samePage(arg, address))
            {
                return 4;
            }
            else
            {
                return 5;
            }
        }

        #endregion

        #region LDY

        [OpCode(opcode = 0xA0, name = "LDY", bytes = 2)]
        private int LDY_Immediate()
        {
            _Y = argOne();
            setZeroForOperand(_Y);
            setNegativeForOperand(_Y);
            _PC += 2;
            return 2;
        }

        [OpCode(opcode = 0xA4, name = "LDX", bytes = 2)]
        private int LDY_ZeroPage()
        {
            _Y = console.memory.read8(argOne());
            setZeroForOperand(_Y);
            setNegativeForOperand(_Y);
            _PC += 2;
            return 3;
        }

        [OpCode(opcode = 0xB4, name = "LDX", bytes = 2)]
        private int LDY_ZeroPageX()
        {
            ushort address = (ushort)((argOne() + _X) & 0xFF);
            _Y = console.memory.read8(address);
            setZeroForOperand(_Y);
            setNegativeForOperand(_Y);
            _PC += 2;
            return 4;
        }

        [OpCode(opcode = 0xAC, name = "LDX", bytes = 3)]
        private int LDY_Absolute()
        {
            _Y = console.memory.read8(argOne16());
            setZeroForOperand(_Y);
            setNegativeForOperand(_Y);
            _PC += 3;
            return 4;
        }

        [OpCode(opcode = 0xBC, name = "LDX", bytes = 3)]
        private int LDY_AbsoluteX()
        {
            ushort arg = argOne16();
            ushort address = (ushort)(arg + _X);
            _Y = console.memory.read8(address);

            setZeroForOperand(_Y);
            setNegativeForOperand(_Y);
            _PC += 3;

            if (samePage(arg, address))
            {
                return 4;
            }
            else
            {
                return 5;
            }
        }

        #endregion

        #endregion

        #region Transfer
        [OpCode(opcode = 0xAA, name = "TAX", bytes = 1)]
        private int TAX()
        {
            _X = _A;
            setZeroForOperand(_X);
            setNegativeForOperand(_X);
            _PC += 1;
            return 2;
        }

        [OpCode(opcode = 0xA8, name = "TAY", bytes = 1)]
        private int TAY()
        {
            _Y = _A;
            setZeroForOperand(_Y);
            setNegativeForOperand(_Y);
            _PC += 1;
            return 2;
        }

        [OpCode(opcode = 0xBA, name = "TSX", bytes = 1)]
        private int TSX()
        {
            _X = _S;
            setZeroForOperand(_X);
            setNegativeForOperand(_X);
            _PC += 1;
            return 2;
        }

        [OpCode(opcode = 0x8A, name = "TXA", bytes = 1)]
        private int TXA()
        {
            _A = _X;
            setZeroForOperand(_A);
            setNegativeForOperand(_A);
            _PC += 1;
            return 2;
        }

        [OpCode(opcode = 0x9A, name = "TXS", bytes = 1)]
        private int TXS()
        {
            _S = _X;
            _PC += 1;
            return 2;
        }

        [OpCode(opcode = 0x98, name = "TYA", bytes = 1)]
        private int TYA()
        {
            _A = _Y;
            setZeroForOperand(_A);
            setNegativeForOperand(_A);
            _PC += 1;
            return 2;
        }

        #endregion

        #region Increment/Decrement
        [OpCode(opcode = 0xE8, name = "INX", bytes = 1)]
        private int INX_Implied()
        {
            _X++;
            setNegativeForOperand(_X);
            setZeroForOperand(_X);
            _PC += 1;
            return 2;
        }

        [OpCode(opcode = 0xC8, name = "INY", bytes = 1)]
        private int INY_Implied()
        {
            _Y++;
            setNegativeForOperand(_Y);
            setZeroForOperand(_Y);
            _PC += 1;
            return 2;
        }

        [OpCode(opcode = 0xE6, name = "INC", bytes = 2)]
        private int INC_ZeroPage()
        {
            ushort address = argOne();
            byte value = console.memory.read8(address);
            value++;
            console.memory.write8(address, value);

            setNegativeForOperand(value);
            setZeroForOperand(value);

            _PC += 2;
            return 5;
        }

        [OpCode(opcode = 0xF6, name = "INC", bytes = 2)]
        private int INC_ZeroPageX()
        {
            ushort address = (ushort)((argOne() + _X) & 0xFF);
            byte value = console.memory.read8(address);
            value++;
            console.memory.write8(address, value);

            setNegativeForOperand(value);
            setZeroForOperand(value);

            _PC += 2;
            return 6;
        }

        [OpCode(opcode = 0xEE, name = "INC", bytes = 3)]
        private int INC_Absolute()
        {
            ushort address = argOne16();
            byte value = console.memory.read8(address);
            value++;
            console.memory.write8(address, value);

            setNegativeForOperand(value);
            setZeroForOperand(value);

            _PC += 3;
            return 6;
        }

        [OpCode(opcode = 0xFE, name = "INC", bytes = 3)]
        private int INC_AbsoluteX()
        {
            ushort address = (ushort)(argOne16() + _X);
            byte value = console.memory.read8(address);
            value++;
            console.memory.write8(address, value);

            setNegativeForOperand(value);
            setZeroForOperand(value);

            _PC += 3;
            return 7;
        }

        [OpCode(opcode = 0xCA, name = "DEX", bytes = 1)]
        private int DEX_Implied()
        {
            _X--;
            setNegativeForOperand(_X);
            setZeroForOperand(_X);
            _PC += 1;
            return 2;
        }

        [OpCode(opcode = 0x88, name = "DEY", bytes = 1)]
        private int DEY_Implied()
        {
            _Y--;
            setNegativeForOperand(_Y);
            setZeroForOperand(_Y);
            _PC += 1;
            return 2;
        }

        [OpCode(opcode = 0xC6, name = "DEC", bytes = 2)]
        private int DEC_ZeroPage()
        {
            ushort address = argOne();
            byte value = console.memory.read8(address);
            value--;
            console.memory.write8(address, value);

            setNegativeForOperand(value);
            setZeroForOperand(value);

            _PC += 2;
            return 5;
        }

        [OpCode(opcode = 0xD6, name = "DEC", bytes = 2)]
        private int DEC_ZeroPageX()
        {
            ushort address = (ushort)((argOne() + _X) & 0xFF);
            byte value = console.memory.read8(address);
            value--;
            console.memory.write8(address, value);

            setNegativeForOperand(value);
            setZeroForOperand(value);

            _PC += 2;
            return 6;
        }

        [OpCode(opcode = 0xCE, name = "DEC", bytes = 3)]
        private int DEC_Absolute()
        {
            ushort address = argOne16();
            byte value = console.memory.read8(address);
            value--;
            console.memory.write8(address, value);

            setNegativeForOperand(value);
            setZeroForOperand(value);

            _PC += 3;
            return 6;
        }

        [OpCode(opcode = 0xDE, name = "DEC", bytes = 3)]
        private int DEC_AbsoluteX()
        {
            ushort address = (ushort)(argOne16() + _X);
            byte value = console.memory.read8(address);
            value--;
            console.memory.write8(address, value);

            setNegativeForOperand(value);
            setZeroForOperand(value);

            _PC += 3;
            return 7;
        }

        #endregion

        #region Bit Manipulation

        #region BIT
        [OpCode(opcode = 0x2C, name = "BIT", bytes = 3)]
        private int BIT_Absolute()
        {
            byte arg = console.memory.read8(argOne16());
            byte result = (byte)(_A & arg);

            setNegativeForOperand(arg);
            setZeroForOperand(result);
            setFlag(StatusFlag.Overflow, (byte)((arg & 0x40) != 0 ? 1 : 0));

            _PC += 3;
            return 4;
        }

        [OpCode(opcode = 0x24, name = "BIT", bytes = 2)]
        private int BIT_ZeroPage()
        {
            byte arg = console.memory.read8(argOne());
            byte result = (byte)(_A & arg);

            setNegativeForOperand(arg);
            setZeroForOperand(result);
            setFlag(StatusFlag.Overflow, (byte)((arg & 0x40) != 0 ? 1 : 0));

            _PC += 2;
            return 3;
        }
        #endregion

        #region EOR
        [OpCode(opcode = 0x49, name = "EOR", bytes = 2)]
        private int EOR_Immediate()
        {
            _A = (byte)(_A ^ argOne());

            setNegativeForOperand(_A);
            setZeroForOperand(_A);

            _PC += 2;
            return 2;
        }

        [OpCode(opcode = 0x45, name = "EOR", bytes = 2)]
        private int EOR_ZeroPage()
        {
            byte arg = console.memory.read8(argOne());
            _A = (byte)(_A ^ arg);

            setNegativeForOperand(_A);
            setZeroForOperand(_A);

            _PC += 2;
            return 3;
        }

        [OpCode(opcode = 0x55, name = "EOR", bytes = 2)]
        private int EOR_ZeroPageX()
        {
            byte arg = console.memory.read8((byte)((argOne() + _X) & 0xFF));
            _A = (byte)(_A ^ arg);

            setNegativeForOperand(_A);
            setZeroForOperand(_A);

            _PC += 2;
            return 4;
        }

        [OpCode(opcode = 0x4D, name = "EOR", bytes = 3)]
        private int EOR_Absolute()
        {
            byte arg = console.memory.read8(argOne16());
            _A = (byte)(_A ^ arg);

            setNegativeForOperand(_A);
            setZeroForOperand(_A);

            _PC += 3;
            return 4;
        }

        [OpCode(opcode = 0x5D, name = "EOR", bytes = 3)]
        private int EOR_AbsoluteX()
        {
            return EOR_AbsoluteWithRegister(_X);
        }

        [OpCode(opcode = 0x59, name = "EOR", bytes = 3)]
        private int EOR_AbsoluteY()
        {
            return EOR_AbsoluteWithRegister(_Y);
        }

        [OpCode(opcode = 0x41, name = "EOR", bytes = 2)]
        private int EOR_IndirectX()
        {
            ushort address = (ushort)((argOne() + _X) & 0xFF);
            ushort indirectAddress = console.memory.read16(address);

            byte arg = console.memory.read8(indirectAddress);
            _A = (byte)(_A ^ arg);

            setNegativeForOperand(_A);
            setZeroForOperand(_A);

            _PC += 2;
            return 6;
        }

        [OpCode(opcode = 0x51, name = "EOR", bytes = 2)]
        private int EOR_IndirectY()
        {
            ushort address = console.memory.read16(argOne(), true);
            ushort addressWithY = (ushort)(address + _Y);

            byte arg = console.memory.read8(addressWithY);
            _A = (byte)(_A ^ arg);

            setNegativeForOperand(_A);
            setZeroForOperand(_A);

            _PC += 2;
            if (samePage(address, addressWithY))
            {
                return 5;
            }
            else
            {
                return 6;
            }
        }

        private int EOR_AbsoluteWithRegister(byte registerValue)
        {
            ushort address = argOne16();
            ushort addressWithReg = (ushort)(argOne16() + registerValue);
            byte arg = console.memory.read8(addressWithReg);

            _A = (byte)(_A ^ arg);

            setNegativeForOperand(_A);
            setZeroForOperand(_A);

            _PC += 3;


            if (samePage(address, addressWithReg))
            {
                return 4;
            }
            else
            {
                return 5;
            }
        }
        #endregion

        #region ORA
        [OpCode(opcode = 0x09, name = "ORA", bytes = 2)]
        private int ORA_Immediate()
        {
            _A = (byte)(_A | argOne());

            setNegativeForOperand(_A);
            setZeroForOperand(_A);

            _PC += 2;
            return 2;
        }

        [OpCode(opcode = 0x05, name = "ORA", bytes = 2)]
        private int ORA_ZeroPage()
        {
            byte arg = console.memory.read8(argOne());
            _A = (byte)(_A | arg);

            setNegativeForOperand(_A);
            setZeroForOperand(_A);

            _PC += 2;
            return 3;
        }

        [OpCode(opcode = 0x15, name = "ORA", bytes = 2)]
        private int ORA_ZeroPageX()
        {
            byte arg = console.memory.read8((byte)((argOne() + _X) & 0xFF));
            _A = (byte)(_A | arg);

            setNegativeForOperand(_A);
            setZeroForOperand(_A);

            _PC += 2;
            return 4;
        }

        [OpCode(opcode = 0x0D, name = "ORA", bytes = 3)]
        private int ORA_Absolute()
        {
            byte arg = console.memory.read8(argOne16());
            _A = (byte)(_A | arg);

            setNegativeForOperand(_A);
            setZeroForOperand(_A);

            _PC += 3;
            return 4;
        }

        [OpCode(opcode = 0x1D, name = "ORA", bytes = 3)]
        private int ORA_AbsoluteX()
        {
            return ORA_AbsoluteWithRegister(_X);
        }

        [OpCode(opcode = 0x19, name = "ORA", bytes = 3)]
        private int ORA_AbsoluteY()
        {
            return ORA_AbsoluteWithRegister(_Y);
        }

        [OpCode(opcode = 0x01, name = "ORA", bytes = 2)]
        private int ORA_IndirectX()
        {
            ushort address = (ushort)((argOne() + _X) & 0xFF);
            ushort indirectAddress = console.memory.read16(address);

            byte arg = console.memory.read8(indirectAddress);
            _A = (byte)(_A | arg);

            setNegativeForOperand(_A);
            setZeroForOperand(_A);

            _PC += 2;
            return 6;
        }

        [OpCode(opcode = 0x11, name = "ORA", bytes = 2)]
        private int ORA_IndirectY()
        {
            ushort address = console.memory.read16(argOne(), true);
            ushort addressWithY = (ushort)(address + _Y);

            byte arg = console.memory.read8(addressWithY);
            _A = (byte)(_A | arg);

            setNegativeForOperand(_A);
            setZeroForOperand(_A);

            _PC += 2;
            if (samePage(address, addressWithY))
            {
                return 5;
            }
            else
            {
                return 6;
            }
        }

        private int ORA_AbsoluteWithRegister(byte registerValue)
        {
            ushort address = argOne16();
            ushort addressWithY = (ushort)(address + registerValue);
            byte arg = console.memory.read8(addressWithY);

            _A = (byte)(_A | arg);

            setNegativeForOperand(_A);
            setZeroForOperand(_A);

            _PC += 3;

            if (samePage(address, addressWithY))
            {
                return 4;
            }
            else
            {
                return 5;
            }
        }
        #endregion

        #region ROL
        [OpCode(opcode = 0x2A, name = "ROL", bytes = 1)]
        private int ROL_Accumulator()
        {
            byte val = _A;
            byte oldCarry = (byte)(getFlag(StatusFlag.Carry) != 0 ? 1 : 0);
            byte newCarry = (byte)((val & 0x80) >> 7);
            val = (byte)((val << 1) | oldCarry);
            _A = val;

            setFlag(StatusFlag.Carry, newCarry);
            setNegativeForOperand(val);
            setZeroForOperand(val);

            _PC += 1;
            return 2;
        }

        [OpCode(opcode = 0x26, name = "ROL", bytes = 2)]
        private int ROL_ZeroPage()
        {
            ushort address = argOne();
            byte val = console.memory.read8(address);
            byte oldCarry = (byte)(getFlag(StatusFlag.Carry) != 0 ? 1 : 0);
            byte newCarry = (byte)((val & 0x80) >> 7);
            val = (byte)((val << 1) | oldCarry);
            console.memory.write8(address, val);

            setFlag(StatusFlag.Carry, newCarry);
            setNegativeForOperand(val);
            setZeroForOperand(val);

            _PC += 2;
            return 5;
        }

        [OpCode(opcode = 0x36, name = "ROL", bytes = 2)]
        private int ROL_ZeroPageX()
        {
            ushort address = (byte)((argOne() + _X) & 0xFF);
            byte val = console.memory.read8(address);
            byte oldCarry = (byte)(getFlag(StatusFlag.Carry) != 0 ? 1 : 0);
            byte newCarry = (byte)((val & 0x80) >> 7);
            val = (byte)((val << 1) | oldCarry);
            console.memory.write8(address, val);

            setFlag(StatusFlag.Carry, newCarry);
            setNegativeForOperand(val);
            setZeroForOperand(val);

            _PC += 2;
            return 6;
        }

        [OpCode(opcode = 0x2E, name = "ROL", bytes = 3)]
        private int ROL_Absolute()
        {
            ushort address = argOne16();
            byte val = console.memory.read8(address);
            byte oldCarry = (byte)(getFlag(StatusFlag.Carry) != 0 ? 1 : 0);
            byte newCarry = (byte)((val & 0x80) >> 7);
            val = (byte)((val << 1) | oldCarry);
            console.memory.write8(address, val);

            setFlag(StatusFlag.Carry, newCarry);
            setNegativeForOperand(val);
            setZeroForOperand(val);

            _PC += 3;
            return 6;
        }

        [OpCode(opcode = 0x3E, name = "ROL", bytes = 3)]
        private int ROL_AbsoluteX()
        {
            ushort address = (ushort)(argOne16() + _X);
            byte val = console.memory.read8(address);
            byte oldCarry = (byte)(getFlag(StatusFlag.Carry) != 0 ? 1 : 0);
            byte newCarry = (byte)((val & 0x80) >> 7);
            val = (byte)((val << 1) | oldCarry);
            console.memory.write8(address, val);

            setFlag(StatusFlag.Carry, newCarry);
            setNegativeForOperand(val);
            setZeroForOperand(val);

            _PC += 3;
            return 7;
        }
        #endregion

        #region ROR
        [OpCode(opcode = 0x6A, name = "ROR", bytes = 1)]
        private int ROR_Accumulator()
        {
            byte oldCarry = (byte)(getFlag(StatusFlag.Carry) != 0 ? 1 : 0);
            byte newCarry = (byte)(_A & 1);
            _A = (byte)((_A >> 1) | (oldCarry << 7));

            setFlag(StatusFlag.Carry, newCarry);
            setNegativeForOperand(_A);
            setZeroForOperand(_A);

            _PC += 1;
            return 2;
        }

        [OpCode(opcode = 0x66, name = "ROR", bytes = 2)]
        private int ROR_ZeroPage()
        {
            ushort address = argOne();
            byte val = console.memory.read8(address);
            byte oldCarry = (byte)(getFlag(StatusFlag.Carry) != 0 ? 1 : 0);
            byte newCarry = (byte)(val & 1);
            val = (byte)((val >> 1) | (oldCarry << 7));
            console.memory.write8(address, val);

            setFlag(StatusFlag.Carry, newCarry);
            setNegativeForOperand(val);
            setZeroForOperand(val);

            _PC += 2;
            return 5;
        }

        [OpCode(opcode = 0x76, name = "ROR", bytes = 2)]
        private int ROR_ZeroPageX()
        {
            ushort address = (ushort)((argOne() + _X) & 0xFF);
            byte val = console.memory.read8(address);
            byte oldCarry = (byte)(getFlag(StatusFlag.Carry) != 0 ? 1 : 0);
            byte newCarry = (byte)(val & 1);
            val = (byte)((val >> 1) | (oldCarry << 7));
            console.memory.write8(address, val);

            setFlag(StatusFlag.Carry, newCarry);
            setNegativeForOperand(val);
            setZeroForOperand(val);

            _PC += 2;
            return 6;
        }

        [OpCode(opcode = 0x6E, name = "ROR", bytes = 3)]
        private int ROR_Absolute()
        {
            ushort address = argOne16();
            byte val = console.memory.read8(address);
            byte oldCarry = (byte)(getFlag(StatusFlag.Carry) != 0 ? 1 : 0);
            byte newCarry = (byte)(val & 1);
            val = (byte)((val >> 1) | (oldCarry << 7));
            console.memory.write8(address, val);

            setFlag(StatusFlag.Carry, newCarry);
            setNegativeForOperand(val);
            setZeroForOperand(val);

            _PC += 3;
            return 6;
        }

        [OpCode(opcode = 0x7E, name = "ROR", bytes = 3)]
        private int ROR_AbsoluteX()
        {
            ushort address = (ushort)(argOne16() + _X);
            byte val = console.memory.read8(address);
            byte oldCarry = (byte)(getFlag(StatusFlag.Carry) != 0 ? 1 : 0);
            byte newCarry = (byte)(val & 1);
            val = (byte)((val >> 1) | (oldCarry << 7));
            console.memory.write8(address, val);

            setFlag(StatusFlag.Carry, newCarry);
            setNegativeForOperand(val);
            setZeroForOperand(val);

            _PC += 3;
            return 7;
        }
        #endregion

        #endregion

        #region OpcodeHelpers
        private byte argOne()
        {
            return console.memory.read8((ushort)(_PC + 1));
        }

        private ushort argOne16()
        {
            return console.memory.read16((ushort)(_PC + 1));
        }

        private byte argTwo()
        {
            return console.memory.read8((ushort)(_PC + 2));
        }

        private void pushStack16(ushort val)
        {
            console.memory.write16(stackAddressOf((byte)(_S - 1)), (ushort)(val));
            _S -= 2;
        }

        private ushort popStack16()
        {
            _S += 2;
            return (ushort)(console.memory.read16(stackAddressOf((byte)(_S - 1))));
        }

        private void pushStack8(byte val)
        {
            console.memory.write8(stackAddressOf(_S), val);
            _S -= 1;
        }

        private byte popStack8()
        {
            _S += 1;
            return console.memory.read8(stackAddressOf(_S));
        }

        private ushort stackAddressOf(byte stackPointer)
        {
            return (ushort)(0x0100 + stackPointer);
        }

        private bool samePage(ushort addressOne, ushort addressTwo)
        {
            return ((addressOne ^ addressTwo) & 0xFF00) == 0;
        }

        private void setOverflowForOperands(byte val1, byte val2, int result)
        {
            // If the sign of the result differs from both inputs, overflow! (Work it out in a table)
            //bool overflowCheck = (((val2 ^ val1) & (result ^ val2)) & 0x80) == 1;
            bool overflowCheck = (val1 & 0x80) == (val2 & 0x80) && (val1 & 0x80) != (result & 0x80);
            setFlag(StatusFlag.Overflow, (byte)(overflowCheck ? 1 : 0));
        }

        private void setCarryForResult(int result)
        {
            setFlag(StatusFlag.Carry, (result >> 8) != 0 ? (byte)1 : (byte)0);
        }

        private void setZeroForOperand(byte operand)
        {
            setFlag(StatusFlag.Zero, operand == 0 ? (byte)1 : (byte)0);
        }

        private void setNegativeForOperand(byte operand)
        {
            setFlag(StatusFlag.Negative, (byte)((operand >> 7)));
        }
        #endregion

        #endregion

        #region System Startup and Reset

        /// <summary>
        /// Set up the CPU to be in the state it would be after a normal power cycle.
        /// Details sampled from physical hardware: http://wiki.nesdev.com/w/index.php/CPU_power_up_state
        /// </summary>
        public void coldBoot()
        {
            _P = 0x34;
            _A = _X = _Y = 0;
            _S = 0xFD;

            jumpToResetVector();

            // Set up some of the memory-mapped stuff
            console.memory.write8(0x4017, 0x00); // (frame irq enabled)
            console.memory.write8(0x4015, 0x00); // (all channels disabled)
        }

        /// <summary>
        /// Set up the CPU to be in the state it would be after the reset line is held high. (e.g. User presses the RESET button on an NES)
        /// Details sampled from physical hardware: http://wiki.nesdev.com/w/index.php/CPU_power_up_state
        /// </summary>
        public void warmBoot()
        {
            jumpToResetVector();

            // S was decremented by 3(but nothing was written to the stack)
            _S -= 3;

            // The I (IRQ disable) flag was set to true(status ORed with $04)
            setFlag(StatusFlag.InterruptDisable, 1);

            // Silence the APU
            console.memory.write8(0x4015, 0x00);
        }

        private void jumpToResetVector()
        {
            // Jump to location pointed at by the reset vector
            _PC = console.memory.read16(0xFFFC);
            log.info("Jumped to Reset Vector @ {0:X}", _PC);
        }

        private void jumpToNMIVector()
        {
            // Jump to location pointed at by the reset vector
            pushStack16(_PC);
            pushStack8(_P);


            _PC = console.memory.read16(0xFFFA);
            log.info("Jumped to NMI Vector @ {0:X}", _PC);
        }

        #endregion


        public CPU(NESConsole console)
        {
            this.console = console;

            initializeOpCodeTable();
        }

        /// <summary>
        /// Execute the next CPU instruction and return the number of cycles used.
        /// </summary>
        /// <returns></returns>
        public int step()
        {
            if (nmi)
            {
                nmi = false;
                jumpToNMIVector();

                // Assuming that NMI consumes one cycle. Confirm somewhere?
                return 1;
            }

            byte opcode = console.memory.read8(_PC);

            MethodInfo opcodeMethod = opcodeFunctions[opcode];
            if (opcodeMethod == null)
            {
                log.error("Unknown opcode {0:X} encountered @ {1:X4}.", opcode, _PC);
                throw new NotImplementedException();
            }

            if (log.IsEnabled)
                printCPUState();

            return (int)opcodeMethod.Invoke(this, null);
        }

        public void printCPUState()
        {
            byte opcode = console.memory.read8(_PC);
            MethodInfo opcodeMethodInfo = opcodeFunctions[opcode];
            //if (opcodeMethodInfo == null) return;

            OpCodeAttribute opcodeMethodAttribute = Attribute.GetCustomAttribute(opcodeMethodInfo, typeof(OpCodeAttribute), false) as OpCodeAttribute;

            string format = "{0,-9} [{1:X2} {2:X2} {3:X2} {4:X2} {5}] $CYAN${6:X4}$RESET$ $RED${7}";

            int opcodeBytes = opcodeMethodAttribute.bytes;
            for (int i = 8; i < 8 + 5; ++i)
            {
                format += " {" + i + ":X2}";

                if ((i - 8) + 1 == opcodeBytes)
                {
                    format += "$RESET$";
                }
            }

            // Make a human-readable form of the status register
            // NVssDIZC
            string Pstring = "";
            Pstring += getFlag(StatusFlag.Negative) != 0 ? "N" : ".";
            Pstring += getFlag(StatusFlag.Overflow) != 0 ? "V" : ".";
            Pstring += "...";
            Pstring += getFlag(StatusFlag.InterruptDisable) != 0 ? "I" : ".";
            Pstring += getFlag(StatusFlag.Zero) != 0 ? "Z" : ".";
            Pstring += getFlag(StatusFlag.Carry) != 0 ? "C" : ".";

            // Top two bytes on the stack
            format += " | {13:X2} {14:X2}";
            byte stack_top = console.memory.read8(stackAddressOf((byte)((_S + 1) & 0xFF)));
            byte stack_top_minus_1 = console.memory.read8(stackAddressOf((byte)((_S + 2) & 0xFF)));

            log.info(format, console.CPUCyclesExecuted, _A, _X, _Y, _S, Pstring, _PC, opcodeMethodAttribute.name, opcode, console.memory.read8((ushort)(_PC + 1)), console.memory.read8((ushort)(_PC + 2)), console.memory.read8((ushort)(_PC + 3)), console.memory.read8((ushort)(_PC + 4)), stack_top, stack_top_minus_1);
        }

    }
}
