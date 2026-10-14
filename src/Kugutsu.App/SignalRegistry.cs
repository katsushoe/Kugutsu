using System.Text;
using System.Text.Json;
using Kugutsu.Contracts;
using Kugutsu.Hosting;

namespace Kugutsu.App;

internal enum SignalState { Pending, Signaled, Expired, Invalidated, Unregistered }
internal sealed record SignalSnapshot(string SignalId, SignalState State, DateTimeOffset ExpiresAt,
    DateTimeOffset? SignaledAt, JsonElement? Observed, ErrorCode? EndReason, long? SignaledTimestamp = null);

/// <summary>プロセス全体のシグナルを所有セッションと取得ロックの世代へ固定する。</summary>
internal sealed class SignalRegistry(OperationRequestQueue queue, KugutsuOptions settings) : IDisposable
{
    internal sealed class Entry(string id, Guid owner, Guid lockId, JsonElement condition, DateTimeOffset expiresAt)
    {
        internal string Id { get; } = id;
        internal Guid Owner { get; } = owner;
        internal Guid LockId { get; } = lockId;
        internal JsonElement Condition { get; } = condition.Clone();
        internal DateTimeOffset ExpiresAt { get; } = expiresAt;
        internal SignalState State { get; set; } = SignalState.Pending;
        internal DateTimeOffset? SignaledAt { get; set; }
        internal long? SignaledTimestamp { get; set; }
        internal DateTimeOffset? FinishedAt { get; set; }
        internal JsonElement? Observed { get; set; }
        internal ErrorCode? EndReason { get; set; }
        internal TaskCompletionSource<SignalSnapshot> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal SignalSnapshot Snapshot() => new(Id, State, ExpiresAt, SignaledAt, Observed?.Clone(), EndReason, SignaledTimestamp);
    }

    private readonly object _gate = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private bool _disposed;

    internal static bool ValidId(string? id) => id is { Length: 36 } && id.StartsWith("sig_", StringComparison.Ordinal)
        && Guid.TryParseExact(id.AsSpan(4), "N", out _);

    internal Entry Register(Guid owner, Guid lockId, JsonElement condition, int retentionSeconds, CancellationToken token)
    {
        if (condition.ValueKind != JsonValueKind.Object || retentionSeconds is < 1 or > 3600 || Encoding.UTF8.GetByteCount(condition.GetRawText()) > 65536)
        { throw new WindowReferenceException(ErrorCode.INVALID_ARGUMENT); }
        queue.VerifySignalLock(owner, lockId);
        Entry entry;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            token.ThrowIfCancellationRequested();
            Prune();
            if (_entries.Values.Count(item => item.State is SignalState.Pending or SignalState.Signaled) >= settings.MaxSignals)
            { throw new WindowReferenceException(ErrorCode.SIGNAL_LIMIT_EXCEEDED); }
            entry = new("sig_" + Guid.NewGuid().ToString("N"), owner, lockId, condition, DateTimeOffset.UtcNow.AddSeconds(retentionSeconds));
            _entries.Add(entry.Id, entry);
        }
        try { queue.VerifySignalLock(owner, lockId); return entry; }
        catch { Invalidate(entry, ErrorCode.MCP_LOCK_EXPIRED); throw; }
    }

    internal Entry Resolve(Guid owner, Guid lockId, string id, CancellationToken token)
    {
        Entry entry;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            token.ThrowIfCancellationRequested();
            Prune();
            if (!_entries.TryGetValue(id, out entry!)) { throw new WindowReferenceException(ErrorCode.SIGNAL_NOT_FOUND); }
            if (entry.Owner != owner || entry.LockId != lockId) { throw new WindowReferenceException(ErrorCode.SIGNAL_ACCESS_DENIED); }
        }
        queue.VerifySignalLock(owner, lockId);
        return entry;
    }

    internal SignalSnapshot Snapshot(Entry entry)
    {
        lock (_gate)
        {
            VerifyEntry(entry);
            Expire(entry);
            return entry.Snapshot();
        }
    }

    internal bool Signal(Entry entry, JsonElement observed)
    {
        if (observed.ValueKind == JsonValueKind.Undefined || Encoding.UTF8.GetByteCount(observed.GetRawText()) > 16384)
        { throw new WindowReferenceException(ErrorCode.SIGNAL_MONITOR_FAILED); }
        queue.VerifySignalLock(entry.Owner, entry.LockId);
        lock (_gate)
        {
            VerifyEntry(entry);
            Expire(entry);
            if (entry.State != SignalState.Pending) { return false; }
            entry.State = SignalState.Signaled;
            entry.SignaledAt = DateTimeOffset.UtcNow;
            entry.SignaledTimestamp = System.Diagnostics.Stopwatch.GetTimestamp();
            entry.Observed = observed.Clone();
            entry.Completion.TrySetResult(entry.Snapshot());
            return true;
        }
    }

    internal void Unregister(Entry entry) { lock (_gate) { VerifyEntry(entry); End(entry, SignalState.Unregistered, ErrorCode.SIGNAL_UNREGISTERED); } }
    internal void Invalidate(Entry entry, ErrorCode reason)
    {
        lock (_gate)
        {
            VerifyEntry(entry);
            End(entry, SignalState.Invalidated, reason);
        }
    }

    internal bool TryInvalidate(Entry entry, ErrorCode reason)
    {
        lock (_gate)
        {
            if (!_entries.TryGetValue(entry.Id, out var current) || !ReferenceEquals(entry, current)) { return false; }
            End(entry, SignalState.Invalidated, reason);
            return true;
        }
    }

    internal string[] Sweep()
    {
        Entry[] entries;
        lock (_gate) { if (_disposed) { return []; } entries = _entries.Values.ToArray(); }
        foreach (var entry in entries)
        {
            // Queue and registry gates are never held together.
            try { queue.VerifySignalLock(entry.Owner, entry.LockId); }
            catch (WindowReferenceException)
            {
                lock (_gate)
                {
                    if (_entries.TryGetValue(entry.Id, out var current) && ReferenceEquals(current, entry))
                    { Expire(entry); End(entry, SignalState.Unregistered, ErrorCode.SIGNAL_UNREGISTERED); }
                }
            }
        }
        lock (_gate)
        {
            foreach (var entry in _entries.Values) { Expire(entry); }
            var stopped = _entries.Values.Where(entry => entry.State != SignalState.Pending).Select(entry => entry.Id).ToArray();
            Prune();
            return stopped;
        }
    }

    internal bool IsPending(string id)
    {
        lock (_gate)
        {
            return _entries.TryGetValue(id, out var entry) && entry.State == SignalState.Pending;
        }
    }

    private void VerifyEntry(Entry entry)
    {
        if (!_entries.TryGetValue(entry.Id, out var current) || !ReferenceEquals(current, entry))
        { throw new WindowReferenceException(ErrorCode.SIGNAL_NOT_FOUND); }
    }

    private static void End(Entry entry, SignalState state, ErrorCode reason)
    {
        if (entry.State is SignalState.Expired or SignalState.Invalidated or SignalState.Unregistered) { return; }
        entry.State = state;
        entry.EndReason = reason;
        entry.FinishedAt = DateTimeOffset.UtcNow;
        entry.Completion.TrySetResult(entry.Snapshot());
    }

    private static void Expire(Entry entry)
    {
        if (entry.ExpiresAt <= DateTimeOffset.UtcNow) { End(entry, SignalState.Expired, ErrorCode.SIGNAL_EXPIRED); }
    }

    private void Prune()
    {
        foreach (var entry in _entries.Values) { Expire(entry); }
        var terminal = _entries.Values.Where(item => item.FinishedAt is not null).OrderBy(item => item.FinishedAt).ToArray();
        var earliest = DateTimeOffset.UtcNow.AddMinutes(-settings.ResultRetentionMinutes);
        for (var index = 0; index < terminal.Length; index++)
        {
            if (terminal[index].FinishedAt <= earliest || index < terminal.Length - 1000) { _entries.Remove(terminal[index].Id); }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) { return; }
            _disposed = true;
            foreach (var entry in _entries.Values) { End(entry, SignalState.Unregistered, ErrorCode.SIGNAL_UNREGISTERED); }
            _entries.Clear();
        }
    }
}
