using System.Runtime.InteropServices;

namespace MobilePackageGen.GUI
{
    internal static class Program
    {
        [DllImport("kernel32.dll")]
        private static extern bool AllocConsole();

        [DllImport("kernel32.dll")]
        private static extern bool FreeConsole();

        [STAThread]
        static void Main()
        {
            try { AllocConsole(); } catch { }
            ApplicationConfiguration.Initialize();
            Application.Run(new MainForm());
            try { FreeConsole(); } catch { }
        }
    }
}
