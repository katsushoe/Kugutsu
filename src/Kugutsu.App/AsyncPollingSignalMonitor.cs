using Kugutsu.Contracts;

namespace Kugutsu.App;

internal sealed class AsyncPollingSignalMonitor : IAsyncDisposable
{
    private readonly CancellationTokenSource _stop;
    private readonly Task _worker;
    private AsyncPollingSignalMonitor(int interval, Func<CancellationToken, ValueTask<SignalObservation>> read,
        Action<SignalObservation> observe, CancellationToken lifetime)
    {
        _stop = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
        _worker = RunAsync(interval, read, observe, _stop.Token);
    }

    internal static async ValueTask<IAsyncDisposable> StartAsync(int interval,
        Func<CancellationToken, ValueTask<SignalObservation>> read, Action<SignalObservation> observe,
        CancellationToken startup, CancellationToken lifetime)
    {
        startup.ThrowIfCancellationRequested();
        observe(await read(startup));
        return new AsyncPollingSignalMonitor(interval, read, observe, lifetime);
    }

    private static async Task RunAsync(int interval, Func<CancellationToken, ValueTask<SignalObservation>> read,
        Action<SignalObservation> observe, CancellationToken token)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(interval));
        try
        {
            while (await timer.WaitForNextTickAsync(token))
            {
                var observation = await read(token);
                observe(observation);
                if (observation.Matched || observation.Error is not null) { return; }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (WindowReferenceException error)
        {
            var reason = error.Code is ErrorCode.UI_ELEMENT_STALE or ErrorCode.UI_ELEMENT_NOT_FOUND
                or ErrorCode.UIA_ELEMENT_STALE or ErrorCode.UIA_ELEMENT_NOT_FOUND or ErrorCode.WINDOW_NOT_FOUND
                or ErrorCode.DOM_ELEMENT_STALE or ErrorCode.DOM_ELEMENT_NOT_FOUND or ErrorCode.DOM_FRAME_NOT_FOUND
                or ErrorCode.BROWSER_NOT_FOUND or ErrorCode.BROWSER_TAB_NOT_FOUND or ErrorCode.SIGNAL_TARGET_INVALIDATED
                ? ErrorCode.SIGNAL_TARGET_INVALIDATED : ErrorCode.SIGNAL_MONITOR_FAILED;
            observe(new(false, null, reason));
        }
        catch (Exception) { observe(new(false, null, ErrorCode.SIGNAL_MONITOR_FAILED)); }
    }

    public async ValueTask DisposeAsync()
    {
        try { await _stop.CancelAsync(); await _worker; }
        finally { _stop.Dispose(); }
    }
}
