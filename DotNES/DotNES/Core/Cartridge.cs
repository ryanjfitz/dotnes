﻿using DotNES.Mappers;
using DotNES.Utilities;
using System;
using System.IO;

namespace DotNES.Core
{
    public class Cartridge
    {
        private Logger log = new Logger("Cartridge");
        public byte[] PRGRomData;
        public byte[] CHRRomData;

        public int PRGROM_16KBankCount { get; private set; }
        public int PRGRAM_8KBankCount { get; private set; }
        public int CHRROM_8KBankCount { get; private set; }
        public int MapperNumber { get; private set; }
        public bool BatteryBackedRAM { get; private set; }

        public NametableMirroringMode NametableMirroring { get; set; }

        #region iNES ROM Loader

        // iNES File Signature : ['N', 'E', 'S', EOF] in little-endian
        static readonly uint INES_FILE_SIGNATURE = 0x1A53454E;

        /// <summary>
        /// Load a rom file using the iNES file format, the de facto NES rom file format.
        /// http://wiki.nesdev.com/w/index.php/INES
        /// </summary>
        /// <param name="romPath"></param>
        private void loadRomData(string romPath)
        {
            byte[] fullRomData = File.ReadAllBytes(romPath);
            byte[] header = new ArraySegment<byte>(fullRomData, 0, 16).ToArray();

            // First confirm this is actually an iNES rom...
            uint fileSignature = BitConverter.ToUInt32(header, 0);
            if (fileSignature != INES_FILE_SIGNATURE)
            {
                log.error("Did not find expected iNES Rom File Signature");
                throw new InvalidDataException();
            }

            PRGROM_16KBankCount = header[4];
            PRGRAM_8KBankCount = header[8];
            CHRROM_8KBankCount = header[5];

            // iNES has a number of flags all rolled up into 4 flag bytes. 
            int flag6 = header[6];
            int flag7 = header[7];
            int flag9 = header[9];
            int flag10 = header[10];

            NametableMirroring = (flag6 & 1) == 1 ? NametableMirroringMode.Vertical : NametableMirroringMode.Horizontal;

            bool usesTrainer = (flag6 & 4) != 0;
            if (usesTrainer)
            {
                log.error("ROM uses a trainer. This is unsupported.");
                throw new NotImplementedException();
            }

            if ((flag6 & 2) == 1)
            {
                BatteryBackedRAM = true;
            }

            MapperNumber = flag6 >> 4 | (flag7 & 0xf0);

            // Now that we know all the 'metadata' about the file, load the actual data.
            int prgStart = 16 + (usesTrainer ? 512 : 0);
            int prgBytes = 0x4000 * PRGROM_16KBankCount;
            PRGRomData = new ArraySegment<byte>(fullRomData, prgStart, prgBytes).ToArray();

            int chrStart = prgStart + prgBytes;
            int chrBytes = 0x2000 * CHRROM_8KBankCount;
            CHRRomData = new ArraySegment<byte>(fullRomData, chrStart, chrBytes).ToArray();

            log.info("ROM Details --");
            log.info(" * Mapper #{0}", MapperNumber);
            log.info(" * Nametable mirroring mode is {0}.", NametableMirroring.ToString());
            if (BatteryBackedRAM) log.info(" * Battery-backed RAM ('Game Saves' supported)");

            log.info(" * ROM Bank Data");
            log.info("   - {0}x 16 KB PRG ROM", PRGROM_16KBankCount);
            log.info("   - {0}x 8 KB PRG RAM", PRGRAM_8KBankCount);
            log.info("   - {0}x 8 KB CHR ROM", CHRROM_8KBankCount);

            log.info("Finished loading '{0}'.", romPath);
        }


        #endregion

        public Cartridge(string romPath)
        {
            loadRomData(romPath);
        }

        public Mapper getMapper()
        {
            switch (MapperNumber)
            {
                case 0:
                    return new Mapper000(this);
                case 1:
                    return new Mapper001(this);
                case 2:
                    return new Mapper002(this);
                case 3:
                    return new Mapper003(this);
                default:
                    break;
            }
            throw new NotImplementedException();
        }
    }
}
