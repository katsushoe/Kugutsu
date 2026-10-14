using Kugutsu.Contracts;
using Kugutsu.Hosting;

namespace Kugutsu.App;

internal sealed class VisualAnalysisPipeline(WindowReferenceRegistry windows, WindowDiscovery discovery,
    WindowStateOperations state, ScreenCapture capture, ILocalOcr ocr, VisualShapeDetector shapes,
    VisualLabelAssociator labels, VisualProfileCatalog profiles, VisualScreenMatcher screens,
    VisualMeaningInference meanings, VisualFrameRegistry frames, Func<UiElementRegistry> elements, KugutsuOptions settings)
{
    internal bool IsAvailable => ocr.IsAvailable;
    internal IReadOnlyList<UiProfileVersion> Profiles(string driver) => profiles.Metadata(driver);
    internal void Discard(string snapshotId) { elements().InvalidateSnapshot(snapshotId); frames.Remove(snapshotId); }

    internal async ValueTask<UiSnapshot> CaptureAsync(WindowReference window, string driver, UiFindQuery query,
        bool includeImage, CancellationToken token)
    {
        if (!IsAvailable) { throw new WindowReferenceException(ErrorCode.UI_DRIVER_UNAVAILABLE); }
        if (query.AutomationId is not null || query.MaxCandidates is < 1 or > 1000
            || query.ControlType is not (null or "Edit" or "Button")) { throw new WindowReferenceException(ErrorCode.INVALID_ARGUMENT); }
        if (windows.Resolve(window.WindowId) != window) { throw new WindowReferenceException(ErrorCode.UI_ELEMENT_STALE); }
        var catalog = profiles.Load(token);
        state.VerifyInputTarget(window.WindowId, token);
        var registry = elements();
        var stamp = registry.GetVisualStamp(window.WindowId, token);
        var context = discovery.Get(window.WindowId, token);
        var captured = capture.Capture(window.WindowId, token);
        var width = captured.GetProperty("width").GetInt32();
        var height = captured.GetProperty("height").GetInt32();
        if (width > 4096 || height > 4096 || (long)width * height > 8388608)
        { throw new WindowReferenceException(ErrorCode.CAPTURE_TOO_LARGE); }
        var png = Convert.FromBase64String(captured.GetProperty("image").GetProperty("data").GetString()!);
        var raster = await Task.Run(() => VisualRaster.Decode(png, token), token);
        var text = await ocr.RecognizeAsync(png, token);
        var shapeCandidates = await Task.Run(() =>
        {
            var inputs = query.ControlType == "Button" ? new VisualShapes([], false) : shapes.FindInputs(raster, settings.MaxUiCandidates, token);
            var buttons = query.ControlType == "Edit" ? new VisualShapes([], false) : shapes.FindButtons(raster, settings.MaxUiCandidates, token);
            var combined = inputs.Shapes.Concat(buttons.Shapes).Take(1000).ToArray();
            return new VisualShapes(Array.AsReadOnly(combined), inputs.Truncated || buttons.Truncated || inputs.Shapes.Count + buttons.Shapes.Count > combined.Length);
        }, token);
        var associated = labels.Associate(shapeCandidates, text, token);
        var screenMatches = screens.Match(context, driver, query.ProfileId, catalog, text, token);
        var classified = meanings.Infer(associated, screenMatches, 1000, token);
        var selected = classified.Candidates.Where(candidate => (query.ProfileId is null || candidate.Profile?.ProfileId == query.ProfileId)
            && (query.SemanticRole is null || candidate.SemanticRole == query.SemanticRole)
            && (query.Name is null || candidate.Label == query.Name || candidate.Rule is null
                && candidate.Source.Labels.Any(label => label.Text == query.Name))).ToArray();
        var current = registry.GetVisualStamp(window.WindowId, token);
        var currentContext = discovery.Get(window.WindowId, token);
        state.VerifyInputTarget(window.WindowId, token);
        if (current.Geometry != stamp.Geometry || current.Dpi != stamp.Dpi || currentContext.Title != context.Title
            || windows.Resolve(window.WindowId) != window) { throw new WindowReferenceException(ErrorCode.UI_ELEMENT_STALE); }
        var snapshotId = "s_" + Guid.NewGuid().ToString("N");
        frames.Add(snapshotId, new(window, context, stamp, png, raster), token);
        try
        {
            var candidates = new List<UiCandidate>();
            foreach (var meaning in selected.Take(query.MaxCandidates))
            {
                token.ThrowIfCancellationRequested();
                var reference = registry.Register(window, driver, new VisualElementHandle(snapshotId, meaning), snapshotId,
                    meaning.Profile?.ProfileId, meaning.Profile?.ProfileVersion, stamp, token);
                candidates.Add(new(reference.Target.ElementId, snapshotId, driver, meaning.Profile?.ProfileId,
                    meaning.Profile?.ProfileVersion, meaning.Source.Shape.Kind == "input_candidate" ? "Edit" : "Button",
                    meaning.SemanticRole, meaning.Label, meaning.Source.Shape.Bounds, new UiElementState(),
                    meaning.Evidence, meaning.RuleScore, "rule_score", SupportedOperations(meaning)));
            }
            return new(window.WindowId, driver, snapshotId, candidates,
                classified.Truncated || selected.Length > candidates.Count, includeImage ? new UiSnapshotImage(png) : null);
        }
        catch { registry.InvalidateSnapshot(snapshotId); frames.Remove(snapshotId); throw; }
    }

    private static IReadOnlyList<string> SupportedOperations(VisualMeaning meaning)
    {
        if (meaning.Source.Shape.Kind == "button_candidate" && meaning.Rule is not null) { return ["control.invoke"]; }
        if (meaning.Source.Shape.Kind != "input_candidate") { return []; }
        var operations = new List<string>();
        if (meaning.Rule is { AllowTextInput: true, FocusIndicator: not null }) { operations.AddRange(["control.set_value", "control.send_keys"]); }
        if (meaning.Rule?.Sensitive == false) { operations.Add("control.get_value"); }
        return operations.AsReadOnly();
    }
}
