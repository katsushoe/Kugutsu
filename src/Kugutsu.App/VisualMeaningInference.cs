using Kugutsu.Contracts;

namespace Kugutsu.App;

internal sealed record VisualMeaning(LabeledVisualShape Source, string? SemanticRole, string? Label,
    VisualProfile? Profile, VisualProfileRule? Rule, string? ScreenId, double RuleScore, IReadOnlyList<UiEvidence> Evidence);
internal sealed record VisualMeanings(IReadOnlyList<VisualMeaning> Candidates, bool Truncated);

/// <summary>Literal profile rules assign purposes, separately from the inferred geometric control kind.</summary>
internal sealed class VisualMeaningInference
{
    internal VisualMeanings Infer(VisualAssociations associations, VisualScreenMatches screens, int maximum, CancellationToken token)
    {
        if (maximum is < 1 or > 1000) { throw new WindowReferenceException(ErrorCode.INVALID_ARGUMENT); }
        var result = new List<VisualMeaning>();
        var truncated = associations.Truncated || screens.Truncated;
        var comparisons = 0;
        foreach (var source in associations.Shapes)
        {
            token.ThrowIfCancellationRequested();
            var matched = false;
            foreach (var screen in screens.Matches)
            {
                foreach (var rule in screen.Screen.Rules)
                {
                    token.ThrowIfCancellationRequested();
                    if (++comparisons > 262144) { truncated = true; goto Finished; }
                    if (rule.Shape != source.Shape.Kind) { continue; }
                    foreach (var label in source.Labels.Where(label => label.Text == rule.Label && label.Relation == rule.Relation))
                    {
                        if (result.Count == maximum) { truncated = true; goto Finished; }
                        var evidence = new List<UiEvidence>(source.Shape.Evidence) {
                            new("ocr_label", label.Text, rule.RuleId, label.Bounds),
                            new("profile_rule", RuleId: rule.RuleId, Bounds: source.Shape.Bounds),
                            new("semantic_role", RuleId: rule.SemanticRole) };
                        if (screen.Evidence.Count > 24) { truncated = true; }
                        evidence.AddRange(screen.Evidence.Take(24));
                        result.Add(new(source, rule.SemanticRole, label.Text, screen.Profile, rule,
                            screen.Screen.ScreenId, Math.Min(source.Shape.Score, label.RuleScore), evidence.AsReadOnly()));
                        matched = true;
                    }
                }
            }
            if (matched) { continue; }
            if (result.Count == maximum) { truncated = true; break; }
            var unknownEvidence = new List<UiEvidence>(source.Shape.Evidence);
            unknownEvidence.AddRange(source.Labels.Select(label => new UiEvidence("ocr_label", label.Text, Bounds: label.Bounds)));
            result.Add(new(source, null, source.Labels.Count == 1 ? source.Labels[0].Text : null, null, null, null,
                source.Shape.Score, unknownEvidence.AsReadOnly()));
        }
    Finished:
        token.ThrowIfCancellationRequested();
        return new(result.AsReadOnly(), truncated);
    }
}
