using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Kugutsu.Hosting;

namespace Kugutsu.McpProxy;

/// <summary>A relay failure carrying only a fixed diagnostic code.</summary>
internal sealed class ProxyException(LogError reason) : Exception("MCP relay failed.")
{
    public LogError Reason { get; } = reason;
}

/// <summary>Relays JSON-RPC without creating a second MCP session or rewriting request IDs.</summary>
internal static class ProxyRelay
{
    private const int MaxConcurrentSends = 16;

    /// <summary>Runs until stdin closes, cancellation is requested, or recovery cannot continue.</summary>
    public static async Task RunAsync(KugutsuOptions settings, BearerCredential credential,
        ILoggerFactory loggerFactory, ILogger logger, CancellationToken cancellationToken)
    {
        var services = new ServiceCollection();
        services.AddHttpClient("Kugutsu", client =>
        {
            client.Timeout = TimeSpan.FromSeconds(settings.RequestTimeoutSeconds);
            client.DefaultRequestHeaders.Authorization = credential.CreateAuthorizationHeader();
        }).ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
        {
            UseProxy = false, AllowAutoRedirect = false, UseCookies = false,
            ConnectTimeout = TimeSpan.FromSeconds(30)
        });
        using var provider = services.BuildServiceProvider();
        using var httpClient = provider.GetRequiredService<IHttpClientFactory>().CreateClient("Kugutsu");
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var session = new ProxySession(httpClient, new Uri(settings.ServerUrl!, UriKind.Absolute),
            settings.RequestTimeoutSeconds, lifetime.Token);
        await using var connector = new HttpClientTransport(new HttpClientTransportOptions
        {
            Endpoint = new Uri(settings.ServerUrl!, UriKind.Absolute), Name = "Kugutsu HTTP",
            TransportMode = HttpTransportMode.StreamableHttp, ConnectionTimeout = TimeSpan.FromSeconds(30),
            EnableStandaloneGetStream = false, MaxReconnectionAttempts = 0, OwnsSession = false,
            AdditionalHeaders = session.TransportHeaders
        }, httpClient, loggerFactory);
        await using var upstream = await connector.ConnectAsync(cancellationToken);
        var endOfInput = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var stdio = new StreamServerTransport(
            new BoundedLineStream(Console.OpenStandardInput(), settings.MaxRequestBytes,
                () => endOfInput.TrySetResult()),
            Console.OpenStandardOutput(), "Kugutsu proxy", loggerFactory);
        var recovery = new ProxyRecovery(httpClient, new Uri(settings.ServerUrl!, UriKind.Absolute),
            settings.RequestTimeoutSeconds, logger, lifetime.Token, session.ResumeAsync);
        var requests = new System.Collections.Concurrent.ConcurrentDictionary<RequestId, byte>();
        var input = ForwardInputAsync(stdio, upstream, lifetime, settings.RequestTimeoutSeconds,
            logger, recovery, requests, session);
        var output = ForwardOutputAsync(upstream, stdio, settings.RequestTimeoutSeconds,
            lifetime.Token, requests);
        try
        {
            var first = await Task.WhenAny(input, output, endOfInput.Task);
            await first;
            if (first == output && !lifetime.IsCancellationRequested)
            { throw new ProxyException(LogError.McpTransportFailed); }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        finally
        {
            await lifetime.CancelAsync();
            try { await Task.WhenAll(input, output, recovery.Completion, session.Completion); }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
            catch (Exception exception)
            { throw Classify(exception); }
        }
        logger.WriteEvent(LogLevel.Information, LogEvent.McpDisconnected, LogOutcome.Succeeded);
    }

    private static async Task ForwardInputAsync(ITransport input, ITransport output,
        CancellationTokenSource lifetime, int timeoutSeconds, ILogger logger, ProxyRecovery recovery,
        System.Collections.Concurrent.ConcurrentDictionary<RequestId, byte> requests, ProxySession session)
    {
        using var slots = new SemaphoreSlim(MaxConcurrentSends);
        var pending = new List<Task>();
        var connected = 0;
        async Task SendAsync(JsonRpcMessage message)
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
            deadline.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
            var sending = false;
            long generation = 0;
            try
            {
                if (message is JsonRpcRequest request && !requests.TryAdd(request.Id, 0))
                { throw new ProxyException(LogError.McpProtocolFailed); }
                // 発行の待機失敗は通常の接続回復へ読み替えず、共有初期化を終了まで観測する。
                await session.EnsureAsync(deadline.Token);
                try
                {
                    generation = await recovery.WaitAsync(deadline.Token);
                    sending = true;
                    await output.SendMessageAsync(message, deadline.Token);
                    if (Interlocked.Exchange(ref connected, 1) == 0)
                    { logger.WriteEvent(LogLevel.Information, LogEvent.McpConnected, LogOutcome.Succeeded); }
                }
                catch (McpProtocolException exception) when (message is JsonRpcRequest failed)
                {
                    await ReplyErrorAsync(input, failed, requests, (int)exception.ErrorCode,
                        "MCP request failed.", null, timeoutSeconds, lifetime.Token);
                    logger.WriteEvent(LogLevel.Warning, LogEvent.McpProtocolError, LogOutcome.Failed,
                        error: LogError.McpProtocolFailed);
                }
                catch (Exception exception) when (ProxyRecovery.IsTransient(exception)
                    && !lifetime.IsCancellationRequested)
                {
                    var reconnect = sending ? recovery.Begin(generation) : Task.CompletedTask;
                    logger.WriteEvent(LogLevel.Warning, LogEvent.McpMessageFailed, LogOutcome.Failed,
                        error: exception is OperationCanceledException ? LogError.Timeout : LogError.McpTransportFailed);
                    if (message is JsonRpcRequest failed)
                    {
                        await ReplyErrorAsync(input, failed, requests, sending ? -32001 : -32002,
                            sending ? "MCP transport failed; request outcome is unknown. Do not replay automatically."
                                : "MCP recovery wait timed out; request was not sent.",
                            new System.Text.Json.Nodes.JsonObject
                            { ["outcome"] = sending ? "unknown" : "not_sent", ["replayed"] = false },
                            timeoutSeconds, lifetime.Token);
                    }
                    // EOFとCtrl+Cは回復周期自身へ伝播する。失敗した通知も再送しない。
                    await reconnect;
                }
            }
            catch (OperationCanceledException) when (!lifetime.IsCancellationRequested)
            { await lifetime.CancelAsync(); throw new ProxyException(LogError.Timeout); }
            catch (Exception exception) when (exception is not OperationCanceledException)
            { await lifetime.CancelAsync(); throw Classify(exception); }
            finally { slots.Release(); }
        }
        try
        {
            await foreach (var message in input.MessageReader.ReadAllAsync(lifetime.Token))
            {
                await slots.WaitAsync(lifetime.Token);
                foreach (var completed in pending.Where(task => task.IsCompleted).ToArray())
                { pending.Remove(completed); await completed; }
                pending.Add(SendAsync(message));
            }
        }
        finally
        {
            await lifetime.CancelAsync();
            await Task.WhenAll(pending);
        }
    }

    private static async Task ForwardOutputAsync(ITransport input, ITransport output, int timeoutSeconds,
        CancellationToken token, System.Collections.Concurrent.ConcurrentDictionary<RequestId, byte> requests)
    {
        await foreach (var message in input.MessageReader.ReadAllAsync(token))
        {
            // ローカル障害通知と遅れて届く応答を二重に返さない。
            if (message is JsonRpcResponse or JsonRpcError && message is JsonRpcMessageWithId reply
                && !requests.TryRemove(reply.Id, out _)) { continue; }
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
            await output.SendMessageAsync(message, deadline.Token);
        }
    }

    private static async Task ReplyErrorAsync(ITransport input, JsonRpcRequest request,
        System.Collections.Concurrent.ConcurrentDictionary<RequestId, byte> requests, int code,
        string message, System.Text.Json.Nodes.JsonNode? data, int timeoutSeconds, CancellationToken token)
    {
        if (!requests.TryRemove(request.Id, out _)) { return; }
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
        await input.SendMessageAsync(new JsonRpcError
        {
            Id = request.Id,
            Error = new JsonRpcErrorDetail { Code = code, Message = message, Data = data }
        }, deadline.Token);
    }

    private static ProxyException Classify(Exception exception) => exception switch
    {
        ProxyException failure => failure,
        HttpRequestException { StatusCode: HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden }
            => new(LogError.McpAuthenticationFailed),
        HttpRequestException { StatusCode: { } status } when (int)status is >= 300 and < 500
            => new(LogError.ConfigurationInvalid),
        OperationCanceledException => new(LogError.Timeout),
        McpException => new(LogError.McpProtocolFailed),
        InvalidDataException => new(LogError.ConfigurationInvalid),
        _ => new(LogError.McpTransportFailed)
    };
}
