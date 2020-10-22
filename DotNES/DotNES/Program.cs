using System;
using System.Windows.Forms;

namespace DotNES
{
    class Program
    {
        [STAThread]
        static void Main(string[] args)
        {
            using (OpenFileDialog openFileDialog = new OpenFileDialog { Filter = "iNES ROM Images|*.nes" })
                if (openFileDialog.ShowDialog() == DialogResult.OK)
                    using (NESEmulator emulator = new NESEmulator(openFileDialog.FileName))
                        emulator.Run();
        }
    }
}
