using System.Collections.Frozen;
using System.Text;
using System.Text.Json;
using Kugutsu.Contracts;
using Kugutsu.Hosting;
using Microsoft.Extensions.Logging;

namespace Kugutsu.App;

internal sealed record SignalObservation(bool Matched, JsonElement? Observed, ErrorCode? Error = null);
internal interface ISignalConditionProvider
{
    string Kind { get; }
    ArgumentSchema Schema { get; }
    ValueTask ValidateAsync(JsonElement condition, CancellationToken token);
    ValueTask<IAsyncDisposable> StartAsync(JsonElement condition, Action<SignalObservation> observe,
        CancellationToken startup, CancellationToken lifetime);
}

internal sealed class SignalConditionSchema : ArgumentSchema
{
    internal static SignalConditionSchema Instance { get; } = new();
    public override bool Accepts(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object || Encoding.UTF8.GetByteCount(value.GetRawText()) > 65536
            || !value.TryGetProperty("kind", out var kind) || kind.ValueKind != JsonValueKind.String
            || kind.GetString() is not { Length: > 0 and <= 64 } text || text.Contains('\0')) { return false; }
        var seen = new HashSet<string>(StringComparer.Ordinal);
        return value.EnumerateObject().All(property => seen.Add(property.Name));
    }
}

/// <summary>監視開始と初期評価の通知を確認してから登録結果を返す。</summary>
internal sealed class SignalRegistration : IAsyncDisposable
{
    private sealed class Monitor(CancellationTokenSource lifetime)
    {
        internal CancellationTokenSource Lifetime { get; } = lifetime;
        internal IAsyncDisposable? Resource { get; set; }
        internal CancellationTokenSource Startup { get; } = new();
        internal TaskCompletionSource Ready { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal Task? StopTask;
        internal int InitialObserved;
    }
    private readonly SignalRegistry _signals;
    private readonly OperationRequestQueue _queue;
    private readonly KugutsuOptions _settings;
    private readonly ILogger<SignalRegistration> _logger;
    private readonly FrozenDictionary<string, ISignalConditionProvider> _providers;
    private readonly object _gate = new();
    private readonly Dictionary<string, Monitor> _monitors = new(StringComparer.Ordinal);

    public SignalRegistration(SignalRegistry signals, OperationRequestQueue queue, KugutsuOptions settings,
        ILogger<SignalRegistration> logger, IEnumerable<ISignalConditionProvider> providers)
    {
        _signals = signals; _queue = queue; _settings = settings; _logger = logger;
        _providers = providers.ToFrozenDictionary(provider => provider.Kind, StringComparer.Ordinal);
    }

    internal OperationDefinition Definition() => new("register_signal", ArgumentSchema.Object([
        new("condition", SignalConditionSchema.Instance), new("retentionSeconds", ArgumentSchema.Integer(1, 3600), false),
        new("signalKey", ArgumentSchema.String(64, 1), false)]),
        (_, _) => ValueTask.FromException<JsonElement?>(new WindowReferenceException(ErrorCode.MCP_LOCK_TOKEN_INVALID)),
        args => !args.TryGetProperty("signalKey", out var key) || SignalBatchBindings.ValidKey(key.GetString()),
        async (args, context, token) =>
        {
            if (args.TryGetProperty("signalKey", out var key) && context.Signals is null)
            { throw new WindowReferenceException(ErrorCode.INVALID_ARGUMENT); }
            var result = await RegisterAsync(context.OwnerId,
                context.LockId ?? throw new WindowReferenceException(ErrorCode.MCP_LOCK_TOKEN_INVALID), args.GetProperty("condition"),
                args.TryGetProperty("retentionSeconds", out var seconds) ? seconds.GetInt32() : _settings.SignalRetentionSeconds, token);
            if (key.ValueKind != JsonValueKind.Undefined) { context.Signals!.Bind(key.GetString()!, result.GetProperty("signalId").GetString()!); }
            return result;
        });

    internal OperationDefinition UnregisterDefinition() => new("unregister_signal", ArgumentSchema.Object([
        new("signalId", ArgumentSchema.String(36, 36), false), new("signalKey", ArgumentSchema.String(64, 1), false)]),
        (_, _) => ValueTask.FromException<JsonElement?>(new WindowReferenceException(ErrorCode.MCP_LOCK_TOKEN_INVALID)),
        SignalBatchBindings.ValidReference,
        async (args, context, token) => await UnregisterAsync(context.OwnerId,
            context.LockId ?? throw new WindowReferenceException(ErrorCode.MCP_LOCK_TOKEN_INVALID),
            SignalBatchBindings.Resolve(args, context), token));

    internal async ValueTask<JsonElement> UnregisterAsync(Guid owner, Guid lockId, string id, CancellationToken token)
    {
        var entry = _signals.Resolve(owner, lockId, id, token);
        var previous = _signals.Snapshot(entry);
        token.ThrowIfCancellationRequested();
        _signals.Unregister(entry);
        await StopAsync(id);
        var current = _signals.Snapshot(entry);
        if (current.State != SignalState.Unregistered)
        { throw new WindowReferenceException(current.EndReason ?? ErrorCode.SIGNAL_MONITOR_FAILED); }
        return ResponseJson.ToElement(new { signalId = id, unregistered = true,
            alreadyUnregistered = previous.State == SignalState.Unregistered, reason = "SIGNAL_UNREGISTERED" });
    }

    internal async ValueTask<JsonElement> RegisterAsync(Guid owner, Guid lockId, JsonElement condition, int retention, CancellationToken token)
    {
        using var startup = CancellationTokenSource.CreateLinkedTokenSource(token);
        startup.CancelAfter(TimeSpan.FromSeconds(_settings.OperationTimeoutSeconds));
        try { return await RegisterCoreAsync(owner, lockId, condition, retention, startup.Token); }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        { throw new WindowReferenceException(ErrorCode.TIMEOUT); }
    }

    private async ValueTask<JsonElement> RegisterCoreAsync(Guid owner, Guid lockId, JsonElement condition, int retention, CancellationToken token)
    {
        if (!SignalConditionSchema.Instance.Accepts(condition) || retention is < 1 or > 3600)
        { throw new WindowReferenceException(ErrorCode.INVALID_ARGUMENT); }
        if (!_providers.TryGetValue(condition.GetProperty("kind").GetString()!, out var provider))
        { throw new WindowReferenceException(ErrorCode.SIGNAL_CONDITION_UNSUPPORTED); }
        if (!provider.Schema.Accepts(condition)) { throw new WindowReferenceException(ErrorCode.INVALID_ARGUMENT); }
        _queue.VerifySignalLock(owner, lockId);
        await provider.ValidateAsync(condition, token);
        var entry = _signals.Register(owner, lockId, condition, retention, token);
        var monitor = new Monitor(new CancellationTokenSource());
        lock (_gate) { _monitors.Add(entry.Id, monitor); }
        try
        {
            using var startup = CancellationTokenSource.CreateLinkedTokenSource(token, monitor.Startup.Token);
            try
            {
                monitor.Resource = await provider.StartAsync(condition, observation => Observe(entry, monitor, observation), startup.Token, monitor.Lifetime.Token);
            }
            finally { monitor.Ready.TrySetResult(); }
            if (Volatile.Read(ref monitor.InitialObserved) == 0) { throw new WindowReferenceException(ErrorCode.SIGNAL_MONITOR_FAILED); }
            _queue.VerifySignalLock(owner, lockId);
            var snapshot = _signals.Snapshot(entry);
            if (snapshot.EndReason is { } reason) { throw new WindowReferenceException(reason); }
            if (snapshot.State == SignalState.Signaled) { await StopAsync(entry.Id); }
            return ResponseJson.ToElement(new { signalId = entry.Id, state = snapshot.State.ToString().ToLowerInvariant(),
                expiresAt = snapshot.ExpiresAt, signaledAt = snapshot.SignaledAt, observed = snapshot.Observed });
        }
        catch
        {
            _signals.Unregister(entry);
            await StopAsync(entry.Id);
            throw;
        }
    }

    private void Observe(SignalRegistry.Entry entry, Monitor monitor, SignalObservation observation)
    {
        try
        {
            if (observation.Error is { } error) { _signals.Invalidate(entry, error); monitor.Lifetime.Cancel(); }
            else if (observation.Matched)
            {
                _signals.Signal(entry, observation.Observed ?? ResponseJson.ToElement(new { matched = true }));
                monitor.Lifetime.Cancel();
            }
        }
        catch (WindowReferenceException error)
        {
            _logger.LogDebug("[Signals] Observation rejected: {Code}", error.Code);
            _signals.TryInvalidate(entry, error.Code);
            monitor.Lifetime.Cancel();
        }
        finally { Interlocked.Exchange(ref monitor.InitialObserved, 1); }
    }

    internal ValueTask StopAsync(string id)
    {
        lock (_gate)
        {
            if (!_monitors.TryGetValue(id, out var monitor)) { return ValueTask.CompletedTask; }
            monitor.StopTask ??= Task.Run(() => StopCoreAsync(id, monitor));
            return new ValueTask(monitor.StopTask);
        }
    }

    private async Task StopCoreAsync(string id, Monitor monitor)
    {
        try
        {
            await monitor.Startup.CancelAsync();
            await monitor.Lifetime.CancelAsync();
            await monitor.Ready.Task;
            if (monitor.Resource is not null) { await monitor.Resource.DisposeAsync(); }
        }
        finally
        {
            monitor.Startup.Dispose();
            monitor.Lifetime.Dispose();
            lock (_gate) { _monitors.Remove(id); }
        }
    }

    internal async Task SweepAsync()
    {
        _signals.Sweep();
        string[] ids;
        lock (_gate) { ids = _monitors.Keys.ToArray(); }
        foreach (var id in ids)
        {
            if (_signals.IsPending(id)) { continue; }
            try { await StopAsync(id); }
            catch (Exception) { _logger.LogError("[Signals] Observer cleanup failed."); }
        }
    }

    public async ValueTask DisposeAsync()
    {
        string[] ids;
        lock (_gate) { ids = _monitors.Keys.ToArray(); }
        foreach (var id in ids) { await StopAsync(id); }
    }
}
