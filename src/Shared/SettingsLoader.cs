using Microsoft.Extensions.Configuration;
using System.Text.Json;

namespace Kugutsu.Hosting;

/// <summary>Reads settings once at startup without environment variable overlays.</summary>
internal static class SettingsLoader
{
    /// <summary>Loads settings and applies the optional endpoint override before value validation.</summary>
    public static KugutsuOptions Load(InstallationPaths paths, string? serverUrlOverride = null)
    {
        ArgumentNullException.ThrowIfNull(paths);
        var bytes = File.ReadAllBytes(paths.SettingsFile);
        var jsonBytes = bytes.AsMemory();
        if (bytes.AsSpan().StartsWith(new byte[] { 0xEF, 0xBB, 0xBF }))
        {
            jsonBytes = jsonBytes[3..];
        }
        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(jsonBytes, new System.Text.Json.JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = System.Text.Json.JsonCommentHandling.Skip,
            });
            SettingsValidator.ValidateDocument(document.RootElement);
        }
        catch (System.Text.Json.JsonException)
        {
            throw new InvalidDataException("Invalid configuration JSON.");
        }
        using var stream = new MemoryStream(bytes, writable: false);
        var configuration = new ConfigurationBuilder()
            .AddJsonStream(stream)
            .Build();
        using var lifetime = configuration as IDisposable;
        var options = new KugutsuOptions();
        var section = configuration.GetSection("Kugutsu");
        if (section.GetChildren().Any(child => string.Equals(
            child.Key, nameof(KugutsuOptions.EnabledUiDrivers), StringComparison.OrdinalIgnoreCase)))
        {
            // Binding appends arrays; an explicit setting must replace the default list.
            options.EnabledUiDrivers = [];
        }

        section.Bind(options);
        ApplyDetectedLaunchTargets(options, paths);
        if (serverUrlOverride is not null) { options.ServerUrl = serverUrlOverride; }
        SettingsValidator.Validate(options, paths);
        return options;
    }

    private static void ApplyDetectedLaunchTargets(KugutsuOptions options, InstallationPaths paths)
    {
        if (!File.Exists(paths.DetectedLaunchTargetsFile)) { return; }
        using var document = System.Text.Json.JsonDocument.Parse(File.ReadAllBytes(paths.DetectedLaunchTargetsFile));
        var root = document.RootElement;
        if (root.ValueKind != System.Text.Json.JsonValueKind.Object) { throw new InvalidDataException("Invalid detected launch targets."); }
        var targets = root.GetProperty("AllowedLaunchTargets").Deserialize<LaunchTargetOptions[]>()
            ?? throw new InvalidDataException("Invalid detected launch targets.");
        var ids = new HashSet<string>(options.AllowedLaunchTargets.Select(target => target.Id), StringComparer.Ordinal);
        var addedIds = new HashSet<string>(StringComparer.Ordinal);
        options.AllowedLaunchTargets = options.AllowedLaunchTargets.Concat(targets.Where(target =>
            ids.Add(target.Id) && addedIds.Add(target.Id))).ToArray();
        if (root.TryGetProperty("BrowserLaunchTargets", out var browsers))
        {
            foreach (var browser in browsers.EnumerateObject())
            {
                if (!options.BrowserLaunchTargets.ContainsKey(browser.Name)
                    && addedIds.Contains(browser.Value.GetString()!))
                { options.BrowserLaunchTargets[browser.Name] = browser.Value.GetString()!; }
            }
        }
        if (root.TryGetProperty("BrowserDriverPaths", out var drivers))
        {
            foreach (var driver in drivers.EnumerateObject())
            {
                if (!options.BrowserDriverPaths.ContainsKey(driver.Name) && driver.Value.ValueKind == System.Text.Json.JsonValueKind.String)
                { options.BrowserDriverPaths[driver.Name] = driver.Value.GetString()!; }
            }
        }
        SetDetectedTarget(root, nameof(options.TerminalLaunchTargetId), options.TerminalLaunchTargetId,
            value => options.TerminalLaunchTargetId = value, ids, addedIds);
        SetDetectedTarget(root, nameof(options.PowerShellLaunchTargetId), options.PowerShellLaunchTargetId,
            value => options.PowerShellLaunchTargetId = value, ids, addedIds);
        SetDetectedTarget(root, nameof(options.ConsoleHostLaunchTargetId), options.ConsoleHostLaunchTargetId,
            value => options.ConsoleHostLaunchTargetId = value, ids, addedIds);
    }

    private static void SetDetectedTarget(System.Text.Json.JsonElement root, string property, string? current,
        Action<string?> set, HashSet<string> existingIds, HashSet<string> addedIds)
    {
        if (current is not null && existingIds.Contains(current)) { return; }
        if (root.TryGetProperty(property, out var detected) && detected.ValueKind == System.Text.Json.JsonValueKind.String
            && detected.GetString() is { } id && (existingIds.Contains(id) || addedIds.Contains(id))) { set(id); }
    }
}
