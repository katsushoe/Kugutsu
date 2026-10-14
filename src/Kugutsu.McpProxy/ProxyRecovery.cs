using System.Net;
using Microsoft.Extensions.Logging;
using Kugutsu.Hosting;

namespace Kugutsu.McpProxy;

/// <summary>Recovers endpoint reachability without replaying any MCP message.</summary>
internal sealed class ProxyRecovery(HttpClient client, Uri endpoint, int timeoutSeconds,
    ILogger logger, CancellationToken lifetime, Func<CancellationToken, Task> resumeSession)
{
    private readonly object _gate = new();
    private Task _recovery = Task.CompletedTask;
    private long _generation;

    /// <summary>Waits for the current recovery cycle before a new message may be sent.</summary>
    public async Task<long> WaitAsync(CancellationToken token)
    {
        while (true)
        {
            Task recovery;
            long generation;
            lock (_gate) { recovery = _recovery; generation = _generation; }
            await recovery.WaitAsync(token);
            lock (_gate)
            {
                if (generation == _generation) { return generation; }
            }
        }
    }

    /// <summary>Coalesces failures from the same generation into one bounded recovery cycle.</summary>
    public Task Begin(long generation)
    {
        lock (_gate)
        {
            if (generation == _generation)
            {
                _generation++;
                _recovery = ReconnectAsync();
            }
            return _recovery;
        }
    }

    /// <summary>Observes the last cycle after lifetime cancellation during relay shutdown.</summary>
    public Task Completion
    {
        get { lock (_gate) { return _recovery; } }
    }

    /// <summary>Identifies failures for which only a reachability probe may be retried.</summary>
    public static bool IsTransient(Exception exception) => exception switch
    {
        HttpRequestException { StatusCode: null } => true,
        HttpRequestException { StatusCode: { } status } => IsTransient(status),
        IOException => true,
        OperationCanceledException => true,
        ModelContextProtocol.McpException and not ModelContextProtocol.McpProtocolException => true,
        _ => false
    };

    private static bool IsTransient(HttpStatusCode status) => status is
        HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests or
        HttpStatusCode.InternalServerError or HttpStatusCode.BadGateway or
        HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout;

    private async Task ReconnectAsync()
    {
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
        budget.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
        var delaySeconds = 1d;
        logger.WriteEvent(LogLevel.Warning, LogEvent.McpDisconnected, LogOutcome.Failed,
            error: LogError.McpTransportFailed);
        try
        {
            while (true)
            {
                // ジッターを加えても待機は60秒以内。操作本文やセッション資格情報は送らない。
                var delay = Math.Min(60d, delaySeconds * (1d + Random.Shared.NextDouble() * 0.2d));
                logger.WriteEvent(LogLevel.Information, LogEvent.McpReconnecting, LogOutcome.Started);
                await Task.Delay(TimeSpan.FromSeconds(delay), budget.Token);
                delaySeconds = Math.Min(60d, delaySeconds * 2d);
                using var request = new HttpRequestMessage(HttpMethod.Options, endpoint);
                using var attempt = CancellationTokenSource.CreateLinkedTokenSource(budget.Token);
                attempt.CancelAfter(TimeSpan.FromSeconds(30));
                try
                {
                    using var response = await client.SendAsync(request,
                        HttpCompletionOption.ResponseHeadersRead, attempt.Token);
                    if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                    { throw new ProxyException(LogError.McpAuthenticationFailed); }
                    if (response.IsSuccessStatusCode || response.StatusCode == HttpStatusCode.MethodNotAllowed)
                    {
                        await resumeSession(attempt.Token);
                        logger.WriteEvent(LogLevel.Information, LogEvent.McpReconnecting, LogOutcome.Succeeded);
                        return;
                    }
                    if (!IsTransient(response.StatusCode))
                    { throw new ProxyException(LogError.ConfigurationInvalid); }
                    logger.WriteEvent(LogLevel.Warning, LogEvent.McpReconnecting, LogOutcome.Failed,
                        error: LogError.McpTransportFailed);
                }
                catch (Exception exception) when (IsTransient(exception) && !budget.IsCancellationRequested)
                {
                    logger.WriteEvent(LogLevel.Warning, LogEvent.McpReconnecting, LogOutcome.Failed,
                        error: LogError.McpTransportFailed);
                }
            }
        }
        catch (OperationCanceledException) when (!lifetime.IsCancellationRequested)
        {
            logger.WriteEvent(LogLevel.Error, LogEvent.McpReconnecting, LogOutcome.TimedOut,
                error: LogError.Timeout);
            throw new ProxyException(LogError.Timeout);
        }
    }
}
