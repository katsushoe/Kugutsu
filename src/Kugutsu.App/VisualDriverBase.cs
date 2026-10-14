using System.Text.Json;
using Kugutsu.Contracts;

namespace Kugutsu.App;

internal abstract class VisualDriverBase(string driver, VisualAnalysisPipeline pipeline, VisualControlActions actions) : IUiDriver
{
    public UiDriverInfo Info
    {
        get
        {
            try
            {
                var available = pipeline.IsAvailable;
                return new(driver, available, available ? null : ErrorCode.UI_DRIVER_UNAVAILABLE,
                    ["shape_candidates", "local_ocr", "literal_profiles", "semantic_rules", "snapshot", "find"], ["control.invoke", "control.set_value", "control.send_keys", "control.get_value"], pipeline.Profiles(driver));
            }
            catch (WindowReferenceException error) { return new(driver, false, error.Code, [], []); }
        }
    }
    public async ValueTask<UiSnapshot> FindAsync(WindowReference window, UiFindQuery query, CancellationToken token)
    {
        var snapshot = await pipeline.CaptureAsync(window, driver, query, false, token);
        if (snapshot.Candidates.Count != 0) { return snapshot; }
        pipeline.Discard(snapshot.SnapshotId);
        throw new WindowReferenceException(ErrorCode.UI_ELEMENT_NOT_FOUND);
    }
    public ValueTask<UiSnapshot> SnapshotAsync(WindowReference window, UiSnapshotQuery query, CancellationToken token) =>
        pipeline.CaptureAsync(window, driver, new(ProfileId: query.ProfileId, MaxCandidates: query.MaxCandidates), query.IncludeImage, token);
    public ValueTask<JsonElement?> ExecuteAsync(UiOperationTarget target, string operation, JsonElement arguments, CancellationToken token) =>
        actions.ExecuteAsync(target, operation, arguments, token);
}
