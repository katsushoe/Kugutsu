using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Kugutsu.Contracts;
using Kugutsu.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Win32.SafeHandles;

namespace Kugutsu.App;

internal sealed class WindowReferenceException(ErrorCode code, System.Text.Json.JsonElement? resultData = null) : Exception("Window reference validation failed.")
{
    internal ErrorCode Code { get; } = code;
    internal System.Text.Json.JsonElement? ResultData { get; } = resultData?.Clone();
}

internal sealed record WindowIdentity(nint Handle, uint ProcessId, uint ThreadId, long ProcessStartedAt);
internal sealed record WindowReference(string WindowId, WindowIdentity Identity);

/// <summary>ウィンドウの世代とプロセスの識別情報を保持するプロセス内レジストリ。</summary>
internal sealed class WindowReferenceRegistry : IDisposable
{
    private sealed record Entry(WindowReference Reference, nint Marker);
    private readonly object _gate = new();
    private readonly Dictionary<string, Entry> _byId = new(StringComparer.Ordinal);
    private readonly Dictionary<nint, Entry> _byHandle = new();
    private readonly string _propertyName = "Kugutsu.WindowRef." + Guid.NewGuid().ToString("N");
    private readonly SecurityIdentifier _userSid;
    private readonly uint _sessionId;
    private readonly ILogger<WindowReferenceRegistry> _logger;
    private int _nextMarker;
    private bool _disposed;

    public WindowReferenceRegistry(ILogger<WindowReferenceRegistry> logger)
    {
        _logger = logger;
        using var user = WindowsIdentity.GetCurrent();
        _userSid = user.User ?? throw new InvalidOperationException("Windows user SID unavailable.");
        using var process = Process.GetCurrentProcess();
        _sessionId = checked((uint)process.SessionId);
    }

    internal WindowReference Register(nint handle)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_byHandle.TryGetValue(handle, out var existing))
            {
                if (Validate(existing) is null) { return existing.Reference; }
                RemoveEntry(existing);
            }
            var identity = ReadIdentity(handle);
            if (_nextMarker == int.MaxValue)
            { throw new WindowReferenceException(ErrorCode.INTERNAL_ERROR); }
            var marker = (nint)(++_nextMarker);
            if (!SetProp(handle, _propertyName, marker))
            { throw new WindowReferenceException(NativeFailure()); }
            var reference = new WindowReference("w_" + Guid.NewGuid().ToString("N"), identity);
            var entry = new Entry(reference, marker);
            var error = Validate(entry);
            if (error is not null)
            {
                RemoveMarker(entry);
                throw new WindowReferenceException(error.Value);
            }
            _byId.Add(reference.WindowId, entry);
            _byHandle.Add(handle, entry);
            return reference;
        }
    }

    internal static bool IsValidId(string? windowId) => windowId is { Length: 34 }
        && windowId.StartsWith("w_", StringComparison.Ordinal)
        && Guid.TryParseExact(windowId.AsSpan(2), "N", out _);

    internal WindowReference Resolve(string windowId)
    {
        if (!IsValidId(windowId))
        { throw new WindowReferenceException(ErrorCode.INVALID_ARGUMENT); }
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_byId.TryGetValue(windowId, out var entry))
            { throw new WindowReferenceException(ErrorCode.WINDOW_NOT_FOUND); }
            var error = Validate(entry);
            if (error is not null)
            {
                RemoveEntry(entry);
                throw new WindowReferenceException(error.Value);
            }
            return entry.Reference;
        }
    }

    /// <summary>終了要求前に保持した対象の消滅を確認する。マーカー欠落だけを終了としない。</summary>
    internal bool HasWindowEnded(WindowReference reference)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var ended = !IsWindow(reference.Identity.Handle);
            if (!ended)
            {
                try { ended = ReadIdentity(reference.Identity.Handle) != reference.Identity; }
                catch (WindowReferenceException exception) when (exception.Code == ErrorCode.WINDOW_NOT_FOUND
                    && !IsWindow(reference.Identity.Handle)) { ended = true; }
            }
            if (ended)
            {
                if (_byId.TryGetValue(reference.WindowId, out var old) && old.Reference == reference)
                { RemoveEntry(old); }
                return true;
            }
            if (!_byId.TryGetValue(reference.WindowId, out var entry) || entry.Reference != reference
                || GetProp(reference.Identity.Handle, _propertyName) != entry.Marker)
            { throw new WindowReferenceException(ErrorCode.WINDOW_NOT_FOUND); }
            return false;
        }
    }

    internal void PruneInvalidReferences()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            foreach (var entry in _byId.Values.ToArray())
            { if (Validate(entry) is not null) { RemoveEntry(entry); } }
        }
    }

    private ErrorCode? Validate(Entry entry)
    {
        try
        {
            if (ReadIdentity(entry.Reference.Identity.Handle) != entry.Reference.Identity
                || GetProp(entry.Reference.Identity.Handle, _propertyName) != entry.Marker)
            { return ErrorCode.WINDOW_NOT_FOUND; }
            return null;
        }
        catch (WindowReferenceException exception) { return exception.Code; }
    }

    private WindowIdentity ReadIdentity(nint handle)
    {
        if (handle == 0 || !IsWindow(handle))
        { throw new WindowReferenceException(ErrorCode.WINDOW_NOT_FOUND); }
        var threadId = GetWindowThreadProcessId(handle, out var processId);
        if (threadId == 0 || processId == 0)
        { throw new WindowReferenceException(ErrorCode.WINDOW_NOT_FOUND); }
        if (!ProcessIdToSessionId(processId, out var sessionId))
        { throw new WindowReferenceException(NativeFailure()); }
        if (sessionId != _sessionId) { throw new WindowReferenceException(ErrorCode.ACCESS_DENIED); }
        using var process = OpenProcess(0x1000, false, processId); // PROCESS_QUERY_LIMITED_INFORMATION
        if (process.IsInvalid) { throw new WindowReferenceException(NativeFailure()); }
        if (!GetProcessTimes(process, out var startedAt, out _, out _, out _))
        { throw new WindowReferenceException(NativeFailure()); }
        if (!OpenProcessToken(process, 0x0008, out var token)) // TOKEN_QUERY
        { throw new WindowReferenceException(NativeFailure()); }
        using (token)
        {
            try
            {
                using var identity = new WindowsIdentity(token.DangerousGetHandle());
                if (identity.User != _userSid) { throw new WindowReferenceException(ErrorCode.ACCESS_DENIED); }
            }
            catch (Exception exception) when (exception is UnauthorizedAccessException or System.Security.SecurityException)
            { throw new WindowReferenceException(ErrorCode.ACCESS_DENIED); }
        }
        if (!IsWindow(handle) || GetWindowThreadProcessId(handle, out var confirmedProcessId) != threadId
            || confirmedProcessId != processId)
        { throw new WindowReferenceException(ErrorCode.WINDOW_NOT_FOUND); }
        return new(handle, processId, threadId, startedAt);
    }

    private static ErrorCode NativeFailure() => Marshal.GetLastWin32Error() == 5
        ? ErrorCode.ACCESS_DENIED : ErrorCode.WINDOW_NOT_FOUND;

    private void RemoveEntry(Entry entry)
    {
        _byId.Remove(entry.Reference.WindowId);
        _byHandle.Remove(entry.Reference.Identity.Handle);
        RemoveMarker(entry);
    }

    private void RemoveMarker(Entry entry)
    {
        var handle = entry.Reference.Identity.Handle;
        if (GetProp(handle, _propertyName) == entry.Marker && RemoveProp(handle, _propertyName) != entry.Marker)
        {
            _logger.WriteEvent(LogLevel.Warning, LogEvent.OperationCompleted, LogOutcome.Failed,
                operation: LogOperation.Window, error: LogError.OperationFailed);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) { return; }
            _disposed = true;
            foreach (var entry in _byId.Values) { RemoveMarker(entry); }
            _byId.Clear();
            _byHandle.Clear();
        }
    }

    [DllImport("user32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindow(nint handle);
    [DllImport("user32.dll", ExactSpelling = true, SetLastError = true)]
    private static extern uint GetWindowThreadProcessId(nint handle, out uint processId);
    [DllImport("user32.dll", EntryPoint = "SetPropW", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetProp(nint handle, string name, nint value);
    [DllImport("user32.dll", EntryPoint = "GetPropW", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern nint GetProp(nint handle, string name);
    [DllImport("user32.dll", EntryPoint = "RemovePropW", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern nint RemoveProp(nint handle, string name);
    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ProcessIdToSessionId(uint processId, out uint sessionId);
    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint processId);
    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessTimes(SafeProcessHandle process, out long created, out long exited, out long kernel, out long user);
    [DllImport("advapi32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(SafeProcessHandle process, uint access, out SafeAccessTokenHandle token);
}
