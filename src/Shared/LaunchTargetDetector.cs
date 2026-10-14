using System.Text.Json;
using Microsoft.Win32;

namespace Kugutsu.Hosting;

/// <summary>Detects installed launch targets at setup and writes only targets that exist.</summary>
internal static class LaunchTargetDetector
{
    private sealed record DetectedConfiguration(LaunchTargetOptions[] AllowedLaunchTargets,
        Dictionary<string, string> BrowserLaunchTargets, string? TerminalLaunchTargetId,
        string? PowerShellLaunchTargetId, string? ConsoleHostLaunchTargetId);

    public static void WriteConfiguration(InstallationPaths paths)
    {
        var targets = new List<LaunchTargetOptions>();
        var browsers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        AddPath(targets, "terminal", ResolveAppPath("wt.exe") ?? FindExecutable("wt.exe"), out var terminal);
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        AddPath(targets, "powershell", Path.Combine(windows, "System32", "WindowsPowerShell", "v1.0", "powershell.exe"), out var shell);
        AddPath(targets, "consolehost", Path.Combine(windows, "System32", "conhost.exe"), out var host);
        foreach (var browser in new[] { "firefox", "chrome", "edge" })
        {
            var executable = ResolveAppPath(browser + ".exe") ?? FindBrowserPath(browser);
            if (AddPath(targets, "browser-" + browser, executable, out var id)) { browsers[browser] = id!; }
        }

        Directory.CreateDirectory(paths.ConfigDirectory);
        var configuration = new DetectedConfiguration(targets.ToArray(), browsers, terminal, shell, host);
        var json = JsonSerializer.Serialize(configuration, new JsonSerializerOptions { WriteIndented = true });
        var temporary = paths.DetectedLaunchTargetsFile + ".tmp";
        File.WriteAllText(temporary, json);
        File.Move(temporary, paths.DetectedLaunchTargetsFile, true);
    }

    private static bool AddPath(List<LaunchTargetOptions> targets, string id, string? path, out string? targetId)
    {
        targetId = null;
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) { return false; }
        targets.Add(new LaunchTargetOptions { Id = id, ExecutablePath = Path.GetFullPath(path) });
        targetId = id;
        return true;
    }

    private static string? ResolveAppPath(string executable)
    {
        foreach (var hive in new[] { Registry.LocalMachine, Registry.CurrentUser })
        foreach (var view in new[] { RegistryView.Registry32, RegistryView.Registry64 })
        {
            using var baseKey = RegistryKey.OpenBaseKey(hive.Name == Registry.LocalMachine.Name ? RegistryHive.LocalMachine : RegistryHive.CurrentUser, view);
            using var key = baseKey.OpenSubKey($@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\{executable}");
            var value = key?.GetValue(null) as string;
            value = value?.Trim().Trim('"');
            if (!string.IsNullOrWhiteSpace(value) && File.Exists(value)) { return value; }
        }
        return null;
    }

    private static string? FindExecutable(string name)
    {
        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(directory.Trim('"'), name);
            if (File.Exists(candidate)) { return candidate; }
        }
        return null;
    }

    private static string? FindBrowserPath(string browser)
    {
        var candidates = browser switch
        {
            "firefox" => new[] { Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Mozilla Firefox", "firefox.exe"), Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Mozilla Firefox", "firefox.exe") },
            "chrome" => new[] { Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Google", "Chrome", "Application", "chrome.exe"), Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Google", "Chrome", "Application", "chrome.exe"), Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Google", "Chrome", "Application", "chrome.exe") },
            _ => new[] { Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Microsoft", "Edge", "Application", "msedge.exe"), Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Microsoft", "Edge", "Application", "msedge.exe") }
        };
        return candidates.FirstOrDefault(File.Exists);
    }
}
