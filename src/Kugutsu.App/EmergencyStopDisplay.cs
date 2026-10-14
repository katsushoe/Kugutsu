namespace Kugutsu.App;

internal static class EmergencyStopDisplay
{
    internal static string Describe(EmergencyStopStatus status) => status.ShuttingDown
        ? "終了中: 実行停止と資源解放を待っています"
        : !status.Active
        ? "操作受付: 有効"
        : status.ForceUnlockPending
            ? "ロック強制解除: 停止確認中（操作受付を停止しています）"
        : status.StopConfirmationPending
            ? "緊急停止: 実行停止確認中（操作受付を停止しています）"
            : "緊急停止: 停止確認済み（再開にはトレイから操作受付を再開してください）";
}
