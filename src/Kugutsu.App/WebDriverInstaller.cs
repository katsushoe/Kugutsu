using System.Diagnostics;
using Kugutsu.Hosting;

namespace Kugutsu.App;

/// <summary>Runs the bundled official WebDriver setup script without exposing output.</summary>
internal static class WebDriverInstaller
{
    public static async Task<int> RunAsync(InstallationPaths paths)
    {
        var script = Path.Combine(paths.BinDirectory, "Install-WebDrivers.ps1");
        var powershell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");
        if (!File.Exists(script) || !File.Exists(powershell)) { return 1; }
        var start = new ProcessStartInfo(powershell)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = paths.BinDirectory,
            Arguments = $"-NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"{script}\" -InstallationRoot \"{paths.RootDirectory}\""
        };
        using var process = Process.Start(start);
        if (process is null) { return 1; }
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(8));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); }
            catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException) { }
            return 1;
        }
        return process.ExitCode;
    }
}
