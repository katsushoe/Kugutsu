using Kugutsu.Contracts;

namespace Kugutsu.App;

internal enum ShellSessionKind { Terminal, PowerShell }
internal enum PowerShellFlavor { WindowsPowerShell, PowerShell7 }

/// <summary>入力先との対応を起動アダプター固有のOS情報で再照合する。破棄はシェルを終了しない。</summary>
internal interface IManagedShellBinding : IDisposable
{
    ShellSessionKind Kind { get; }
    PowerShellFlavor? PowerShell { get; }
    ApplicationReference Application { get; }
    ApplicationReference ShellProcess { get; }
    WindowReference Window { get; }
    object InputIdentity { get; }
    ValueTask VerifyBindingAsync(CancellationToken token);
}

internal sealed record ShellSessionReference(string SessionId, ShellSessionKind Kind, PowerShellFlavor? PowerShell,
    ApplicationReference Application, ApplicationReference ShellProcess, WindowReference Window);
internal sealed record ResolvedShellSession(ShellSessionReference Reference, IManagedShellBinding Binding);

/// <summary>シェル専用IDと管理プロセス・ウィンドウ・入力先を固定して保持する。</summary>
internal sealed class ShellSessionRegistry(ApplicationReferenceRegistry applications, WindowReferenceRegistry windows) : IDisposable
{
    private sealed record Entry(ShellSessionReference Reference, IManagedShellBinding Binding, object InputIdentity);
    private readonly object _gate = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private bool _disposed;

    internal static bool IsValidId(string? id) => id is { Length: 35 } && id.StartsWith("sh_", StringComparison.Ordinal)
        && Guid.TryParseExact(id.AsSpan(3), "N", out _);

    internal async ValueTask<ShellSessionReference> RegisterAsync(IManagedShellBinding binding, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(binding);
        if (!Enum.IsDefined(binding.Kind) || binding.PowerShell is { } flavor && !Enum.IsDefined(flavor)
            || binding.Kind == ShellSessionKind.PowerShell && binding.PowerShell is null
            || binding.InputIdentity is null)
        { throw new WindowReferenceException(ErrorCode.SESSION_TARGET_UNRESOLVED); }
        var reference = new ShellSessionReference("sh_" + Guid.NewGuid().ToString("N"), binding.Kind,
            binding.PowerShell, binding.Application, binding.ShellProcess, binding.Window);
        var entry = new Entry(reference, binding, binding.InputIdentity);
        await VerifyAsync(entry, token);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            token.ThrowIfCancellationRequested();
            if (_entries.Count >= 1000) { PruneEnded(token); }
            if (_entries.Count >= 1000) { throw new WindowReferenceException(ErrorCode.SESSION_LIMIT_EXCEEDED); }
            _entries.Add(reference.SessionId, entry);
        }
        // 登録成功後だけBindingの所有権をレジストリへ移す。
        return reference;
    }

    internal async ValueTask<ResolvedShellSession> ResolveAsync(string id, ShellSessionKind expectedKind, CancellationToken token)
    {
        Entry entry;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            token.ThrowIfCancellationRequested();
            if (!_entries.TryGetValue(id, out entry!)) { throw new WindowReferenceException(ErrorCode.SESSION_NOT_FOUND); }
            if (entry.Reference.Kind != expectedKind) { throw new WindowReferenceException(ErrorCode.SESSION_TYPE_MISMATCH); }
        }
        await VerifyAsync(entry, token);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            token.ThrowIfCancellationRequested();
            if (!_entries.TryGetValue(id, out var current) || !ReferenceEquals(current, entry))
            { throw new WindowReferenceException(ErrorCode.SESSION_NOT_FOUND); }
        }
        return new(entry.Reference, entry.Binding);
    }

    private async ValueTask VerifyAsync(Entry entry, CancellationToken token)
    {
        var reference = entry.Reference;
        VerifyIdentity(entry);
        try
        {
            VerifyReferences(reference, token);
            await entry.Binding.VerifyBindingAsync(token);
            VerifyIdentity(entry);
            VerifyReferences(reference, token);
        }
        catch (WindowReferenceException exception) when (exception.Code is ErrorCode.APP_NOT_FOUND
            or ErrorCode.APP_INSTANCE_UNRESOLVED or ErrorCode.WINDOW_NOT_FOUND or ErrorCode.UI_ELEMENT_STALE or ErrorCode.UIA_ELEMENT_STALE)
        { throw new WindowReferenceException(ErrorCode.SESSION_TARGET_UNRESOLVED); }
    }

    private static void VerifyIdentity(Entry entry)
    {
        var reference = entry.Reference;
        var binding = entry.Binding;
        if (binding.Kind != reference.Kind || binding.PowerShell != reference.PowerShell
            || binding.Application != reference.Application || binding.ShellProcess != reference.ShellProcess
            || binding.Window != reference.Window || !ReferenceEquals(binding.InputIdentity, entry.InputIdentity))
        { throw new WindowReferenceException(ErrorCode.SESSION_TARGET_UNRESOLVED); }
    }

    private void VerifyReferences(ShellSessionReference reference, CancellationToken token)
    {
        if (applications.Resolve(reference.Application.ApplicationId, token) != reference.Application
            || applications.Resolve(reference.ShellProcess.ApplicationId, token) != reference.ShellProcess
            || windows.Resolve(reference.Window.WindowId) != reference.Window)
        { throw new WindowReferenceException(ErrorCode.SESSION_TARGET_UNRESOLVED); }
        token.ThrowIfCancellationRequested();
    }

    private void PruneEnded(CancellationToken token)
    {
        foreach (var pair in _entries.ToArray())
        {
            token.ThrowIfCancellationRequested();
            var ended = false;
            try { ended = applications.HasEnded(pair.Value.Reference.ShellProcess, token)
                || applications.HasEnded(pair.Value.Reference.Application, token); }
            catch (WindowReferenceException exception) when (exception.Code == ErrorCode.APP_NOT_FOUND) { ended = true; }
            if (!ended) { continue; }
            _entries.Remove(pair.Key);
            pair.Value.Binding.Dispose();
        }
    }

    internal void Remove(string id)
    {
        lock (_gate)
        {
            if (_entries.Remove(id, out var entry)) { entry.Binding.Dispose(); }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) { return; }
            _disposed = true;
            foreach (var entry in _entries.Values) { entry.Binding.Dispose(); }
            _entries.Clear();
        }
    }
}
