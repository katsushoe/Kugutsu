using System.Text.Json;
using Kugutsu.Contracts;

namespace Kugutsu.App;

internal sealed record UiFindQuery(string? AutomationId = null, string? Name = null,
    string? ControlType = null, string? SemanticRole = null, string? ProfileId = null, int MaxCandidates = 1000);
internal sealed record UiSnapshotQuery(string? ProfileId = null, bool IncludeImage = false,
    int MaxDepth = 32, int MaxCandidates = 1000);
internal sealed record UiOperationTarget(WindowReference Window, string Driver, string ElementId,
    string? SnapshotId, string? ProfileId, string? ProfileVersion);

/// <summary>登録されたドライバへ明示委譲する契約。参照検証は委譲直前と実入力直前に行う。</summary>
internal interface IUiDriver
{
    UiDriverInfo Info { get; }
    ValueTask<UiSnapshot> FindAsync(WindowReference window, UiFindQuery query, CancellationToken token);
    ValueTask<UiSnapshot> SnapshotAsync(WindowReference window, UiSnapshotQuery query, CancellationToken token);
    // 未対応はUI_OPERATION_UNSUPPORTED、未準備はUI_DRIVER_UNAVAILABLEとして例外契約へ変換する。
    // 他ドライバへの切替、曖昧な候補選択、未知属性や内部値の推測は行わない。
    ValueTask<JsonElement?> ExecuteAsync(UiOperationTarget target, string operation,
        JsonElement arguments, CancellationToken token);
}
