using System.Collections.Frozen;
using Kugutsu.Contracts;

namespace Kugutsu.App;

internal sealed record VisualScreenMatch(VisualProfile Profile, VisualProfileScreen Screen, IReadOnlyList<UiEvidence> Evidence);
internal sealed record VisualScreenMatches(IReadOnlyList<VisualScreenMatch> Matches, bool Truncated);

/// <summary>Literal target and surrounding-label matches are evidence, without selecting one screen implicitly.</summary>
internal sealed class VisualScreenMatcher(VisualLabelAssociator labels)
{
    internal VisualScreenMatches Match(WindowInfo window, string driver, string? profileId,
        IReadOnlyList<VisualProfile> profiles, OcrResult ocr, CancellationToken token)
    {
        if (profileId is not null && !profiles.Any(profile => profile.ProfileId == profileId && profile.Driver == driver))
        { throw new WindowReferenceException(ErrorCode.UI_PROFILE_INVALID); }
        var text = labels.ReadLabels(ocr, token);
        var byText = text.Labels.GroupBy(label => label.Text, StringComparer.Ordinal)
            .ToFrozenDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);
        var matches = new List<VisualScreenMatch>();
        var truncated = text.Truncated;
        foreach (var profile in profiles)
        {
            token.ThrowIfCancellationRequested();
            if (profile.Driver != driver || profileId is not null && profile.ProfileId != profileId
                || !profile.ProcessName.Equals(window.ProcessName, StringComparison.OrdinalIgnoreCase)
                || !TitleMatches(window, profile.TitleContains)) { continue; }
            foreach (var screen in profile.Screens)
            {
                token.ThrowIfCancellationRequested();
                if (!TitleMatches(window, screen.TitleContains) || !screen.RequiredLabels.All(byText.ContainsKey)) { continue; }
                if (matches.Count == 128) { truncated = true; return new(matches.AsReadOnly(), truncated); }
                var evidence = new List<UiEvidence> {
                    new("profile", RuleId: profile.ProfileId), new("screen", RuleId: screen.ScreenId),
                    new("target_process", RuleId: profile.ProfileId) };
                if (profile.TitleContains is not null || screen.TitleContains is not null)
                { evidence.Add(new("window_title", RuleId: screen.ScreenId)); }
                foreach (var required in screen.RequiredLabels)
                {
                    var found = byText[required];
                    if (found.Length > 4) { truncated = true; }
                    foreach (var label in found.Take(4))
                    { evidence.Add(new("ocr_context", label.Text, screen.ScreenId, label.Bounds)); }
                }
                matches.Add(new(profile, screen, evidence.AsReadOnly()));
            }
        }
        if (profileId is not null && matches.Count == 0) { throw new WindowReferenceException(ErrorCode.UI_TARGET_UNVERIFIED); }
        return new(matches.AsReadOnly(), truncated);
    }

    private static bool TitleMatches(WindowInfo window, string? expected) => expected is null
        || !window.TitleTruncated && window.Title.Contains(expected, StringComparison.OrdinalIgnoreCase);
}
