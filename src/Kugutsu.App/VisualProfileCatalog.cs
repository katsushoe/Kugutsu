using System.Security.Cryptography;
using System.Text.Json;
using Kugutsu.Contracts;
using Kugutsu.Hosting;

namespace Kugutsu.App;

internal sealed record VisualFocusIndicator(string Color, int Tolerance, int Inset, double Coverage);
internal sealed record VisualProfileRule(string RuleId, string Shape, string Label, string Relation,
    string SemanticRole, bool? Sensitive, bool AllowTextInput, VisualFocusIndicator? FocusIndicator);
internal sealed record VisualProfileScreen(string ScreenId, string? TitleContains,
    IReadOnlyList<string> RequiredLabels, IReadOnlyList<VisualProfileRule> Rules);
internal sealed record VisualProfile(string ProfileId, string ProfileVersion, string Driver, string ProcessName,
    string? TitleContains, IReadOnlyList<VisualProfileScreen> Screens, string ContentHash);

/// <summary>Profiles are bounded literal rules, with no script, regex, executable path or external resource.</summary>
internal sealed class VisualProfileCatalog(InstallationPaths paths, KugutsuOptions settings)
{
    private static readonly ArgumentSchema FocusSchema = ArgumentSchema.Object([
        new("kind", ArgumentSchema.String(12, 12, ["border_color"])), new("color", ArgumentSchema.String(7, 7)),
        new("tolerance", ArgumentSchema.Integer(0, 64), false), new("inset", ArgumentSchema.Integer(0, 8), false),
        new("coverage", ArgumentSchema.Number(0.5, 1), false)]);
    private static readonly ArgumentSchema RuleSchema = ArgumentSchema.Object([
        new("ruleId", ArgumentSchema.String(64, 1)),
        new("shape", ArgumentSchema.String(16, 15, ["input_candidate", "button_candidate"])),
        new("label", ArgumentSchema.String(512, 1)), new("relation", ArgumentSchema.String(6, 4, ["left", "right", "above", "inside"])),
        new("semanticRole", ArgumentSchema.String(64, 1)), new("sensitive", ArgumentSchema.Boolean, false),
        new("allowTextInput", ArgumentSchema.Boolean, false), new("focusIndicator", FocusSchema, false)]);
    private static readonly ArgumentSchema ScreenSchema = ArgumentSchema.Object([
        new("screenId", ArgumentSchema.String(64, 1)), new("titleContains", ArgumentSchema.String(1024), false),
        new("requiredLabels", ArgumentSchema.Array(ArgumentSchema.String(512, 1), 32, 1)),
        new("rules", ArgumentSchema.Array(RuleSchema, 256, 1))]);
    private static readonly ArgumentSchema ProfileSchema = ArgumentSchema.Object([
        new("schemaVersion", ArgumentSchema.Integer(1, 1)), new("profileId", ArgumentSchema.String(64, 1)),
        new("profileVersion", ArgumentSchema.String(64, 1)),
        new("driver", ArgumentSchema.String(14, 12, ["imgui_visual", "generic_visual"])),
        new("target", ArgumentSchema.Object([new("processName", ArgumentSchema.String(256, 1)),
            new("titleContains", ArgumentSchema.String(1024), false)])),
        new("screens", ArgumentSchema.Array(ScreenSchema, 32, 1))]);
    private readonly object _gate = new();
    private IReadOnlyList<VisualProfile>? _metadata;

    internal IReadOnlyList<VisualProfile> Load(CancellationToken token)
    {
        try
        {
            var directory = Path.GetFullPath(settings.UiProfileDirectory ?? throw Invalid(), paths.RootDirectory);
            var prefix = Path.TrimEndingDirectorySeparator(paths.RootDirectory) + Path.DirectorySeparatorChar;
            if (!directory.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || directory.StartsWith("\\\\", StringComparison.Ordinal)) { throw Invalid(); }
            VerifyAncestors(directory);
            if (!Directory.Exists(directory)) { lock (_gate) { _metadata = Array.Empty<VisualProfile>(); } return Array.Empty<VisualProfile>(); }
            var profiles = new List<VisualProfile>();
            var ids = new HashSet<string>(StringComparer.Ordinal);
            long total = 0;
            foreach (var file in Directory.EnumerateFiles(directory, "*.json", SearchOption.TopDirectoryOnly).Take(101).Order(StringComparer.Ordinal))
            {
                token.ThrowIfCancellationRequested();
                if (profiles.Count == 100) { throw Invalid(); }
                VerifyAncestors(file);
                using var input = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read);
                if (input.Length is <= 0 or > 262144 || (total += input.Length) > 10485760) { throw Invalid(); }
                var bytes = new byte[(int)input.Length];
                input.ReadExactly(bytes);
                var json = bytes.AsMemory();
                if (bytes.AsSpan().StartsWith(new byte[] { 239, 187, 191 })) { json = json[3..]; }
                using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 16 });
                if (!ProfileSchema.Accepts(document.RootElement)) { throw Invalid(); }
                var profile = Parse(document.RootElement, Convert.ToHexString(SHA256.HashData(bytes)));
                if (!ids.Add(profile.ProfileId)) { throw Invalid(); }
                profiles.Add(profile);
            }
            token.ThrowIfCancellationRequested();
            var result = profiles.AsReadOnly();
            lock (_gate) { _metadata = result; }
            return result;
        }
        catch (WindowReferenceException) { throw; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or ArgumentException or NotSupportedException)
        { throw Invalid(); }
    }

    internal IReadOnlyList<UiProfileVersion> Metadata(string driver)
    {
        IReadOnlyList<VisualProfile>? profiles;
        lock (_gate) { profiles = _metadata; }
        profiles ??= Load(CancellationToken.None);
        return Array.AsReadOnly(profiles.Where(profile => profile.Driver == driver)
            .Select(profile => new UiProfileVersion(profile.ProfileId, profile.ProfileVersion)).ToArray());
    }

    private static VisualProfile Parse(JsonElement root, string hash)
    {
        var id = Text(root, "profileId"); var version = Text(root, "profileVersion");
        if (!Identifier(id) || !Identifier(version)) { throw Invalid(); }
        var target = root.GetProperty("target");
        var process = Text(target, "processName"); var title = OptionalText(target, "titleContains");
        if (!Literal(process) || title is not null && !Literal(title)) { throw Invalid(); }
        var screens = new List<VisualProfileScreen>();
        var screenIds = new HashSet<string>(StringComparer.Ordinal);
        var ruleIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var screen in root.GetProperty("screens").EnumerateArray())
        {
            var screenId = Text(screen, "screenId"); var screenTitle = OptionalText(screen, "titleContains");
            if (!Identifier(screenId) || !screenIds.Add(screenId) || screenTitle is not null && !Literal(screenTitle)) { throw Invalid(); }
            var labels = screen.GetProperty("requiredLabels").EnumerateArray().Select(item => item.GetString()!).ToArray();
            if (labels.Any(label => !Literal(label)) || labels.Distinct(StringComparer.Ordinal).Count() != labels.Length) { throw Invalid(); }
            var rules = new List<VisualProfileRule>();
            foreach (var rule in screen.GetProperty("rules").EnumerateArray())
            {
                var ruleId = Text(rule, "ruleId"); var semantic = Text(rule, "semanticRole"); var label = Text(rule, "label");
                var shape = Text(rule, "shape"); var relation = Text(rule, "relation");
                var allowInput = rule.TryGetProperty("allowTextInput", out var allow) && allow.GetBoolean();
                if (!Identifier(ruleId) || !ruleIds.Add(ruleId) || !Identifier(semantic) || !Literal(label)
                    || shape == "input_candidate" && relation == "inside" || shape == "button_candidate" && relation != "inside"
                    || shape != "input_candidate" && allowInput) { throw Invalid(); }
                VisualFocusIndicator? focus = null;
                if (rule.TryGetProperty("focusIndicator", out var indicator))
                {
                    var color = Text(indicator, "color");
                    if (shape != "input_candidate" || color[0] != '#' || !color.AsSpan(1).ToArray().All(char.IsAsciiHexDigit)) { throw Invalid(); }
                    focus = new(color, indicator.TryGetProperty("tolerance", out var tolerance) ? tolerance.GetInt32() : 12,
                        indicator.TryGetProperty("inset", out var inset) ? inset.GetInt32() : 0,
                        indicator.TryGetProperty("coverage", out var coverage) ? coverage.GetDouble() : 0.7);
                }
                rules.Add(new(ruleId, shape, label, relation, semantic,
                    rule.TryGetProperty("sensitive", out var sensitive) ? sensitive.GetBoolean() : null, allowInput, focus));
            }
            screens.Add(new(screenId, screenTitle, Array.AsReadOnly(labels), rules.AsReadOnly()));
        }
        return new(id, version, Text(root, "driver"), process, title, screens.AsReadOnly(), hash);
    }

    internal static bool Identifier(string value) => value.Length is > 0 and <= 64
        && value.All(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '-' or '.');
    private static bool Literal(string value) => ControlTextSignalProvider.ValidText(value) && !value.Any(char.IsControl);
    private static string Text(JsonElement value, string name) => value.GetProperty(name).GetString()!;
    private static string? OptionalText(JsonElement value, string name) => value.TryGetProperty(name, out var text) ? text.GetString() : null;
    private static void VerifyAncestors(string path)
    {
        string? current = path;
        while (current is not null)
        {
            if ((Directory.Exists(current) || File.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) { throw Invalid(); }
            current = Path.GetDirectoryName(current);
        }
    }
    private static WindowReferenceException Invalid() => new(ErrorCode.UI_PROFILE_INVALID);
}
