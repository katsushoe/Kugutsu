using System.Globalization;

namespace Kugutsu.App;

internal static class LockStatusDisplay
{
    internal static string Describe(McpLockDisplayStatus status)
    {
        if (!status.Locked) { return "ロック: 未占有"; }
        var state = status.StopConfirmationPending
            ? "リース失効・停止確認中（復旧が必要）" : "占有中";
        var expiry = status.ExpiresAt?.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
        return $"ロック: {state} | 有効期限（UTC）: {expiry}";
    }

    internal static string Tooltip(McpLockDisplayStatus status)
    {
        if (!status.Locked) { return "Kugutsu - 未占有"; }
        if (status.StopConfirmationPending) { return "Kugutsu - リース失効・停止確認中（復旧が必要）"; }
        var expiry = status.ExpiresAt?.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
        return $"Kugutsu - 占有中 | 期限（UTC）: {expiry}";
    }
}
