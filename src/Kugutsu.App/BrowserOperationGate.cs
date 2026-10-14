namespace Kugutsu.App;

/// <summary>Classic WebDriver selection belongs to an entire operation, not to one HTTP command.</summary>
internal sealed class BrowserOperationGate
{
    private readonly SemaphoreSlim _available = new(1, 1);
    internal async ValueTask<IDisposable> EnterAsync(CancellationToken token)
    {
        await _available.WaitAsync(token);
        return new Lease(_available);
    }
    private sealed class Lease(SemaphoreSlim available) : IDisposable
    {
        private SemaphoreSlim? _available = available;
        public void Dispose() { Interlocked.Exchange(ref _available, null)?.Release(); }
    }
}
