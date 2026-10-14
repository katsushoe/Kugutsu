using System.Diagnostics;
using System.Text.Json;
using Kugutsu.Contracts;
using Kugutsu.Hosting;

namespace Kugutsu.App;

internal sealed class WindowOpenedSignalProvider(WindowReferenceRegistry windows, WindowDiscovery discovery,
    LaunchTargetRegistry targets) : ISignalConditionProvider
{
    public string Kind => "window.opened";
    public ArgumentSchema Schema { get; } = ArgumentSchema.Object([
        new("kind", ArgumentSchema.String(13, 13, ["window.opened"])),
        new("processName", ArgumentSchema.String(256, 1), false),
        new("launchTargetId", ArgumentSchema.String(64, 1), false),
        new("titleContains", ArgumentSchema.String(1024), false)]);

    public async ValueTask ValidateAsync(JsonElement condition, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var process = Text(condition, "processName");
        var target = Text(condition, "launchTargetId");
        if (process is null && target is null || !WindowDiscovery.ValidFilters(Text(condition, "titleContains"), process)
            || target?.Contains('\0') == true)
        { throw new WindowReferenceException(ErrorCode.INVALID_ARGUMENT); }
        if (target is not null) { using var lease = await targets.ResolveAsync(target, token); }
    }

    public async ValueTask<IAsyncDisposable> StartAsync(JsonElement condition, Action<SignalObservation> observe,
        CancellationToken startup, CancellationToken lifetime)
    {
        var processName = Text(condition, "processName");
        var title = Text(condition, "titleContains");
        string? path = null;
        if (Text(condition, "launchTargetId") is { } target)
        { using var lease = await targets.ResolveAsync(target, startup); path = lease.Target.ExecutablePath; }
        var baseline = discovery.List(null, null, startup);
        if (baseline.Truncated) { throw new WindowReferenceException(ErrorCode.SIGNAL_CONDITION_UNSUPPORTED); }
        var existing = baseline.Windows.Select(window => window.WindowId).ToHashSet(StringComparer.Ordinal);
        SignalObservation? Read(WindowReference reference, CancellationToken token)
        {
            if (existing.Contains(reference.WindowId)) { return null; }
            var info = discovery.Get(reference.WindowId, token);
            if (processName is not null && !info.ProcessName.Equals(processName, StringComparison.OrdinalIgnoreCase)
                || title is not null && !info.Title.Contains(title, StringComparison.OrdinalIgnoreCase)) { return null; }
            if (path is not null)
            {
                using var process = Process.GetProcessById(checked((int)reference.Identity.ProcessId));
                if (!string.Equals(process.MainModule?.FileName, path, StringComparison.OrdinalIgnoreCase)) { return null; }
            }
            windows.Resolve(reference.WindowId);
            return new(true, ResponseJson.ToElement(new { windowId = reference.WindowId }));
        }
        return await WindowEventSignalMonitor.StartAsync(windows, Read, observe, startup, lifetime);
    }

    private static string? Text(JsonElement condition, string key) =>
        condition.TryGetProperty(key, out var value) ? value.GetString() : null;
}
