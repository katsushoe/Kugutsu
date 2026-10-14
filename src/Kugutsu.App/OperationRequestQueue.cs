using Kugutsu.Contracts;

namespace Kugutsu.App;

internal enum QueueAdmission
{
    Accepted,
    Duplicate,
    Conflict,
    Full,
    LockPending,
    Locked,
    LockTokenInvalid,
    LockExpired,
    Stopped
}

internal sealed record McpLockGrant(string LockToken, DateTimeOffset ExpiresAt);
internal sealed record McpLockStatus(bool Locked, bool OwnedByCaller,
    DateTimeOffset? ExpiresAt, long RemainingMs);
internal sealed record McpLockDisplayStatus(bool Locked, DateTimeOffset? ExpiresAt,
    bool StopConfirmationPending);
internal sealed record EmergencyStopStatus(bool Active, bool StopConfirmationPending,
    bool ForceUnlockPending, bool ShuttingDown);

internal enum McpLockReleaseResult
{
    Released,
    AlreadyReleased,
    Busy,
    Locked,
    TokenInvalid,
    Expired
}

internal sealed class ReleasedMcpLock(Guid ownerId, byte[] tokenHash, DateTimeOffset releasedAt)
{
    internal Guid OwnerId { get; } = ownerId;
    internal byte[] TokenHash { get; } = tokenHash;
    internal DateTimeOffset ReleasedAt { get; } = releasedAt;
}

internal sealed class McpLockWaiter(Guid ownerId, TimeSpan lease)
{
    internal Guid LockId { get; } = Guid.NewGuid();
    internal Guid OwnerId { get; } = ownerId;
    internal TimeSpan Lease { get; } = lease;
    internal string? LockToken { get; set; }
    internal DateTimeOffset ExpiresAt { get; set; }
    internal bool IsExpiring { get; set; }
    internal TaskCompletionSource<McpLockGrant?> Completion { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
}

internal sealed record QueuedRequestSnapshot(Guid RequestId, Guid OwnerId, RequestState State,
    DateTimeOffset AcceptedAt, DateTimeOffset? StartedAt, DateTimeOffset? FinishedAt);
internal sealed record OperationBufferEntry(Guid RequestId, RequestState State,
    string OperationSummary, DateTimeOffset AcceptedAt);

internal sealed record QueuedOperation(string Name,
    Func<Guid, CancellationToken, ValueTask<System.Text.Json.JsonElement?>> Execute, bool UseRequestDeadline = false);

/// <summary>A request admitted to the FIFO worker and its completion signal.</summary>
internal sealed class QueuedRequest
{
    private readonly object _gate = new();
    private readonly TaskCompletionSource<RequestState> _completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private RequestState _state = RequestState.Queued;
    private DateTimeOffset? _startedAt;
    private DateTimeOffset? _finishedAt;
    private bool _cancellationRequested;
    private long? _cancellationRequestedTimestamp;
    private readonly CancellationTokenSource _cancellation = new();
    private readonly IReadOnlyList<QueuedOperation> _queuedOperations;
    private byte[]? _lockTokenHash;
    private ToolError? _requestError;

    private readonly List<OperationResult> _operations = [];

    internal QueuedRequest(Guid requestId, Guid ownerId, IReadOnlyList<QueuedOperation> operations,
        string fingerprint, byte[]? lockTokenHash)
    {
        ArgumentOutOfRangeException.ThrowIfZero(operations.Count);
        RequestId = requestId;
        OwnerId = ownerId;
        _queuedOperations = operations.ToArray();
        Fingerprint = fingerprint;
        _lockTokenHash = lockTokenHash?.ToArray();
        AcceptedAt = DateTimeOffset.UtcNow;
        AcceptedTimestamp = System.Diagnostics.Stopwatch.GetTimestamp();
    }

    internal Guid RequestId { get; }
    internal Guid OwnerId { get; }
    internal string OperationName => _queuedOperations[0].Name;
    internal IReadOnlyList<QueuedOperation> Operations => _queuedOperations;
    internal string Fingerprint { get; }
    internal bool HasLockToken => _lockTokenHash is not null;
    internal bool HasLockTokenHash(byte[] expectedHash)
    {
        lock (_gate)
        { return _lockTokenHash is not null && System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(_lockTokenHash, expectedHash); }
    }
    internal DateTimeOffset AcceptedAt { get; }
    internal long AcceptedTimestamp { get; }
    internal CancellationToken CancellationToken => _cancellation.Token;
    internal bool IsCancellationRequested { get { lock (_gate) { return _cancellationRequested; } } }
    public Task<RequestState> Completion => _completion.Task;

    internal bool IsQueueExpired(TimeSpan timeout) =>
        System.Diagnostics.Stopwatch.GetElapsedTime(AcceptedTimestamp) >= timeout;

    internal bool WasCancellationRequestedBefore(long operationStartedTimestamp, TimeSpan timeout)
    {
        lock (_gate)
        {
            return _cancellationRequestedTimestamp is { } requestedAt
                && System.Diagnostics.Stopwatch.GetElapsedTime(operationStartedTimestamp, requestedAt) < timeout;
        }
    }

    public QueuedRequestSnapshot GetSnapshot()
    {
        lock (_gate) { return new(RequestId, OwnerId, _state, AcceptedAt, _startedAt, _finishedAt); }
    }

    internal ToolResponse GetResponse()
    {
        lock (_gate)
        {
            var error = _requestError ?? _operations.LastOrDefault()?.Error;
            return ToolResponse.Request(RequestId, _state, _operations.ToArray(), error);
        }
    }

    internal void Start()
    {
        lock (_gate)
        {
            _state = RequestState.Running;
            _startedAt = DateTimeOffset.UtcNow;
            _operations.Add(new OperationResult(0, OperationName, OperationState.Running,
                _startedAt, null, null, null));
        }
    }

    internal void StartOperation(int index)
    {
        lock (_gate)
        {
            if (_state != RequestState.Running || index != _operations.Count
                || index >= _queuedOperations.Count)
            { throw new InvalidOperationException("Invalid queued operation transition."); }
            var now = DateTimeOffset.UtcNow;
            _operations.Add(new OperationResult(index, _queuedOperations[index].Name,
                OperationState.Running, now, null, null, null));
        }
    }

    internal void CompleteOperation(int index, OperationState state,
        System.Text.Json.JsonElement? data = null, ToolError? error = null)
    {
        if (state is not (OperationState.Succeeded or OperationState.Failed
            or OperationState.Cancelled or OperationState.TimedOut))
        { throw new ArgumentOutOfRangeException(nameof(state)); }
        lock (_gate)
        {
            if (index < 0 || index >= _operations.Count || index != _operations.Count - 1
                || _operations[index].State != OperationState.Running)
            { throw new InvalidOperationException("Only the active queued operation can complete."); }
            error ??= state switch
            {
                OperationState.Cancelled => new ToolError(ErrorCode.CANCELLED),
                OperationState.TimedOut => new ToolError(ErrorCode.TIMEOUT),
                OperationState.Failed => new ToolError(ErrorCode.INTERNAL_ERROR),
                _ => null
            };
            var current = _operations[index];
            _operations[index] = new OperationResult(current.Index, current.Name, state,
                current.StartedAt, DateTimeOffset.UtcNow,
                data is { ValueKind: not System.Text.Json.JsonValueKind.Undefined } value
                    ? value.Clone() : null, error);
        }
    }

    internal bool TryMarkCancellationRequested(out bool newlyRequested)
    {
        newlyRequested = false;
        lock (_gate)
        {
            if (_state != RequestState.Running) { return false; }
            newlyRequested = !_cancellationRequested;
            _cancellationRequested = true;
            _cancellationRequestedTimestamp ??= System.Diagnostics.Stopwatch.GetTimestamp();
        }
        return true;
    }

    internal void SignalCancellation()
    {
        try { _cancellation.Cancel(); }
        catch (AggregateException) { /* A failing callback cannot undo the cancellation signal. */ }
    }

    internal Task SignalCancellationAsync() => _cancellation.CancelAsync();

    internal void Finish(RequestState state, System.Text.Json.JsonElement? data = null, ToolError? error = null)
    {
        if (state is not (RequestState.Succeeded or RequestState.Failed or RequestState.Cancelled or RequestState.TimedOut))
        { throw new ArgumentOutOfRangeException(nameof(state)); }
        lock (_gate)
        {
            var startedAt = _startedAt;
            _state = state;
            _finishedAt = DateTimeOffset.UtcNow;
            _requestError = error;
            var operationState = state switch
            {
                RequestState.Succeeded => OperationState.Succeeded,
                RequestState.Cancelled => OperationState.Cancelled,
                RequestState.TimedOut => OperationState.TimedOut,
                _ => OperationState.Failed
            };
            error ??= operationState switch
            {
                OperationState.Cancelled => new ToolError(ErrorCode.CANCELLED),
                OperationState.TimedOut => new ToolError(ErrorCode.TIMEOUT),
                OperationState.Failed => new ToolError(ErrorCode.INTERNAL_ERROR),
                _ => null
            };
            var runningIndex = _operations.FindLastIndex(static operation => operation.State == OperationState.Running);
            if (runningIndex >= 0)
            {
                var running = _operations[runningIndex];
                _operations[runningIndex] = new OperationResult(running.Index, running.Name,
                    operationState, running.StartedAt, _finishedAt,
                    data is { ValueKind: not System.Text.Json.JsonValueKind.Undefined } value
                        ? value.Clone() : null, error);
            }
            else if (_operations.Count == 0)
            {
                _operations.Add(new OperationResult(0, OperationName, operationState, startedAt,
                    _finishedAt, data, error));
            }
            for (var index = _operations.Count; index < _queuedOperations.Count; index++)
            {
                _operations.Add(new OperationResult(index, _queuedOperations[index].Name,
                    OperationState.Skipped, null, null, null, null));
            }
            if (_lockTokenHash is not null)
            {
                System.Security.Cryptography.CryptographicOperations.ZeroMemory(_lockTokenHash);
                _lockTokenHash = null;
            }
            _completion.TrySetResult(state);
        }
    }
}

/// <summary>Bounded FIFO with one consumer; capacity counts waiting requests, not the running request.</summary>
internal sealed class OperationRequestQueue : IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly Queue<QueuedRequest> _pending = new();
    private readonly Queue<McpLockWaiter> _lockWaiters = new();
    private readonly List<ReleasedMcpLock> _releasedLocks = [];
    private readonly Dictionary<Guid, List<QueuedRequest>> _requests = [];
    private QueuedRequest? _running;
    private McpLockWaiter? _activeLock;
    private readonly SemaphoreSlim _available = new(0);
    private readonly CancellationTokenSource _stopping = new();
    private readonly Task _worker;
    private readonly System.Threading.Timer _resultRetentionTimer;
    private readonly System.Threading.Timer _lockLeaseTimer;
    private readonly int _capacity;
    private readonly TimeSpan _queueTimeout;
    private readonly TimeSpan _operationTimeout;
    private readonly TimeSpan _requestTimeout;
    private readonly TimeSpan _resultRetention;
    private const int MaxRetainedResults = 1000;
    private readonly Microsoft.Extensions.Logging.ILogger<OperationRequestQueue> _logger;
    private bool _stopped;
    private bool _emergencyStopped;
    private bool _emergencyCancellationPending;
    private bool _forceUnlockPending;
    private Task _localCancellationTask = Task.CompletedTask;
    private Task? _shutdownTask;

    public OperationRequestQueue(int capacity, int queueTimeoutSeconds, int operationTimeoutSeconds,
        int requestTimeoutSeconds, int resultRetentionMinutes,
        Microsoft.Extensions.Logging.ILogger<OperationRequestQueue> logger)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(queueTimeoutSeconds);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(operationTimeoutSeconds);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(requestTimeoutSeconds);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(resultRetentionMinutes);
        _capacity = capacity;
        _queueTimeout = TimeSpan.FromSeconds(queueTimeoutSeconds);
        _operationTimeout = TimeSpan.FromSeconds(operationTimeoutSeconds);
        _requestTimeout = TimeSpan.FromSeconds(requestTimeoutSeconds);
        _resultRetention = TimeSpan.FromMinutes(resultRetentionMinutes);
        _logger = logger;
        _resultRetentionTimer = new System.Threading.Timer(static state =>
            ((OperationRequestQueue)state!).SweepRetainedResults(), this,
            TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));
        _lockLeaseTimer = new System.Threading.Timer(static state =>
            ((OperationRequestQueue)state!).ExpireActiveLock(), this,
            Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        _worker = RunAsync();
    }

    public QueueAdmission TryEnqueue(Guid requestId, Guid ownerId, string operationName,
        string fingerprint, Func<Guid, CancellationToken, ValueTask<System.Text.Json.JsonElement?>> execute,
        out QueuedRequest? request, string? lockToken = null)
    {
        ArgumentNullException.ThrowIfNull(execute);
        ArgumentException.ThrowIfNullOrWhiteSpace(operationName);
        return TryEnqueueBatch(requestId, ownerId, fingerprint,
            [new QueuedOperation(operationName, execute)], out request, lockToken);
    }

    public QueueAdmission TryEnqueueBatch(Guid requestId, Guid ownerId, string fingerprint,
        IReadOnlyList<QueuedOperation> operations, out QueuedRequest? request, string? lockToken = null)
    {
        ArgumentNullException.ThrowIfNull(operations);
        if (operations.Count == 0 || operations.Any(operation =>
            string.IsNullOrWhiteSpace(operation.Name) || operation.Execute is null))
        { throw new ArgumentException("At least one valid operation is required.", nameof(operations)); }
        request = null;
        lock (_gate)
        {
            PruneCompletedRequests(DateTimeOffset.UtcNow);
            if (FindExisting(requestId, ownerId) is { } existing)
            {
                request = existing;
                return string.Equals(existing.Fingerprint, fingerprint, StringComparison.Ordinal)
                    ? QueueAdmission.Duplicate : QueueAdmission.Conflict;
            }
            if (_stopped || _emergencyStopped) { return QueueAdmission.Stopped; }
            var lockAdmission = CheckLockAdmission(ownerId, lockToken);
            if (lockAdmission is not null) { return lockAdmission.Value; }
            if (_pending.Count >= _capacity) { return QueueAdmission.Full; }
            var lockTokenHash = lockToken is null ? null : HashToken(lockToken);
            try { request = new QueuedRequest(requestId, ownerId, operations, fingerprint, lockTokenHash); }
            finally
            {
                if (lockTokenHash is not null)
                { System.Security.Cryptography.CryptographicOperations.ZeroMemory(lockTokenHash); }
            }
            _pending.Enqueue(request);
            if (!_requests.TryGetValue(requestId, out var requests))
            { requests = []; _requests.Add(requestId, requests); }
            requests.Add(request);
            _available.Release();
        }
        return QueueAdmission.Accepted;
    }

    private QueueAdmission? CheckLockAdmission(Guid ownerId, string? lockToken)
    {
        if (_lockWaiters.Count > 0) { return QueueAdmission.LockPending; }
        if (_activeLock is not { } active)
        { return lockToken is null ? null : QueueAdmission.LockExpired; }
        if (active.OwnerId != ownerId) { return QueueAdmission.Locked; }
        if (active.LockToken is null || active.ExpiresAt <= DateTimeOffset.UtcNow)
        { return QueueAdmission.LockExpired; }
        if (lockToken is null || !TokensEqual(active.LockToken, lockToken))
        { return QueueAdmission.LockTokenInvalid; }
        return null;
    }

    internal Guid RequireSignalLock(Guid ownerId, string lockToken)
    {
        lock (_gate)
        {
            if (_stopped || _emergencyStopped || _forceUnlockPending) { throw new WindowReferenceException(ErrorCode.MCP_LOCK_EXPIRED); }
            var failure = CheckLockAdmission(ownerId, lockToken);
            if (failure is { } rejected)
            { throw new WindowReferenceException(rejected == QueueAdmission.LockPending ? ErrorCode.MCP_LOCK_PENDING : ToLockError(rejected)); }
            return _activeLock?.LockId ?? throw new WindowReferenceException(ErrorCode.MCP_LOCK_EXPIRED);
        }
    }

    internal void VerifySignalLock(Guid ownerId, Guid lockId)
    {
        lock (_gate)
        {
            if (_stopped || _emergencyStopped || _forceUnlockPending || _activeLock is not { } active
                || active.LockId != lockId || active.ExpiresAt <= DateTimeOffset.UtcNow)
            { throw new WindowReferenceException(ErrorCode.MCP_LOCK_EXPIRED); }
            if (active.OwnerId != ownerId) { throw new WindowReferenceException(ErrorCode.SIGNAL_ACCESS_DENIED); }
        }
    }

    private QueueAdmission? CheckRunningLock(Guid ownerId, QueuedRequest request)
    {
        lock (_gate)
        {
            if (_activeLock is not { } active) { return QueueAdmission.LockExpired; }
            if (active.OwnerId != ownerId) { return QueueAdmission.Locked; }
            if (active.LockToken is null || active.ExpiresAt <= DateTimeOffset.UtcNow)
            { return QueueAdmission.LockExpired; }
            var expectedHash = HashToken(active.LockToken);
            try { return request.HasLockTokenHash(expectedHash) ? null : QueueAdmission.LockTokenInvalid; }
            finally { System.Security.Cryptography.CryptographicOperations.ZeroMemory(expectedHash); }
        }
    }

    private static ErrorCode ToLockError(QueueAdmission admission) => admission switch
    {
        QueueAdmission.Locked => ErrorCode.MCP_LOCKED,
        QueueAdmission.LockTokenInvalid => ErrorCode.MCP_LOCK_TOKEN_INVALID,
        QueueAdmission.LockExpired => ErrorCode.MCP_LOCK_EXPIRED,
        _ => ErrorCode.INTERNAL_ERROR
    };

    private static bool TokensEqual(string expected, string provided)
    {
        if (!IsLockTokenFormatValid(provided)) { return false; }
        var expectedHash = HashToken(expected);
        var providedHash = HashToken(provided);
        try { return System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(expectedHash, providedHash); }
        finally
        {
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(expectedHash);
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(providedHash);
        }
    }

    internal static bool IsLockTokenFormatValid(string token) => token.Length == 43
        && token.All(character => character is >= 'A' and <= 'Z' or >= 'a' and <= 'z'
            or >= '0' and <= '9' or '-' or '_');

    private static byte[] HashToken(string token)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(token);
        try { return System.Security.Cryptography.SHA256.HashData(bytes); }
        finally { System.Security.Cryptography.CryptographicOperations.ZeroMemory(bytes); }
    }

    public async Task<McpLockGrant?> AcquireLockAsync(Guid ownerId, TimeSpan acquireTimeout,
        TimeSpan lease, CancellationToken cancellationToken)
    {
        var waiter = new McpLockWaiter(ownerId, lease);
        lock (_gate)
        {
            if (_stopped) { return null; }
            if (_activeLock is { } current && current.OwnerId == ownerId)
            {
                return current.LockToken is { } token
                    ? new McpLockGrant(token, current.ExpiresAt)
                    : current.Completion.Task.GetAwaiter().GetResult();
            }
            _lockWaiters.Enqueue(waiter);
            TryGrantNextLockWaiter();
        }

        if (waiter.Completion.Task.IsCompleted)
        { return await waiter.Completion.Task.ConfigureAwait(false); }
        try
        {
            var delay = Task.Delay(acquireTimeout, cancellationToken);
            var completed = await Task.WhenAny(waiter.Completion.Task, delay).ConfigureAwait(false);
            if (!ReferenceEquals(completed, waiter.Completion.Task)) { cancellationToken.ThrowIfCancellationRequested(); }
        }
        catch (OperationCanceledException)
        {
            RemoveLockWaiter(waiter);
            throw;
        }

        lock (_gate)
        {
            if (waiter.Completion.Task.IsCompleted)
            { return waiter.Completion.Task.GetAwaiter().GetResult(); }
            RemoveLockWaiterLocked(waiter);
            TryGrantNextLockWaiter();
            return null;
        }
    }

    /// <summary>Returns the active lock state without treating queued acquisitions as locks.</summary>
    public McpLockStatus GetLockStatus(Guid callerOwnerId)
    {
        lock (_gate)
        {
            if (_activeLock is not { LockToken: not null } active)
            { return new McpLockStatus(false, false, null, 0); }

            var now = DateTimeOffset.UtcNow;
            var expiresAt = active.ExpiresAt;
            var remaining = Math.Max(0L, (long)Math.Ceiling((expiresAt - now).TotalMilliseconds));
            return new McpLockStatus(true,
                remaining > 0 && active.OwnerId == callerOwnerId, expiresAt, remaining);
        }
    }

    internal McpLockDisplayStatus GetLockDisplayStatus()
    {
        lock (_gate)
        {
            if (_activeLock is not { LockToken: not null } active)
            { return new(false, null, false); }
            return new(true, active.ExpiresAt, active.ExpiresAt <= DateTimeOffset.UtcNow);
        }
    }

    public QueueAdmission RenewLock(Guid ownerId, string lockToken, TimeSpan lease,
        out DateTimeOffset? expiresAt)
    {
        expiresAt = null;
        lock (_gate)
        {
            if (_stopped) { return QueueAdmission.Stopped; }
            if (_activeLock is not { LockToken: not null } active)
            { return QueueAdmission.LockExpired; }
            if (active.OwnerId != ownerId) { return QueueAdmission.Locked; }
            if (active.ExpiresAt <= DateTimeOffset.UtcNow) { return QueueAdmission.LockExpired; }
            if (!TokensEqual(active.LockToken, lockToken)) { return QueueAdmission.LockTokenInvalid; }

            active.ExpiresAt = DateTimeOffset.UtcNow + lease;
            active.IsExpiring = false;
            ScheduleLockExpiry(active.ExpiresAt);
            expiresAt = active.ExpiresAt;
            return QueueAdmission.Accepted;
        }
    }

    public McpLockReleaseResult UnlockLock(Guid ownerId, string lockToken, out bool alreadyReleased)
    {
        alreadyReleased = false;
        lock (_gate)
        {
            var now = DateTimeOffset.UtcNow;
            PruneReleasedLocks(now);
            var validTokenFormat = IsLockTokenFormatValid(lockToken);
            var tokenHash = validTokenFormat ? HashToken(lockToken) : null;
            try
            {
                if (tokenHash is not null && _releasedLocks.Any(entry => entry.OwnerId == ownerId
                    && System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(entry.TokenHash, tokenHash)))
                {
                    alreadyReleased = true;
                    return McpLockReleaseResult.AlreadyReleased;
                }

                if (_stopped) { return McpLockReleaseResult.Busy; }
                if (_activeLock is not { LockToken: not null } active)
                { return McpLockReleaseResult.Expired; }
                if (active.OwnerId != ownerId) { return McpLockReleaseResult.Locked; }
                if (active.ExpiresAt <= now) { return McpLockReleaseResult.Expired; }
                if (tokenHash is null) { return McpLockReleaseResult.TokenInvalid; }
                var expectedHash = HashToken(active.LockToken);
                bool tokenMatches;
                try { tokenMatches = System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(expectedHash, tokenHash); }
                finally { System.Security.Cryptography.CryptographicOperations.ZeroMemory(expectedHash); }
                if (!tokenMatches)
                { return McpLockReleaseResult.TokenInvalid; }
                if (_forceUnlockPending || (_running?.OwnerId == ownerId) || _pending.Any(request => request.OwnerId == ownerId)
                    || _requests.Values.SelectMany(static requests => requests).Any(request =>
                    request.OwnerId == ownerId && request.GetSnapshot().State is RequestState.Queued or RequestState.Running))
                { return McpLockReleaseResult.Busy; }

                _releasedLocks.Add(new ReleasedMcpLock(ownerId, tokenHash.ToArray(), now));
                _lockLeaseTimer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
                _activeLock = null;
                PruneReleasedLocks(now);
                TryGrantNextLockWaiter();
                return McpLockReleaseResult.Released;
            }
            finally
            {
                if (tokenHash is not null)
                { System.Security.Cryptography.CryptographicOperations.ZeroMemory(tokenHash); }
            }
        }
    }

    private void PruneReleasedLocks(DateTimeOffset now)
    {
        for (var index = _releasedLocks.Count - 1; index >= 0; index--)
        {
            if (now - _releasedLocks[index].ReleasedAt < _resultRetention) { continue; }
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(_releasedLocks[index].TokenHash);
            _releasedLocks.RemoveAt(index);
        }
        while (_releasedLocks.Count > MaxRetainedResults)
        {
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(_releasedLocks[0].TokenHash);
            _releasedLocks.RemoveAt(0);
        }
    }

    private void RemoveLockWaiter(McpLockWaiter waiter)
    {
        lock (_gate)
        {
            if (ReferenceEquals(_activeLock, waiter))
            {
                _activeLock = null;
                TryGrantNextLockWaiter();
                return;
            }
            RemoveLockWaiterLocked(waiter);
            TryGrantNextLockWaiter();
        }
    }

    private void RemoveLockWaiterLocked(McpLockWaiter waiter)
    {
        if (_lockWaiters.Count == 0) { return; }
        var remaining = _lockWaiters.Where(candidate => !ReferenceEquals(candidate, waiter)).ToArray();
        _lockWaiters.Clear();
        foreach (var candidate in remaining) { _lockWaiters.Enqueue(candidate); }
    }

    private void TryGrantNextLockWaiter()
    {
        if (_stopped || _emergencyStopped || _activeLock is not null || _pending.Count != 0 || _running is not null
            || !_lockWaiters.TryDequeue(out var waiter)) { return; }
        var tokenBytes = System.Security.Cryptography.RandomNumberGenerator.GetBytes(32);
        string token;
        try { token = Convert.ToBase64String(tokenBytes).TrimEnd('=').Replace('+', '-').Replace('/', '_'); }
        finally { System.Security.Cryptography.CryptographicOperations.ZeroMemory(tokenBytes); }
        _activeLock = waiter;
        waiter.LockToken = token;
        waiter.ExpiresAt = DateTimeOffset.UtcNow + waiter.Lease;
        waiter.IsExpiring = false;
        ScheduleLockExpiry(waiter.ExpiresAt);
        waiter.Completion.TrySetResult(new McpLockGrant(token, waiter.ExpiresAt));
    }

    private void ScheduleLockExpiry(DateTimeOffset expiresAt)
    {
        var delay = expiresAt - DateTimeOffset.UtcNow;
        _lockLeaseTimer.Change(delay > TimeSpan.Zero ? delay : TimeSpan.Zero,
            Timeout.InfiniteTimeSpan);
    }

    private void ExpireActiveLock()
    {
        QueuedRequest? runningToCancel = null;
        lock (_gate)
        {
            if (_stopped || _activeLock is not { LockToken: not null } active
                || active.ExpiresAt > DateTimeOffset.UtcNow) { return; }

            active.IsExpiring = true;
            var retained = new Queue<QueuedRequest>();
            while (_pending.TryDequeue(out var request))
            {
                if (request.OwnerId == active.OwnerId)
                {
                    _available.Wait(0); // Balance the removed request's wake-up.
                    request.Finish(RequestState.Cancelled, error: new ToolError(ErrorCode.CANCELLED));
                }
                else { retained.Enqueue(request); }
            }
            while (retained.TryDequeue(out var request)) { _pending.Enqueue(request); }

            if (_running is { } running && running.OwnerId == active.OwnerId
                && running.TryMarkCancellationRequested(out var newlyRequested) && newlyRequested)
            { runningToCancel = running; }

            if (_running is null && !_forceUnlockPending)
            {
                _activeLock = null;
                TryGrantNextLockWaiter();
            }
        }
        runningToCancel?.SignalCancellation();
    }

    public QueueAdmission CheckExisting(Guid requestId, Guid ownerId, string fingerprint,
        out QueuedRequest? request)
    {
        lock (_gate)
        {
            PruneCompletedRequests(DateTimeOffset.UtcNow);
            request = FindExisting(requestId, ownerId);
            if (request is null) { return QueueAdmission.Accepted; }
            return string.Equals(request.Fingerprint, fingerprint, StringComparison.Ordinal)
                ? QueueAdmission.Duplicate : QueueAdmission.Conflict;
        }
    }

    public IReadOnlyList<OperationBufferEntry> GetBufferSnapshot()
    {
        lock (_gate)
        {
            return _requests.Values.SelectMany(static requests => requests)
                .Select(request => (Request: request, Snapshot: request.GetSnapshot()))
                .OrderByDescending(static item => item.Snapshot.AcceptedAt)
                .Take(MaxRetainedResults)
                .Select(static item => new OperationBufferEntry(item.Snapshot.RequestId,
                    item.Snapshot.State,
                    string.Join(" → ", item.Request.Operations.Select(static operation => operation.Name)),
                    item.Snapshot.AcceptedAt))
                .ToArray();
        }
    }

    public bool TryGet(Guid requestId, Guid ownerId, out ToolResponse? response)
    {
        response = null;
        QueuedRequest? request;
        lock (_gate)
        {
            PruneCompletedRequests(DateTimeOffset.UtcNow);
            if (!_requests.TryGetValue(requestId, out var requests)) { return false; }
            request = requests.LastOrDefault(candidate => candidate.OwnerId == ownerId);
        }
        if (request is null) { return false; }
        response = request.GetResponse();
        return true;
    }

    public bool TryCancel(Guid requestId, Guid ownerId, out ToolResponse? response,
        out bool cancelled, out bool timedOut, out bool cancellationRequested, out bool newlyRequested)
    {
        response = null;
        cancelled = false;
        timedOut = false;
        cancellationRequested = false;
        newlyRequested = false;
        QueuedRequest? cancellationTarget = null;
        lock (_gate)
        {
            PruneCompletedRequests(DateTimeOffset.UtcNow);
            var request = FindExisting(requestId, ownerId);
            if (request is null) { return false; }

            var pending = _pending.ToArray();
            var index = Array.IndexOf(pending, request);
            if (index >= 0)
            {
                _pending.Clear();
                for (var itemIndex = 0; itemIndex < pending.Length; itemIndex++)
                {
                    if (itemIndex != index) { _pending.Enqueue(pending[itemIndex]); }
                }
                _available.Wait(0); // Balance this enqueue's wake-up if the worker has not consumed it.
                if (request.IsQueueExpired(_queueTimeout))
                {
                    request.Finish(RequestState.TimedOut, error: new ToolError(ErrorCode.TIMEOUT));
                    timedOut = true;
                }
                else
                {
                    request.Finish(RequestState.Cancelled, error: new ToolError(ErrorCode.CANCELLED));
                    cancelled = true;
                }
                TryGrantNextLockWaiter();
            }
            else if (ReferenceEquals(_running, request))
            {
                cancellationRequested = request.TryMarkCancellationRequested(out newlyRequested);
                if (newlyRequested) { cancellationTarget = request; }
            }
            response = request.GetResponse();
        }
        cancellationTarget?.SignalCancellation();
        return true;
    }

    internal Task EmergencyStopAsync() => StopLocallyAsync(false);

    internal Task ForceUnlockAsync() => StopLocallyAsync(true);

    private async Task StopLocallyAsync(bool forceUnlock)
    {
        QueuedRequest? runningToCancel = null;
        TaskCompletionSource? cancellationCompletion = null;
        lock (_gate)
        {
            if (_stopped) { return; }
            if (forceUnlock && !_forceUnlockPending)
            {
                _forceUnlockPending = true;
                Kugutsu.Hosting.AuditLog.WriteEvent(_logger, Microsoft.Extensions.Logging.LogLevel.Warning,
                    Kugutsu.Hosting.LogEvent.ForceUnlockRequested, Kugutsu.Hosting.LogOutcome.Started);
            }
            if (!_emergencyStopped)
            {
                Kugutsu.Hosting.AuditLog.WriteEvent(_logger, Microsoft.Extensions.Logging.LogLevel.Warning,
                    Kugutsu.Hosting.LogEvent.EmergencyStopActivated, Kugutsu.Hosting.LogOutcome.Started);
            }
            _emergencyStopped = true;
            while (_pending.TryDequeue(out var request))
            {
                _available.Wait(0);
                request.Finish(RequestState.Cancelled, error: new ToolError(ErrorCode.CANCELLED));
                Kugutsu.Hosting.AuditLog.WriteEvent(_logger, Microsoft.Extensions.Logging.LogLevel.Information,
                    Kugutsu.Hosting.LogEvent.RequestCompleted, Kugutsu.Hosting.LogOutcome.Cancelled,
                    requestId: request.RequestId, error: Kugutsu.Hosting.LogError.Cancelled);
            }
            if (_running is { } running
                && running.TryMarkCancellationRequested(out var newlyRequested) && newlyRequested)
            {
                runningToCancel = running;
                _emergencyCancellationPending = true;
                cancellationCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
                _localCancellationTask = cancellationCompletion.Task;
            }
            TryCompleteForceUnlockLocked();
        }
        if (runningToCancel is null) { return; }
        Kugutsu.Hosting.AuditLog.WriteEvent(_logger, Microsoft.Extensions.Logging.LogLevel.Information,
            Kugutsu.Hosting.LogEvent.RequestCancellationRequested, Kugutsu.Hosting.LogOutcome.Started,
            requestId: runningToCancel.RequestId);
        try { await runningToCancel.SignalCancellationAsync().ConfigureAwait(false); }
        catch (AggregateException)
        {
            Kugutsu.Hosting.AuditLog.WriteEvent(_logger, Microsoft.Extensions.Logging.LogLevel.Error,
                Kugutsu.Hosting.LogEvent.RequestCancellationRequested, Kugutsu.Hosting.LogOutcome.Failed,
                requestId: runningToCancel.RequestId, error: Kugutsu.Hosting.LogError.OperationFailed);
        }
        finally
        {
            lock (_gate)
            {
                _emergencyCancellationPending = false;
                TryCompleteForceUnlockLocked();
            }
            cancellationCompletion?.TrySetResult();
        }
    }

    private void TryCompleteForceUnlockLocked()
    {
        if (_stopped || !_forceUnlockPending || _running is not null || _pending.Count != 0
            || _emergencyCancellationPending) { return; }
        _lockLeaseTimer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        _activeLock = null;
        _forceUnlockPending = false;
        Kugutsu.Hosting.AuditLog.WriteEvent(_logger, Microsoft.Extensions.Logging.LogLevel.Information,
            Kugutsu.Hosting.LogEvent.ForceUnlockCompleted, Kugutsu.Hosting.LogOutcome.Succeeded);
    }

    internal EmergencyStopStatus GetEmergencyStopStatus()
    {
        lock (_gate)
        { return new(_emergencyStopped, (_emergencyStopped || _stopped) && (_running is not null || _emergencyCancellationPending), _forceUnlockPending, _stopped); }
    }

    internal bool TryResumeAfterEmergencyStop()
    {
        lock (_gate)
        {
            if (_stopped || _running is not null || _emergencyCancellationPending || _forceUnlockPending) { return false; }
            if (!_emergencyStopped) { return true; }
            _emergencyStopped = false;
            Kugutsu.Hosting.AuditLog.WriteEvent(_logger, Microsoft.Extensions.Logging.LogLevel.Information,
                Kugutsu.Hosting.LogEvent.OperationAdmissionResumed, Kugutsu.Hosting.LogOutcome.Succeeded);
            TryGrantNextLockWaiter();
            return true;
        }
    }

    private QueuedRequest? FindExisting(Guid requestId, Guid ownerId) =>
        _requests.TryGetValue(requestId, out var requests)
            ? requests.LastOrDefault(candidate => candidate.OwnerId == ownerId) : null;

    private void SweepRetainedResults()
    {
        lock (_gate) { PruneCompletedRequests(DateTimeOffset.UtcNow); }
    }

    private void PruneCompletedRequests(DateTimeOffset now)
    {
        PruneReleasedLocks(now);
        var completed = _requests.Values.SelectMany(static requests => requests)
            .Select(static request => (Request: request, Snapshot: request.GetSnapshot()))
            .Where(static item => item.Snapshot.FinishedAt is not null)
            .OrderBy(static item => item.Snapshot.FinishedAt)
            .ToArray();
        var cutoff = now - _resultRetention;
        foreach (var item in completed.Where(item => item.Snapshot.FinishedAt <= cutoff))
        { RemoveRetainedRequest(item.Request); }

        completed = _requests.Values.SelectMany(static requests => requests)
            .Select(static request => (Request: request, Snapshot: request.GetSnapshot()))
            .Where(static item => item.Snapshot.FinishedAt is not null)
            .OrderBy(static item => item.Snapshot.FinishedAt)
            .ToArray();
        foreach (var item in completed.Take(Math.Max(0, completed.Length - MaxRetainedResults)))
        { RemoveRetainedRequest(item.Request); }
    }

    private void RemoveRetainedRequest(QueuedRequest request)
    {
        if (!_requests.TryGetValue(request.RequestId, out var requests)) { return; }
        requests.Remove(request);
        if (requests.Count == 0) { _requests.Remove(request.RequestId); }
    }

    internal Task ShutdownAsync()
    {
        lock (_gate)
        {
            if (_shutdownTask is not null) { return _shutdownTask; }
            _stopped = true;
            Kugutsu.Hosting.AuditLog.WriteEvent(_logger, Microsoft.Extensions.Logging.LogLevel.Information,
                Kugutsu.Hosting.LogEvent.Shutdown, Kugutsu.Hosting.LogOutcome.Started);
            _resultRetentionTimer.Dispose();
            _lockLeaseTimer.Dispose();
            while (_pending.TryDequeue(out var request))
            { request.Finish(RequestState.Cancelled, error: new ToolError(ErrorCode.CANCELLED)); }
            while (_lockWaiters.TryDequeue(out var waiter)) { waiter.Completion.TrySetResult(null); }
            _shutdownTask = StopAsync(_localCancellationTask);
            return _shutdownTask;
        }
    }

    public ValueTask DisposeAsync() => new(ShutdownAsync());

    private async Task StopAsync(Task localCancellationTask)
    {
        await Task.Yield();
        try { await _stopping.CancelAsync().ConfigureAwait(false); }
        catch (AggregateException)
        {
            Kugutsu.Hosting.AuditLog.WriteEvent(_logger, Microsoft.Extensions.Logging.LogLevel.Error,
                Kugutsu.Hosting.LogEvent.RequestCancellationRequested, Kugutsu.Hosting.LogOutcome.Failed,
                error: Kugutsu.Hosting.LogError.OperationFailed);
        }
        await localCancellationTask.ConfigureAwait(false);
        await _worker.ConfigureAwait(false);
        lock (_gate)
        {
            _running = null;
            _activeLock = null;
            _forceUnlockPending = false;
            foreach (var releasedLock in _releasedLocks)
            { System.Security.Cryptography.CryptographicOperations.ZeroMemory(releasedLock.TokenHash); }
            _releasedLocks.Clear();
        }
        _available.Dispose();
        _stopping.Dispose();
    }

    private async Task RunAsync()
    {
        QueuedRequest? current = null;
        try
        {
            while (true)
            {
                await _available.WaitAsync(_stopping.Token).ConfigureAwait(false);
                QueuedRequest? timedOut = null;
                lock (_gate)
                {
                    if (!_pending.TryDequeue(out current)) { continue; }
                    if (current.IsQueueExpired(_queueTimeout))
                    {
                        timedOut = current;
                        current = null;
                        TryGrantNextLockWaiter();
                    }
                    else
                    {
                        _running = current;
                        current.Start();
                    }
                }
                if (timedOut is not null)
                {
                    timedOut.Finish(RequestState.TimedOut, error: new ToolError(ErrorCode.TIMEOUT));
                    Kugutsu.Hosting.AuditLog.WriteEvent(_logger, Microsoft.Extensions.Logging.LogLevel.Warning,
                        Kugutsu.Hosting.LogEvent.RequestCompleted, Kugutsu.Hosting.LogOutcome.TimedOut,
                        requestId: timedOut.RequestId, error: Kugutsu.Hosting.LogError.Timeout);
                    continue;
                }
                if (current is null) { continue; }
                Kugutsu.Hosting.AuditLog.WriteEvent(_logger, Microsoft.Extensions.Logging.LogLevel.Information,
                    Kugutsu.Hosting.LogEvent.RequestStarted, Kugutsu.Hosting.LogOutcome.Started,
                    requestId: current.RequestId);
                var state = await ExecuteRequestAsync(current).ConfigureAwait(false);
                var (level, outcome, error) = state switch
                {
                    RequestState.Succeeded => (Microsoft.Extensions.Logging.LogLevel.Information, Kugutsu.Hosting.LogOutcome.Succeeded, (Kugutsu.Hosting.LogError?)null),
                    RequestState.Cancelled => (Microsoft.Extensions.Logging.LogLevel.Information, Kugutsu.Hosting.LogOutcome.Cancelled, (Kugutsu.Hosting.LogError?)Kugutsu.Hosting.LogError.Cancelled),
                    RequestState.TimedOut when current.GetResponse().Error?.Code == ErrorCode.REQUEST_TIMEOUT => (Microsoft.Extensions.Logging.LogLevel.Warning, Kugutsu.Hosting.LogOutcome.TimedOut, (Kugutsu.Hosting.LogError?)Kugutsu.Hosting.LogError.RequestTimeout),
                    RequestState.TimedOut => (Microsoft.Extensions.Logging.LogLevel.Warning, Kugutsu.Hosting.LogOutcome.TimedOut, (Kugutsu.Hosting.LogError?)Kugutsu.Hosting.LogError.Timeout),
                    _ => (Microsoft.Extensions.Logging.LogLevel.Error, Kugutsu.Hosting.LogOutcome.Failed, (Kugutsu.Hosting.LogError?)Kugutsu.Hosting.LogError.OperationFailed)
                };
                Kugutsu.Hosting.AuditLog.WriteEvent(_logger, level, Kugutsu.Hosting.LogEvent.RequestCompleted,
                    outcome, requestId: current.RequestId, error: error);
                if (_stopping.IsCancellationRequested) { return; }
                lock (_gate)
                {
                    if (ReferenceEquals(_running, current)) { _running = null; }
                    TryCompleteForceUnlockLocked();
                    if (_activeLock is { IsExpiring: true } && !_forceUnlockPending) { _activeLock = null; }
                    TryGrantNextLockWaiter();
                }
                current = null;
            }
        }
        catch (OperationCanceledException) when (_stopping.IsCancellationRequested)
        {
            current?.Finish(RequestState.Cancelled);
        }
    }

    private async Task<RequestState> ExecuteRequestAsync(QueuedRequest request)
    {
        var requestStarted = System.Diagnostics.Stopwatch.GetTimestamp();
        for (var index = 0; index < request.Operations.Count; index++)
        {
            if (_stopping.IsCancellationRequested)
            { request.Finish(RequestState.Cancelled, error: new ToolError(ErrorCode.CANCELLED)); return RequestState.Cancelled; }
            if (request.IsCancellationRequested)
            { request.Finish(RequestState.Cancelled, error: new ToolError(ErrorCode.CANCELLED)); return RequestState.Cancelled; }
            if (index > 0) { request.StartOperation(index); }
            if (request.HasLockToken
                && CheckRunningLock(request.OwnerId, request) is { } lockFailure)
            {
                request.Finish(RequestState.Failed, error: new ToolError(ToLockError(lockFailure)));
                return RequestState.Failed;
            }
            var started = System.Diagnostics.Stopwatch.GetTimestamp();
            var requestElapsed = System.Diagnostics.Stopwatch.GetElapsedTime(requestStarted, started);
            var remainingRequest = _requestTimeout - requestElapsed;
            if (remainingRequest <= TimeSpan.Zero)
            {
                request.Finish(RequestState.TimedOut, error: new ToolError(ErrorCode.REQUEST_TIMEOUT));
                return RequestState.TimedOut;
            }
            var wholeRequest = request.Operations[index].UseRequestDeadline;
            var operationTimeout = wholeRequest || remainingRequest < _operationTimeout ? remainingRequest : _operationTimeout;
            var requestDeadlineFirst = wholeRequest || remainingRequest <= _operationTimeout;
            using var timeout = new CancellationTokenSource(operationTimeout);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(
                _stopping.Token, request.CancellationToken, timeout.Token);
            try
            {
                var data = await request.Operations[index].Execute(request.OwnerId, linked.Token).ConfigureAwait(false);
                var elapsed = System.Diagnostics.Stopwatch.GetElapsedTime(started);
                var total = System.Diagnostics.Stopwatch.GetElapsedTime(requestStarted);
                if (requestDeadlineFirst && total >= _requestTimeout)
                {
                    request.Finish(RequestState.TimedOut, error: new ToolError(ErrorCode.REQUEST_TIMEOUT));
                    return RequestState.TimedOut;
                }
                if (!wholeRequest && elapsed >= _operationTimeout)
                {
                    request.Finish(RequestState.TimedOut, error: new ToolError(ErrorCode.TIMEOUT));
                    return RequestState.TimedOut;
                }
                if (total >= _requestTimeout)
                {
                    request.Finish(RequestState.TimedOut, error: new ToolError(ErrorCode.REQUEST_TIMEOUT));
                    return RequestState.TimedOut;
                }
                request.CompleteOperation(index, OperationState.Succeeded, data);
            }
            catch (OperationCanceledException) when (_stopping.IsCancellationRequested)
            { request.Finish(RequestState.Cancelled, error: new ToolError(ErrorCode.CANCELLED)); return RequestState.Cancelled; }
            catch (OperationCanceledException) when (request.WasCancellationRequestedBefore(started, operationTimeout))
            { request.Finish(RequestState.Cancelled, error: new ToolError(ErrorCode.CANCELLED)); return RequestState.Cancelled; }
            catch (OperationCanceledException) when (requestDeadlineFirst
                && System.Diagnostics.Stopwatch.GetElapsedTime(requestStarted) >= _requestTimeout)
            { request.Finish(RequestState.TimedOut, error: new ToolError(ErrorCode.REQUEST_TIMEOUT)); return RequestState.TimedOut; }
            catch (OperationCanceledException) when (!wholeRequest && System.Diagnostics.Stopwatch.GetElapsedTime(started) >= _operationTimeout)
            { request.Finish(RequestState.TimedOut, error: new ToolError(ErrorCode.TIMEOUT)); return RequestState.TimedOut; }
            catch (OperationCanceledException) when (System.Diagnostics.Stopwatch.GetElapsedTime(requestStarted) >= _requestTimeout)
            { request.Finish(RequestState.TimedOut, error: new ToolError(ErrorCode.REQUEST_TIMEOUT)); return RequestState.TimedOut; }
            catch (OperationCanceledException) when (request.IsCancellationRequested)
            { request.Finish(RequestState.Cancelled, error: new ToolError(ErrorCode.CANCELLED)); return RequestState.Cancelled; }
            catch (WindowReferenceException exception)
            {
                if (exception.Code is ErrorCode.TIMEOUT or ErrorCode.REQUEST_TIMEOUT)
                {
                    if (_stopping.IsCancellationRequested || request.WasCancellationRequestedBefore(started, operationTimeout))
                    { request.Finish(RequestState.Cancelled, exception.ResultData, new ToolError(ErrorCode.CANCELLED)); return RequestState.Cancelled; }
                    var code = exception.Code == ErrorCode.REQUEST_TIMEOUT || requestDeadlineFirst && System.Diagnostics.Stopwatch.GetElapsedTime(requestStarted) >= _requestTimeout
                        ? ErrorCode.REQUEST_TIMEOUT : ErrorCode.TIMEOUT;
                    request.Finish(RequestState.TimedOut, exception.ResultData, new ToolError(code));
                    return RequestState.TimedOut;
                }
                request.Finish(RequestState.Failed, exception.ResultData, new ToolError(exception.Code));
                return RequestState.Failed;
            }
            catch (Exception)
            { request.Finish(RequestState.Failed, error: new ToolError(ErrorCode.INTERNAL_ERROR)); return RequestState.Failed; }
        }
        request.Finish(RequestState.Succeeded);
        return RequestState.Succeeded;
    }
}
