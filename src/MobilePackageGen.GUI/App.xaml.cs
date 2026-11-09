using System.Runtime.InteropServices;
using System.Windows;

namespace MobilePackageGen.GUI;

public partial class App : Application
{
    [DllImport("kernel32.dll")]
    private static extern bool AllocConsole();

    [DllImport("kernel32.dll")]
    private static extern bool FreeConsole();

    protected override void OnStartup(StartupEventArgs e)
    {
        try { AllocConsole(); } catch { }

        if (!TrustedInstallerHelper.IsTrustedInstaller())
        {
            try
            {
                TrustedInstallerHelper.ElevateSelf(Environment.ProcessPath!, e.Args);
                Shutdown();
                return;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Warning] Failed to elevate to TrustedInstaller: {ex.Message}");
            }
        }

        base.OnStartup(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try { FreeConsole(); } catch { }
        base.OnExit(e);
    }
}
