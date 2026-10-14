using System.Text.Json;
using Kugutsu.Contracts;

namespace Kugutsu.App;

internal enum BrowserKind { Chrome, Edge, Firefox }
internal sealed record BrowserCapabilities(BrowserKind Kind, string BrowserVersion, string DriverVersion,
    bool CompatibilityVerified);
internal sealed record BrowserReference(string BrowserId, BrowserKind Kind, ApplicationReference Application,
    ApplicationReference Driver, BrowserCapabilities Capabilities);
internal sealed record BrowserTabReference(string TabId, string BrowserId, string NativeHandle);
internal sealed record ResolvedBrowser(BrowserReference Reference, IManagedBrowserConnection Connection);
internal sealed record ResolvedBrowserTab(ResolvedBrowser Browser, BrowserTabReference Tab);

/// <summary>アダプターが所有するWebDriverセッション。Disposeは通信資源だけを解放する。</summary>
internal interface IManagedBrowserConnection : IDisposable
{
    BrowserCapabilities Capabilities { get; }
    ApplicationReference Application { get; }
    ApplicationReference Driver { get; }
    ValueTask VerifyConnectionAsync(CancellationToken token);
    ValueTask<IReadOnlyList<string>> GetWindowHandlesAsync(CancellationToken token);
    ValueTask<JsonElement> ExecuteAsync(HttpMethod method, string command, JsonElement? body, CancellationToken token);
}

/// <summary>ブラウザとタブの参照を管理し、種類不一致・終了・通信断を区別する。</summary>
internal sealed class BrowserSessionRegistry(ApplicationReferenceRegistry applications) : IDisposable
{
    private sealed class Entry(BrowserReference reference, IManagedBrowserConnection connection)
    {
        internal BrowserReference Reference { get; } = reference;
        internal IManagedBrowserConnection Connection { get; } = connection;
        internal Dictionary<string, BrowserTabReference> Tabs { get; } = new(StringComparer.Ordinal);
    }

    private readonly object _gate = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private bool _disposed;

    internal static string Name(BrowserKind kind) => kind switch
    {
        BrowserKind.Chrome => "chrome", BrowserKind.Edge => "edge", BrowserKind.Firefox => "firefox",
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };

    internal static BrowserKind Parse(string name) => name switch
    {
        "chrome" => BrowserKind.Chrome, "edge" => BrowserKind.Edge, "firefox" => BrowserKind.Firefox,
        _ => throw new WindowReferenceException(ErrorCode.INVALID_ARGUMENT)
    };

    internal static bool ValidBrowserId(string? id) => ValidId(id, "b_");
    internal static bool ValidTabId(string? id) => ValidId(id, "bt_");
    private static bool ValidId(string? id, string prefix) => id is not null && id.Length == prefix.Length + 32
        && id.StartsWith(prefix, StringComparison.Ordinal) && Guid.TryParseExact(id.AsSpan(prefix.Length), "N", out _);

    internal async ValueTask<(BrowserReference Browser, IReadOnlyList<BrowserTabReference> Tabs)> RegisterAsync(
        IManagedBrowserConnection connection, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(connection);
        var capabilities = connection.Capabilities;
        if (!Enum.IsDefined(capabilities.Kind) || !capabilities.CompatibilityVerified
            || !ValidVersion(capabilities.BrowserVersion) || !ValidVersion(capabilities.DriverVersion))
        { throw new WindowReferenceException(ErrorCode.BROWSER_DRIVER_INCOMPATIBLE); }
        var reference = new BrowserReference("b_" + Guid.NewGuid().ToString("N"), capabilities.Kind,
            connection.Application, connection.Driver, capabilities);
        var entry = new Entry(reference, connection);
        var handles = await VerifyAsync(entry, token);
        if (handles.Count == 0) { throw new WindowReferenceException(ErrorCode.BROWSER_TAB_NOT_FOUND); }
        foreach (var handle in handles)
        {
            var tab = new BrowserTabReference("bt_" + Guid.NewGuid().ToString("N"), reference.BrowserId, handle);
            entry.Tabs.Add(tab.TabId, tab);
        }
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            token.ThrowIfCancellationRequested();
            if (_entries.Count >= 1000) { throw new WindowReferenceException(ErrorCode.BROWSER_INSTANCE_LIMIT_EXCEEDED); }
            _entries.Add(reference.BrowserId, entry);
        }
        return (reference, Array.AsReadOnly(entry.Tabs.Values.ToArray()));
    }

    internal async ValueTask<ResolvedBrowser> ResolveAsync(string browserId, BrowserKind expectedKind, CancellationToken token)
    {
        Entry entry;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            token.ThrowIfCancellationRequested();
            if (!_entries.TryGetValue(browserId, out entry!)) { throw new WindowReferenceException(ErrorCode.BROWSER_NOT_FOUND); }
            if (entry.Reference.Kind != expectedKind) { throw new WindowReferenceException(ErrorCode.BROWSER_TYPE_MISMATCH); }
        }
        var handles = await VerifyAsync(entry, token);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            token.ThrowIfCancellationRequested();
            if (!_entries.TryGetValue(browserId, out var current) || !ReferenceEquals(entry, current))
            { throw new WindowReferenceException(ErrorCode.BROWSER_NOT_FOUND); }
            foreach (var tab in entry.Tabs.Values.ToArray())
            {
                if (!handles.Contains(tab.NativeHandle, StringComparer.Ordinal)) { entry.Tabs.Remove(tab.TabId); }
            }
        }
        return new(entry.Reference, entry.Connection);
    }

    internal async ValueTask<ResolvedBrowserTab> ResolveTabAsync(string browserId, BrowserKind expectedKind,
        string tabId, CancellationToken token)
    {
        var browser = await ResolveAsync(browserId, expectedKind, token);
        lock (_gate)
        {
            token.ThrowIfCancellationRequested();
            if (!_entries.TryGetValue(browserId, out var entry) || !ReferenceEquals(entry.Connection, browser.Connection))
            { throw new WindowReferenceException(ErrorCode.BROWSER_NOT_FOUND); }
            if (!entry.Tabs.TryGetValue(tabId, out var tab)) { throw new WindowReferenceException(ErrorCode.BROWSER_TAB_NOT_FOUND); }
            return new(browser, tab);
        }
    }

    private async ValueTask<IReadOnlyList<string>> VerifyAsync(Entry entry, CancellationToken token)
    {
        if (entry.Reference.Application != entry.Connection.Application || entry.Reference.Driver != entry.Connection.Driver
            || entry.Reference.Capabilities != entry.Connection.Capabilities)
        { throw new WindowReferenceException(ErrorCode.BROWSER_CONNECTION_LOST); }
        VerifyProcesses(entry.Reference, token);
        await entry.Connection.VerifyConnectionAsync(token);
        var handles = (await entry.Connection.GetWindowHandlesAsync(token)).ToArray();
        if (handles.Length > 256 || handles.Any(handle => string.IsNullOrEmpty(handle) || handle.Length > 1024 || handle.Contains('\0'))
            || handles.Distinct(StringComparer.Ordinal).Count() != handles.Length)
        { throw new WindowReferenceException(ErrorCode.BROWSER_CONNECTION_LOST); }
        VerifyProcesses(entry.Reference, token);
        return handles;
    }

    private void VerifyProcesses(BrowserReference reference, CancellationToken token)
    {
        try { applications.Resolve(reference.Application.ApplicationId, token); }
        catch (WindowReferenceException error) when (error.Code is ErrorCode.APP_NOT_FOUND or ErrorCode.APP_INSTANCE_UNRESOLVED)
        { throw new WindowReferenceException(ErrorCode.BROWSER_NOT_FOUND); }
        try { applications.Resolve(reference.Driver.ApplicationId, token); }
        catch (WindowReferenceException error) when (error.Code is ErrorCode.APP_NOT_FOUND or ErrorCode.APP_INSTANCE_UNRESOLVED)
        { throw new WindowReferenceException(ErrorCode.BROWSER_CONNECTION_LOST); }
    }

    internal void Remove(string browserId)
    {
        lock (_gate)
        {
            if (_entries.Remove(browserId, out var entry)) { entry.Connection.Dispose(); }
        }
    }

    private static bool ValidVersion(string version) => version.Length is > 0 and <= 128 && !version.Contains('\0');

    internal static bool ValidUrl(string? url) => url is { Length: > 0 and <= 8192 }
        && !url.Any(char.IsControl) && Uri.TryCreate(url, UriKind.Absolute, out var parsed)
        && parsed.Scheme is "http" or "https" && string.IsNullOrEmpty(parsed.UserInfo) && !string.IsNullOrEmpty(parsed.Host);

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) { return; }
            _disposed = true;
            foreach (var entry in _entries.Values) { entry.Connection.Dispose(); }
            _entries.Clear();
        }
    }
}
