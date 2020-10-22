namespace DotNES
{
    class Program
    {
        static void Main(string[] args)
        {
            using (NESEmulator emulator = new NESEmulator())
            {
                emulator.Run();
            }
        }
    }
}
