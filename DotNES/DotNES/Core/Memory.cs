﻿namespace DotNES.Core
{
    public class Memory
    {
        private byte[] RAM;
        private NESConsole console;

        public Memory(NESConsole console)
        {
            this.console = console;
            RAM = new byte[0x800];
        }

        /// <summary>
        /// Write a byte to system memory. This might be internal RAM ($0000-$07FF), or 
        /// could refer to memory-mapped devices, e.g. mappers, APU, PPU, etc.
        /// </summary>
        /// <param name="addr">The 16-bit address at which to set a value.</param>
        /// <param name="val">The byte to write at the given location.</param>
        public void write8(ushort addr, byte val)
        {
            if (addr < 0x2000)
            {
                RAM[addr & 0x7FF] = val;
            }
            else if (addr < 0x4000)
            {
                // 0x2000 - 0x2007 repeats every 8 bytes up until 0x3FFF
                console.ppu.write((ushort)(0x2000 + (addr & 7)), val);
            }
            else if (addr == 0x4014)
            {
                // 0x4014 - writing $XX initiates OAM DMA from $XX00-$XXFF to PPU OAM Memory
                console.ppu.write(addr, val);
            }
            else if (addr < 0x4014 || addr == 0x4015 || addr == 0x4017)
            {
                console.apu.write(addr, val);
            }
            else if (addr == 0x4016)
            {
                console.io.write(addr, val);
            }
            else
            {
                console.mapper.write(addr, val);
            }
        }

        public byte read8(ushort addr)
        {
            if (addr < 0x2000)
            {
                return RAM[addr & 0x7FF];
            }
            else if (addr < 0x4000)
            {
                // 0x2000 - 0x2007 repeats every 8 bytes up until 0x3FFF
                return console.ppu.read((ushort)(0x2000 + (addr & 7)));
            }
            else if (addr < 0x4016)
            {
                return console.apu.read(addr);
            }
            else if (addr < 0x4018)
            {
                return console.io.read(addr);
            }
            else
            {
                return console.mapper.read(addr);
            }
        }

        /// <summary>
        /// Upper and lower bytes of val are swapped before writing.
        /// </summary>
        /// <param name="addr"></param>
        /// <param name="val"></param>
        public void write16(ushort addr, ushort val)
        {
            write8(addr, (byte)(val & 0xFF));
            write8((ushort)(addr + 1), (byte)((val & 0xFF00) >> 8));
        }

        /// <summary>
        /// Upper and lower bytes of val are swapped after reading.
        /// </summary>
        /// <param name="addr"></param>
        /// <returns></returns>
        public ushort read16(ushort addr, bool pageWrap = false)
        {
            if (pageWrap)
            {
                ushort lowByte = addr;
                ushort highByte = (ushort)((addr & 0xFF) == 0xFF ? addr & 0xFF00 : addr + 1);

                return (ushort)((read8(highByte) << 8) | read8(lowByte));
            }
            else
            {
                return (ushort)((read8((ushort)(addr + 1)) << 8) | read8(addr));
            }

        }
    }
}
