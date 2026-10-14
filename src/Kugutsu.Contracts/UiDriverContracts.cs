using System.Text.Json.Serialization;

namespace Kugutsu.Contracts;

/// <summary>クライアント左上を原点とする物理ピクセル矩形。</summary>
public sealed record UiBounds
{
    [JsonConstructor]
    public UiBounds(int x, int y, int width, int height)
    {
        if (width < 0 || height < 0) { throw new ArgumentOutOfRangeException(nameof(width)); }
        X = x; Y = y; Width = width; Height = height;
    }
    [JsonPropertyName("x")] public int X { get; }
    [JsonPropertyName("y")] public int Y { get; }
    [JsonPropertyName("width")] public int Width { get; }
    [JsonPropertyName("height")] public int Height { get; }
}

/// <summary>nullは未取得を表し、falseや既定状態へ置き換えない。</summary>
public sealed record UiElementState(
    [property: JsonPropertyName("enabled")] bool? Enabled = null,
    [property: JsonPropertyName("visible")] bool? Visible = null,
    [property: JsonPropertyName("offscreen")] bool? Offscreen = null,
    [property: JsonPropertyName("focused")] bool? Focused = null,
    [property: JsonPropertyName("selected")] bool? Selected = null,
    [property: JsonPropertyName("expanded")] bool? Expanded = null,
    [property: JsonPropertyName("toggleState")] string? ToggleState = null);

public sealed record UiEvidence(
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("text")] string? Text = null,
    [property: JsonPropertyName("ruleId")] string? RuleId = null,
    [property: JsonPropertyName("bounds")] UiBounds? Bounds = null);

/// <summary>認識の信頼度種別を保持し、rule_scoreを確率へ変換しない。</summary>
public sealed class UiCandidate
{
    [JsonConstructor]
    public UiCandidate(string elementId, string? snapshotId, string driver, string? profileId,
        string? profileVersion, string? controlType, string? semanticRole, string? label,
        UiBounds? bounds, UiElementState? state, IReadOnlyList<UiEvidence> evidence,
        double? confidence, string? confidenceKind, IReadOnlyList<string> supportedOperations,
        string? parentElementId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(elementId);
        ArgumentException.ThrowIfNullOrWhiteSpace(driver);
        if (confidence is { } score && (!double.IsFinite(score) || score < 0 || score > 1))
        { throw new ArgumentOutOfRangeException(nameof(confidence)); }
        if ((confidence is null) != (confidenceKind is null)
            || confidenceKind is not null && string.IsNullOrWhiteSpace(confidenceKind))
        { throw new ArgumentException("Confidence requires its source kind.", nameof(confidenceKind)); }
        ArgumentNullException.ThrowIfNull(evidence);
        ArgumentNullException.ThrowIfNull(supportedOperations);
        if (evidence.Any(item => item is null || string.IsNullOrWhiteSpace(item.Kind))
            || supportedOperations.Any(string.IsNullOrWhiteSpace)
            || supportedOperations.Distinct(StringComparer.Ordinal).Count() != supportedOperations.Count)
        { throw new ArgumentException("Invalid candidate evidence or operations."); }
        ElementId = elementId; SnapshotId = snapshotId; Driver = driver;
        ProfileId = profileId; ProfileVersion = profileVersion; ControlType = controlType;
        SemanticRole = semanticRole; Label = label; Bounds = bounds; State = state;
        Evidence = Array.AsReadOnly(evidence.ToArray()); Confidence = confidence;
        ConfidenceKind = confidenceKind; SupportedOperations = Array.AsReadOnly(supportedOperations.ToArray());
        ParentElementId = parentElementId;
    }
    [JsonPropertyName("elementId")] public string ElementId { get; }
    [JsonPropertyName("snapshotId")] public string? SnapshotId { get; }
    [JsonPropertyName("driver")] public string Driver { get; }
    [JsonPropertyName("profileId")] public string? ProfileId { get; }
    [JsonPropertyName("profileVersion")] public string? ProfileVersion { get; }
    [JsonPropertyName("controlType")] public string? ControlType { get; }
    [JsonPropertyName("semanticRole")] public string? SemanticRole { get; }
    [JsonPropertyName("label")] public string? Label { get; }
    [JsonPropertyName("bounds")] public UiBounds? Bounds { get; }
    [JsonPropertyName("state")] public UiElementState? State { get; }
    [JsonPropertyName("evidence")] public IReadOnlyList<UiEvidence> Evidence { get; }
    [JsonPropertyName("confidence")] public double? Confidence { get; }
    [JsonPropertyName("confidenceKind")] public string? ConfidenceKind { get; }
    [JsonPropertyName("supportedOperations")] public IReadOnlyList<string> SupportedOperations { get; }
    [JsonPropertyName("parentElementId")] public string? ParentElementId { get; }
}

public sealed record UiProfileVersion(
    [property: JsonPropertyName("profileId")] string ProfileId,
    [property: JsonPropertyName("profileVersion")] string ProfileVersion);

public sealed class UiDriverInfo
{
    public UiDriverInfo(string driver, bool available, ErrorCode? unavailableReason,
        IEnumerable<string> discoveryCapabilities, IEnumerable<string> supportedOperations,
        IEnumerable<UiProfileVersion>? profiles = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(driver);
        if (available == (unavailableReason is not null))
        { throw new ArgumentException("Availability and reason disagree."); }
        ArgumentNullException.ThrowIfNull(discoveryCapabilities);
        ArgumentNullException.ThrowIfNull(supportedOperations);
        Driver = driver; Available = available; UnavailableReason = unavailableReason;
        DiscoveryCapabilities = Array.AsReadOnly(discoveryCapabilities.ToArray());
        SupportedOperations = Array.AsReadOnly(supportedOperations.ToArray());
        Profiles = Array.AsReadOnly((profiles ?? []).ToArray());
        if (DiscoveryCapabilities.Any(string.IsNullOrWhiteSpace) || SupportedOperations.Any(string.IsNullOrWhiteSpace))
        { throw new ArgumentException("Invalid driver capabilities."); }
        if (Profiles.Any(item => item is null || string.IsNullOrWhiteSpace(item.ProfileId)
            || string.IsNullOrWhiteSpace(item.ProfileVersion)))
        { throw new ArgumentException("Invalid profile versions."); }
    }
    [JsonPropertyName("driver")] public string Driver { get; }
    [JsonPropertyName("available")] public bool Available { get; }
    [JsonPropertyName("unavailableReason")] public ErrorCode? UnavailableReason { get; }
    [JsonPropertyName("discoveryCapabilities")] public IReadOnlyList<string> DiscoveryCapabilities { get; }
    [JsonPropertyName("supportedOperations")] public IReadOnlyList<string> SupportedOperations { get; }
    [JsonPropertyName("profiles")] public IReadOnlyList<UiProfileVersion> Profiles { get; }
}

public sealed class UiSnapshotImage
{
    public UiSnapshotImage(ReadOnlyMemory<byte> data)
    {
        if (data.Length is < 1 or > 8 * 1024 * 1024)
        { throw new ArgumentOutOfRangeException(nameof(data)); }
        Data = data.ToArray();
    }
    [JsonPropertyName("mimeType")] public string MimeType => "image/png";
    [JsonPropertyName("data")] public ReadOnlyMemory<byte> Data { get; }
}

public sealed class UiSnapshot
{
    public UiSnapshot(string windowId, string driver, string snapshotId, IEnumerable<UiCandidate> candidates,
        bool truncated, UiSnapshotImage? image = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(windowId);
        ArgumentException.ThrowIfNullOrWhiteSpace(driver);
        ArgumentException.ThrowIfNullOrWhiteSpace(snapshotId);
        ArgumentNullException.ThrowIfNull(candidates);
        var items = candidates.ToArray();
        if (items.Any(item => item is null || item.Driver != driver || item.SnapshotId != snapshotId))
        { throw new ArgumentException("Candidates must belong to this driver and snapshot."); }
        var ids = items.Select(item => item.ElementId).ToHashSet(StringComparer.Ordinal);
        if (ids.Count != items.Length || items.Any(item => item.ParentElementId is not null
            && (!ids.Contains(item.ParentElementId) || item.ParentElementId == item.ElementId)))
        { throw new ArgumentException("Invalid snapshot hierarchy."); }
        WindowId = windowId; Driver = driver; SnapshotId = snapshotId;
        Candidates = Array.AsReadOnly(items); Truncated = truncated; Image = image;
    }
    [JsonPropertyName("windowId")] public string WindowId { get; }
    [JsonPropertyName("driver")] public string Driver { get; }
    [JsonPropertyName("snapshotId")] public string SnapshotId { get; }
    [JsonPropertyName("candidates")] public IReadOnlyList<UiCandidate> Candidates { get; }
    [JsonPropertyName("truncated")] public bool Truncated { get; }
    [JsonPropertyName("image")] public UiSnapshotImage? Image { get; }
}
