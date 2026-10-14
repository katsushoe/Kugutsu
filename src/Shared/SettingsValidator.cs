using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Kugutsu.Hosting;

/// <summary>Validates configuration before either executable continues startup.</summary>
internal static class SettingsValidator
{
    private static readonly string[] Drivers = ["uia", "imgui_visual", "generic_visual"];
    private static readonly string[] Browsers = ["firefox", "chrome", "edge"];

    /// <summary>Rejects unknown keys, duplicate keys and incorrect JSON shapes.</summary>
    public static void ValidateDocument(JsonElement root)
    {
        Check(root.ValueKind == JsonValueKind.Object, "document");
        var found = false;
        foreach (var property in root.EnumerateObject())
        {
            Check(!found && property.Name.Equals("Kugutsu", StringComparison.OrdinalIgnoreCase), "document");
            Object(property.Value, typeof(KugutsuOptions), "Kugutsu");
            found = true;
        }
        Check(found, "Kugutsu");
    }

    /// <summary>Validates ranges, supported identifiers, paths and cross-field constraints.</summary>
    public static void Validate(KugutsuOptions options, InstallationPaths paths)
    {
        Range(options.QueueCapacity, 1, 1000, nameof(options.QueueCapacity));
        Range(options.MaxBatchOperations, 1, 10000, nameof(options.MaxBatchOperations));
        Range(options.MaxAloeInstructions, 1, 10000000, nameof(options.MaxAloeInstructions));
        Range(options.AloeMemoryLimitMiB, 128, 1024, nameof(options.AloeMemoryLimitMiB));
        Range(options.MaxRequestBytes, 65536, 16777216, nameof(options.MaxRequestBytes));
        Range(options.MaxClientSessions, 1, 10000, nameof(options.MaxClientSessions));
        Range(options.OperationTimeoutSeconds, 1, 300, nameof(options.OperationTimeoutSeconds));
        Range(options.RequestTimeoutSeconds, 1, 3600, nameof(options.RequestTimeoutSeconds));
        Range(options.QueueTimeoutSeconds, 1, 3600, nameof(options.QueueTimeoutSeconds));
        Range(options.McpLockLeaseSeconds, 10, 3600, nameof(options.McpLockLeaseSeconds));
        Range(options.McpLockAcquireTimeoutSeconds, 0, 300, nameof(options.McpLockAcquireTimeoutSeconds));
        Range(options.MaxSignals, 1, 1000, nameof(options.MaxSignals));
        Range(options.SignalRetentionSeconds, 1, 3600, nameof(options.SignalRetentionSeconds));
        Range(options.SignalPollIntervalMilliseconds, 50, 5000, nameof(options.SignalPollIntervalMilliseconds));
        Check(options.OcrLanguage is "eng" or "jpn" or "jpn+eng", nameof(options.OcrLanguage));
        Range(options.OcrMaxWords, 1, 10000, nameof(options.OcrMaxWords));
        Range(options.VisualEdgeContrast, 1, 255, nameof(options.VisualEdgeContrast));
        Range(options.VisualSnapshotLifetimeSeconds, 1, 60, nameof(options.VisualSnapshotLifetimeSeconds));
        Range(options.MaxUiCandidates, 1, 1000, nameof(options.MaxUiCandidates));
        Range(options.ResultRetentionMinutes, 1, 1440, nameof(options.ResultRetentionMinutes));
        Range(options.LogRetentionDays, 1, 365, nameof(options.LogRetentionDays));
        Range(options.LogFileSizeMiB, 1, 100, nameof(options.LogFileSizeMiB));
        Range(options.LogTotalSizeMiB, 10, 1024, nameof(options.LogTotalSizeMiB));
        Check(options.LogTotalSizeMiB >= options.LogFileSizeMiB, nameof(options.LogTotalSizeMiB));
        Check(options.LogLevel is >= LogLevel.Trace and <= LogLevel.Error, nameof(options.LogLevel));
        Check(Uri.TryCreate(options.ServerUrl, UriKind.Absolute, out var url)
            && url.Scheme == Uri.UriSchemeHttp && url.Port > 0
            && (url.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
                || (IPAddress.TryParse(url.Host.Trim('[', ']'), out var address) && IPAddress.IsLoopback(address)))
            && url.UserInfo.Length == 0 && url.Query.Length == 0 && url.Fragment.Length == 0, nameof(options.ServerUrl));
        Check(!string.IsNullOrWhiteSpace(options.UiProfileDirectory)
            && !Path.IsPathRooted(options.UiProfileDirectory)
            && options.UiProfileDirectory.IndexOfAny([':', '*', '?', '"', '<', '>', '|', '\0']) < 0,
            nameof(options.UiProfileDirectory));
        try
        {
            var profile = Path.GetFullPath(options.UiProfileDirectory!, paths.RootDirectory);
            var prefix = Path.EndsInDirectorySeparator(paths.RootDirectory)
                ? paths.RootDirectory : paths.RootDirectory + Path.DirectorySeparatorChar;
            Check(profile.StartsWith(prefix, StringComparison.OrdinalIgnoreCase), nameof(options.UiProfileDirectory));
        }
        catch (ArgumentException)
        {
            throw Invalid(nameof(options.UiProfileDirectory));
        }
        Check(options.EnabledUiDrivers.Length > 0
            && options.EnabledUiDrivers.All(name => Drivers.Contains(name, StringComparer.Ordinal))
            && options.EnabledUiDrivers.Distinct(StringComparer.Ordinal).Count() == options.EnabledUiDrivers.Length,
            nameof(options.EnabledUiDrivers));
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var target in options.AllowedLaunchTargets)
        {
            Check(Identifier(target.Id) && ids.Add(target.Id) && Executable(target.ExecutablePath)
                && target.Arguments.All(argument => !argument.Contains('\0')), nameof(options.AllowedLaunchTargets));
        }
        Check(options.TerminalLaunchTargetId is null || Identifier(options.TerminalLaunchTargetId), nameof(options.TerminalLaunchTargetId));
        Check(options.PowerShellLaunchTargetId is null || Identifier(options.PowerShellLaunchTargetId), nameof(options.PowerShellLaunchTargetId));
        Check(options.ConsoleHostLaunchTargetId is null || Identifier(options.ConsoleHostLaunchTargetId), nameof(options.ConsoleHostLaunchTargetId));
        foreach (var pair in options.BrowserLaunchTargets)
        {
            Check(Browsers.Contains(pair.Key, StringComparer.Ordinal) && ids.Contains(pair.Value), nameof(options.BrowserLaunchTargets));
        }
        foreach (var pair in options.BrowserDriverPaths)
        {
            Check(Browsers.Contains(pair.Key, StringComparer.Ordinal) && Executable(pair.Value), nameof(options.BrowserDriverPaths));
        }
    }

    private static void Object(JsonElement element, Type type, string field)
    {
        Check(element.ValueKind == JsonValueKind.Object, field);
        var members = type.GetProperties().ToDictionary(property => property.Name, StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in element.EnumerateObject())
        {
            Check(seen.Add(property.Name) && members.ContainsKey(property.Name), field);
            var member = members[property.Name];
            if (type == typeof(KugutsuOptions)
                && member.Name is nameof(KugutsuOptions.TerminalLaunchTargetId) or nameof(KugutsuOptions.PowerShellLaunchTargetId)
                    or nameof(KugutsuOptions.ConsoleHostLaunchTargetId)
                && property.Value.ValueKind == JsonValueKind.Null)
            {
                continue;
            }
            Value(property.Value, member.PropertyType, field + "." + member.Name);
        }
    }

    private static void Value(JsonElement element, Type type, string field)
    {
        if (type == typeof(string) || type == typeof(LogLevel))
        {
            Check(element.ValueKind == JsonValueKind.String, field);
            if (type == typeof(LogLevel))
            {
                Check(Enum.GetNames<LogLevel>().Take(5).Contains(element.GetString(), StringComparer.OrdinalIgnoreCase), field);
            }
        }
        else if (type == typeof(int))
        {
            Check(element.ValueKind == JsonValueKind.Number && element.TryGetInt32(out _), field);
        }
        else if (type.IsArray)
        {
            Check(element.ValueKind == JsonValueKind.Array, field);
            foreach (var item in element.EnumerateArray()) { Value(item, type.GetElementType()!, field); }
        }
        else if (type == typeof(Dictionary<string, string>))
        {
            Check(element.ValueKind == JsonValueKind.Object, field);
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in element.EnumerateObject())
            {
                Check(seen.Add(property.Name) && Browsers.Contains(property.Name, StringComparer.Ordinal), field);
                Value(property.Value, typeof(string), field);
            }
        }
        else { Object(element, type, field); }
    }

    private static bool Identifier(string value) => value.Length is >= 1 and <= 64 && char.IsAsciiLetter(value[0])
        && value.All(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '-' or '.');

    private static bool Executable(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || !Path.IsPathFullyQualified(value)
            || value.IndexOfAny(Path.GetInvalidPathChars()) >= 0
            || value.IndexOfAny(['*', '?']) >= 0
            || value[Path.GetPathRoot(value)!.Length..].Contains(':')
            || !value.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) { return false; }
        try { return Path.GetFullPath(value).EndsWith(".exe", StringComparison.OrdinalIgnoreCase); }
        catch (ArgumentException) { return false; }
    }

    private static void Range(int value, int minimum, int maximum, string field) => Check(value >= minimum && value <= maximum, field);
    private static void Check(bool valid, string field)
    {
        if (!valid) { throw Invalid(field); }
    }
    private static InvalidDataException Invalid(string field) => new($"Invalid configuration field: {field}.");
}
