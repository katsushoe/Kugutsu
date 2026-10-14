using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Kugutsu.App;

/// <summary>HTTP requests are not required to expire registrations or release observers.</summary>
internal sealed class SignalLifetimeService(SignalRegistration registration,
    ILogger<SignalLifetimeService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(200));
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try { await registration.SweepAsync(); }
                catch (Exception) { logger.LogError("[Signals] Lifetime cleanup failed."); }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        finally { await registration.DisposeAsync(); }
    }
}
