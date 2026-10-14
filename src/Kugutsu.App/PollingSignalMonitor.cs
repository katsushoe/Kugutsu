using Kugutsu.Contracts;

namespace Kugutsu.App;

/// <summary>Initial observation precedes registration acknowledgement; cancellation is joined on disposal.</summary>
internal sealed class PollingSignalMonitor : IAsyncDisposable
{
    private readonly CancellationTokenSource _stop;
    private readonly Task _worker;

    internal PollingSignalMonitor(int interval, Func<CancellationToken, SignalObservation> read,
        Action<SignalObservation> observe, CancellationToken startup, CancellationToken lifetime)
    {
        startup.ThrowIfCancellationRequested();
        observe(read(startup));
        _stop = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
        _worker = RunAsync(interval, read, observe, _stop.Token);
    }

    private static async Task RunAsync(int interval, Func<CancellationToken, SignalObservation> read,
        Action<SignalObservation> observe, CancellationToken token)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(interval));
        try
        {
            while (await timer.WaitForNextTickAsync(token))
            {
                var observation = read(token);
                observe(observation);
                if (observation.Matched || observation.Error is not null) { return; }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (WindowReferenceException) { observe(new(false, null, ErrorCode.SIGNAL_TARGET_INVALIDATED)); }
        catch (Exception) { observe(new(false, null, ErrorCode.SIGNAL_MONITOR_FAILED)); }
    }

    public async ValueTask DisposeAsync()
    {
        try { await _stop.CancelAsync(); await _worker; }
        finally { _stop.Dispose(); }
    }
}
