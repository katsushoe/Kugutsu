using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security;
using Kugutsu.Contracts;
using Kugutsu.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;

namespace Kugutsu.App;

internal sealed record InstalledBrowser(string Browser, string DisplayName, string? Version, string ExecutablePath,
    bool Supported, bool? HttpDefault, bool? HttpsDefault, bool DriverReady, string DriverReason);

/// <summary>ブラウザ登録と設定済みパスを読み、起動せずに存在と版を確認する。</summary>
internal sealed class BrowserDiscovery(KugutsuOptions settings, BrowserDefaults defaults, ILogger<BrowserDiscovery> logger)
{
    private const int Limit = 1024;
    private sealed record Candidate(string Path, string? Name);

    internal OperationDefinition Definition() => new("browser.list_installed", ArgumentSchema.Object([]),
        async (_, token) => ResponseJson.ToElement(await DiscoverAsync(token)));

    private async ValueTask<object> DiscoverAsync(CancellationToken token)
    {
        var warnings = new HashSet<string>(StringComparer.Ordinal);
        BrowserDefaultResult? current = null;
        try { current = await defaults.ReadAsync(token); }
        catch (WindowReferenceException error) when (error.Code == ErrorCode.BROWSER_DISCOVERY_FAILED)
        { Warn("default_query_failed", warnings); }
        return await Task.Run(() => Discover(current, warnings, token), token);
    }

    private object Discover(BrowserDefaultResult? current, HashSet<string> warnings, CancellationToken token)
    {
        var candidates = new List<Candidate>();
        var completedSources = 0;
        foreach (var hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
        foreach (var view in new[] { RegistryView.Registry32, RegistryView.Registry64 })
        {
            token.ThrowIfCancellationRequested();
            try
            {
                using var root = RegistryKey.OpenBaseKey(hive, view);
                ReadClients(root, candidates, warnings, token);
                ReadAppPaths(root, candidates, warnings, token);
                completedSources++;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or SecurityException)
            { Warn("registry_source_unavailable", warnings); }
        }
        foreach (var mapping in settings.BrowserLaunchTargets)
        {
            var target = settings.AllowedLaunchTargets.FirstOrDefault(item => item.Id == mapping.Value);
            if (target is not null) { Add(candidates, new(target.ExecutablePath, null), warnings); }
        }
        if (completedSources == 0 && settings.BrowserLaunchTargets.Count == 0)
        { throw new WindowReferenceException(ErrorCode.BROWSER_DISCOVERY_FAILED); }
        var browsers = new Dictionary<string, InstalledBrowser>(StringComparer.OrdinalIgnoreCase);
        foreach (var candidate in candidates)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                var path = Normalize(candidate.Path);
                if (path is null) { Warn("nonlocal_or_invalid_path", warnings); continue; }
                if (browsers.ContainsKey(path)) { continue; }
                if (HasReparseAncestor(path)) { Warn("executable_alias_unresolved", warnings); continue; }
                var info = new FileInfo(path);
                if (!info.Exists) { Warn("registered_executable_missing", warnings); continue; }
                if ((info.Attributes & FileAttributes.ReparsePoint) != 0)
                { Warn("executable_alias_unresolved", warnings); continue; }
                var version = FileVersionInfo.GetVersionInfo(path);
                var browser = BrowserDefaults.Identify(path);
                var name = candidate.Name ?? version.ProductName ?? Path.GetFileName(path);
                browsers.Add(path, new(browser, Bound(name), BoundNullable(version.FileVersion), path, browser != "other",
                    IsDefault(current?.Http, path), IsDefault(current?.Https, path), false, DriverReason(browser, warnings)));
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or SecurityException
                or ArgumentException or NotSupportedException)
            { Warn("executable_metadata_unavailable", warnings); }
        }
        token.ThrowIfCancellationRequested();
        return new { browsers = browsers.Values.OrderBy(item => item.ExecutablePath, StringComparer.OrdinalIgnoreCase).ToArray(),
            warnings = warnings.Order(StringComparer.Ordinal).ToArray(),
            detectionScope = new[] { "current_user_and_local_machine", "registry_32_and_64", "start_menu_internet", "known_app_paths", "configured_launch_targets" },
            portableDiscovery = "configured_paths_only" };
    }

    private void ReadClients(RegistryKey root, List<Candidate> candidates, HashSet<string> warnings, CancellationToken token)
    {
        using var clients = root.OpenSubKey(@"SOFTWARE\Clients\StartMenuInternet", false);
        if (clients is null) { return; }
        var names = clients.GetSubKeyNames();
        if (names.Length > Limit) { Warn("registration_limit_exceeded", warnings); }
        foreach (var name in names.Take(Limit))
        {
            token.ThrowIfCancellationRequested();
            try
            {
                using var client = clients.OpenSubKey(name, false);
                using var command = client?.OpenSubKey(@"shell\open\command", false);
                var value = command?.GetValue(null) as string;
                if (value is null) { continue; }
                var path = CommandExecutable(value);
                if (path is null) { Warn("registered_command_unresolved", warnings); continue; }
                Add(candidates, new(path, client?.GetValue(null) as string), warnings);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or SecurityException)
            { Warn("registry_entry_unavailable", warnings); }
        }
    }

    private void ReadAppPaths(RegistryKey root, List<Candidate> candidates, HashSet<string> warnings, CancellationToken token)
    {
        foreach (var name in new[] { "chrome.exe", "msedge.exe", "firefox.exe" })
        {
            token.ThrowIfCancellationRequested();
            using var key = root.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\" + name, false);
            if (key?.GetValue(null) is string path) { Add(candidates, new(path.Trim().Trim('"'), null), warnings); }
        }
    }

    private void Add(List<Candidate> candidates, Candidate candidate, HashSet<string> warnings)
    {
        if (candidates.Count >= Limit) { Warn("candidate_limit_exceeded", warnings); return; }
        candidates.Add(candidate);
    }

    private string DriverReason(string browser, HashSet<string> warnings)
    {
        if (browser == "other") { return "unsupported_browser"; }
        if (!settings.BrowserDriverPaths.TryGetValue(browser, out var path)) { return "not_configured"; }
        var normalized = Normalize(path);
        if (normalized is null) { return "driver_path_invalid"; }
        try { return File.Exists(normalized) ? "compatibility_not_verified" : "driver_missing"; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or SecurityException)
        { Warn("driver_metadata_unavailable", warnings); return "driver_unavailable"; }
    }

    private static bool? IsDefault(BrowserDefault? handler, string path)
    {
        if (handler is null || handler.ExecutablePath is null) { return null; }
        var normalized = Normalize(handler.ExecutablePath);
        return normalized is null ? null : string.Equals(normalized, path, StringComparison.OrdinalIgnoreCase);
    }

    private static string? Normalize(string path)
    {
        if (path.Length is < 4 or > 32767 || path.Contains('\0') || !char.IsAsciiLetter(path[0])
            || path[1] != ':' || path[2] != '\\' || !path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) { return null; }
        return Path.GetFullPath(path);
    }

    private static string? CommandExecutable(string command)
    {
        if (string.IsNullOrWhiteSpace(command) || command.Length > 32767 || command.Contains('\0')) { return null; }
        var pointer = CommandLineToArgv(command, out var count);
        if (pointer == 0) { return null; }
        try { return count is > 0 and <= Limit ? Marshal.PtrToStringUni(Marshal.ReadIntPtr(pointer)) : null; }
        finally { LocalFree(pointer); }
    }

    private static bool HasReparseAncestor(string path)
    {
        var directory = Path.GetDirectoryName(path);
        while (directory is not null)
        {
            if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0) { return true; }
            directory = Path.GetDirectoryName(directory);
        }
        return false;
    }

    private static string Bound(string value) => value.Length <= 4096 ? value : value[..4096];
    private static string? BoundNullable(string? value) => value is null ? null : Bound(value);
    private void Warn(string code, HashSet<string> warnings)
    {
        if (warnings.Add(code)) { logger.LogWarning("[BrowserDiscovery] {Code}", code); }
    }

    [DllImport("shell32.dll", EntryPoint = "CommandLineToArgvW", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern nint CommandLineToArgv(string command, out int count);
    [DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern nint LocalFree(nint memory);
}
