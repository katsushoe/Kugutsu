using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using Kugutsu.Contracts;
using Microsoft.Win32.SafeHandles;

namespace Kugutsu.App;

internal sealed record ApplicationReference(string ApplicationId, string LaunchTargetId,
    int ProcessId, long ProcessStartedAt, string ExecutablePath);

/// <summary>起動したプロセスのハンドルを保持し、PID再利用や他の既存プロセスへの流用を拒否する。</summary>
internal sealed class ApplicationReferenceRegistry : IDisposable
{
    private sealed record Entry(ApplicationReference Reference, Process Process);
    private readonly object _gate = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly SecurityIdentifier _userSid;
    private readonly int _sessionId;
    private int _reserved;
    private bool _disposed;

    public ApplicationReferenceRegistry()
    {
        using var identity = WindowsIdentity.GetCurrent();
        _userSid = identity.User ?? throw new InvalidOperationException("Windows user SID unavailable.");
        using var current = Process.GetCurrentProcess();
        _sessionId = current.SessionId;
    }

    internal static bool IsValidId(string? id) => id is { Length: 34 }
        && id.StartsWith("a_", StringComparison.Ordinal) && Guid.TryParseExact(id.AsSpan(2), "N", out _);

    internal Reservation Reserve(CancellationToken token)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            token.ThrowIfCancellationRequested();
            foreach (var pair in _entries.ToArray())
            {
                if (!pair.Value.Process.HasExited) { continue; }
                _entries.Remove(pair.Key);
                pair.Value.Process.Dispose();
            }
            if (_entries.Count + _reserved >= 1000)
            { throw new WindowReferenceException(ErrorCode.APP_INSTANCE_LIMIT_EXCEEDED); }
            _reserved++;
            return new(this);
        }
    }

    internal ApplicationReference Resolve(string id, CancellationToken token)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            token.ThrowIfCancellationRequested();
            if (!_entries.TryGetValue(id, out var entry) || entry.Process.HasExited)
            { throw new WindowReferenceException(ErrorCode.APP_NOT_FOUND); }
            var started = VerifyProcess(entry.Process, entry.Reference.ExecutablePath);
            if (started != entry.Reference.ProcessStartedAt)
            { throw new WindowReferenceException(ErrorCode.APP_NOT_FOUND); }
            token.ThrowIfCancellationRequested();
            return entry.Reference;
        }
    }

    internal Process GetProcess(ApplicationReference reference, CancellationToken token)
    {
        lock (_gate)
        {
            if (Resolve(reference.ApplicationId, token) != reference)
            { throw new WindowReferenceException(ErrorCode.APP_NOT_FOUND); }
            return _entries[reference.ApplicationId].Process;
        }
    }

    internal bool HasEnded(ApplicationReference reference, CancellationToken token)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            token.ThrowIfCancellationRequested();
            if (!_entries.TryGetValue(reference.ApplicationId, out var entry) || entry.Reference != reference)
            { throw new WindowReferenceException(ErrorCode.APP_NOT_FOUND); }
            return entry.Process.HasExited;
        }
    }

    internal long Inspect(Process process, RegisteredLaunchTarget target) => VerifyProcess(process, target.ExecutablePath);

    private long VerifyProcess(Process process, string expectedPath)
    {
        if (process.HasExited) { throw new WindowReferenceException(ErrorCode.APP_INSTANCE_UNRESOLVED); }
        if (process.SessionId != _sessionId) { throw new WindowReferenceException(ErrorCode.ACCESS_DENIED); }
        if (!GetProcessTimes(process.SafeHandle, out var started, out _, out _, out _))
        { throw NativeFailure(); }
        var buffer = new StringBuilder(32768);
        var size = (uint)buffer.Capacity;
        if (!QueryFullProcessImageNameW(process.SafeHandle, 0, buffer, ref size)) { throw NativeFailure(); }
        if (!string.Equals(buffer.ToString(), expectedPath, StringComparison.OrdinalIgnoreCase))
        { throw new WindowReferenceException(ErrorCode.APP_INSTANCE_UNRESOLVED); }
        if (!OpenProcessToken(process.SafeHandle, 0x0008, out var token)) { throw NativeFailure(); }
        using (token)
        using (var identity = new WindowsIdentity(token.DangerousGetHandle()))
        {
            if (identity.User != _userSid) { throw new WindowReferenceException(ErrorCode.ACCESS_DENIED); }
        }
        if (process.HasExited) { throw new WindowReferenceException(ErrorCode.APP_INSTANCE_UNRESOLVED); }
        return started;
    }

    private static WindowReferenceException NativeFailure() => new(Marshal.GetLastWin32Error() == 5
        ? ErrorCode.ACCESS_DENIED : ErrorCode.APP_INSTANCE_UNRESOLVED);

    internal sealed class Reservation(ApplicationReferenceRegistry owner) : IDisposable
    {
        private bool _finished;

        internal ApplicationReference Register(Process process, RegisteredLaunchTarget target, CancellationToken token)
        {
            var started = owner.VerifyProcess(process, target.ExecutablePath);
            lock (owner._gate)
            {
                ObjectDisposedException.ThrowIf(owner._disposed, owner);
                token.ThrowIfCancellationRequested();
                if (_finished) { throw new InvalidOperationException("Application reservation already consumed."); }
                var reference = new ApplicationReference("a_" + Guid.NewGuid().ToString("N"), target.Id,
                    process.Id, started, target.ExecutablePath);
                owner._entries.Add(reference.ApplicationId, new(reference, process));
                owner._reserved--;
                _finished = true;
                return reference;
            }
        }

        public void Dispose()
        {
            lock (owner._gate)
            {
                if (_finished) { return; }
                owner._reserved--;
                _finished = true;
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) { return; }
            _disposed = true;
            foreach (var entry in _entries.Values) { entry.Process.Dispose(); }
            _entries.Clear();
        }
    }

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessTimes(SafeProcessHandle process, out long created, out long exited, out long kernel, out long user);
    [DllImport("kernel32.dll", ExactSpelling = true, CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageNameW(SafeProcessHandle process, uint flags, StringBuilder path, ref uint size);
    [DllImport("advapi32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(SafeProcessHandle process, uint access, out SafeAccessTokenHandle token);
}
