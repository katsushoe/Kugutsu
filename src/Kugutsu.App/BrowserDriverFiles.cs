namespace Kugutsu.App;

/// <summary>設定したWebDriverだけを通常の起動対象と同じファイル検証へ通す。</summary>
internal sealed class BrowserDriverFiles(LaunchTargetRegistry targets, ApplicationOperations launcher)
{
    internal ValueTask<LaunchTargetLease> ResolveAsync(BrowserKind kind, CancellationToken token) =>
        targets.ResolveAsync(BrowserSessionRegistry.Name(kind), token);
    internal bool IsRegistered(BrowserKind kind) => targets.IsRegistered(BrowserSessionRegistry.Name(kind));
    internal ValueTask<ApplicationReference> LaunchAsync(BrowserKind kind, IReadOnlyList<string> args, CancellationToken token) =>
        launcher.LaunchAsync(BrowserSessionRegistry.Name(kind), token, _ => args, true);
}
