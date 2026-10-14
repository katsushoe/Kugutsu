using System.Diagnostics;
using System.Text.Json;
using Kugutsu.Contracts;

namespace Kugutsu.App;

/// <summary>成立を消費せず待ち、通常timeoutを要求の中断と区別する。</summary>
internal sealed class SignalWaiting(SignalRegistry signals, OperationRequestQueue queue)
{
    internal OperationDefinition Definition() => new("wait_for_signal", ArgumentSchema.Object([
        new("signalId", ArgumentSchema.String(36, 36), false), new("signalKey", ArgumentSchema.String(64, 1), false),
        new("timeoutMs", ArgumentSchema.Integer(0, 300000))]),
        (_, _) => ValueTask.FromException<JsonElement?>(new WindowReferenceException(ErrorCode.MCP_LOCK_TOKEN_INVALID)),
        SignalBatchBindings.ValidReference,
        async (args, context, token) => await WaitAsync(context.OwnerId,
            context.LockId ?? throw new WindowReferenceException(ErrorCode.MCP_LOCK_TOKEN_INVALID),
            SignalBatchBindings.Resolve(args, context), args.GetProperty("timeoutMs").GetInt32(), token));

    internal async ValueTask<JsonElement> WaitAsync(Guid owner, Guid lockId, string id, int timeoutMs, CancellationToken token)
    {
        var entry = signals.Resolve(owner, lockId, id, token);
        var started = Stopwatch.GetTimestamp();
        var deadline = started + (long)(timeoutMs / 1000d * Stopwatch.Frequency);
        var initial = signals.Snapshot(entry);
        if (initial.EndReason is { } initialError) { throw new WindowReferenceException(initialError); }
        while (true)
        {
            token.ThrowIfCancellationRequested();
            queue.VerifySignalLock(owner, lockId);
            if (entry.Completion.Task.IsCompletedSuccessfully)
            {
                var completed = await entry.Completion.Task;
                if (completed.State == SignalState.Signaled && completed.SignaledTimestamp is { } at && at <= deadline)
                { return Signaled(completed); }
                if (completed.EndReason is { } error) { throw new WindowReferenceException(error); }
            }
            var current = signals.Snapshot(entry);
            if (current.State == SignalState.Signaled && current.SignaledTimestamp is { } stamp && stamp <= deadline)
            { return Signaled(current); }
            if (current.EndReason is { } reason) { throw new WindowReferenceException(reason); }
            if (Stopwatch.GetTimestamp() >= deadline)
            {
                return ResponseJson.ToElement(new { status = "timeout", signalId = id,
                    signaledAt = (DateTimeOffset?)null, observed = (JsonElement?)null });
            }
            var remaining = Stopwatch.GetElapsedTime(Stopwatch.GetTimestamp(), deadline);
            if (remaining <= TimeSpan.Zero) { continue; }
            var poll = TimeSpan.FromMilliseconds(Math.Clamp(Math.Ceiling(remaining.TotalMilliseconds), 1, 200));
            await Task.WhenAny(entry.Completion.Task, Task.Delay(poll, token));
        }
    }

    private static JsonElement Signaled(SignalSnapshot snapshot) => ResponseJson.ToElement(new
    { status = "signaled", signalId = snapshot.SignalId, signaledAt = snapshot.SignaledAt, observed = snapshot.Observed });
}
