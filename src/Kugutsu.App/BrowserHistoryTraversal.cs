using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Kugutsu.Contracts;

namespace Kugutsu.App;

internal interface IHistoryBrowserConnection
{
    ValueTask<bool> TraverseAsync(string handle, int delta, CancellationToken token);
}

/// <summary>所有PIDへ限定したBiDi接続で履歴移動と対象コンテキストの完了通知を待つ。</summary>
internal sealed class BrowserHistoryTraversal(Uri endpoint, ApplicationReference browser, ApplicationReference driver,
    Func<CancellationToken, ValueTask> verify)
{
    internal async ValueTask<bool> TraverseAsync(string handle, int delta, CancellationToken token)
    {
        if (delta is not (-1 or 1)) { throw new WindowReferenceException(ErrorCode.INVALID_ARGUMENT); }
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        using var socket = new ClientWebSocket();
        socket.Options.Proxy = null;
        try
        {
            await VerifyAsync(timeout.Token);
            await socket.ConnectAsync(endpoint, timeout.Token);
            await SendAsync(socket, new { id = 1, method = "session.subscribe", @params = new
            {
                events = new[] { "browsingContext.historyUpdated", "browsingContext.fragmentNavigated", "browsingContext.domContentLoaded" },
                contexts = new[] { handle }
            } }, timeout.Token);
            await WaitSubscribeAsync(socket, timeout.Token);
            var startedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            await SendAsync(socket, new { id = 2, method = "browsingContext.traverseHistory", @params = new { context = handle, delta } }, timeout.Token);
            var acknowledged = false;
            var completed = false;
            for (var index = 0; index < 1024; index++)
            {
                var message = await ReadAsync(socket, timeout.Token);
                if (message.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.Number && id.TryGetInt32(out var number) && number == 2)
                {
                    var type = String(message, "type");
                    if (type == "error")
                    {
                        if (String(message, "error") == "no such history entry") { return false; }
                        throw ProtocolError(String(message, "error"));
                    }
                    if (type != "success") { throw Lost(); }
                    acknowledged = true;
                }
                else if (String(message, "type") == "event" && message.TryGetProperty("params", out var parameters)
                    && parameters.ValueKind == JsonValueKind.Object && String(parameters, "context") == handle
                    && parameters.TryGetProperty("timestamp", out var timestamp) && timestamp.ValueKind == JsonValueKind.Number
                    && timestamp.TryGetInt64(out var milliseconds) && milliseconds >= startedAt
                    && String(message, "method") is "browsingContext.historyUpdated" or "browsingContext.fragmentNavigated" or "browsingContext.domContentLoaded")
                { completed = true; }
                if (acknowledged && completed) { await VerifyAsync(timeout.Token); return true; }
            }
            throw Lost();
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { throw new WindowReferenceException(ErrorCode.TIMEOUT); }
        catch (Exception error) when (error is WebSocketException or IOException or JsonException) { throw Lost(); }
        finally { socket.Abort(); }
    }

    private async ValueTask VerifyAsync(CancellationToken token)
    {
        await verify(token);
        try { WebDriverTransport.VerifyListener(endpoint.Port, driver.ProcessId, token); }
        catch (WindowReferenceException error) when (error.Code == ErrorCode.BROWSER_CONNECTION_LOST)
        { WebDriverTransport.VerifyListener(endpoint.Port, browser.ProcessId, token); }
    }

    private async ValueTask SendAsync(ClientWebSocket socket, object command, CancellationToken token)
    {
        await VerifyAsync(token);
        await socket.SendAsync(Encoding.UTF8.GetBytes(ResponseJson.Serialize(command)).AsMemory(), WebSocketMessageType.Text, true, token);
    }

    private async ValueTask<JsonElement> ReadAsync(ClientWebSocket socket, CancellationToken token)
    {
        var buffer = new byte[8192];
        using var memory = new MemoryStream();
        ValueWebSocketReceiveResult received;
        do
        {
            received = await socket.ReceiveAsync(buffer.AsMemory(), token);
            if (received.MessageType != WebSocketMessageType.Text || memory.Length + received.Count > 4 * 1024 * 1024)
            { throw Lost(); }
            memory.Write(buffer, 0, received.Count);
        } while (!received.EndOfMessage);
        using var document = JsonDocument.Parse(memory.ToArray(), new JsonDocumentOptions { MaxDepth = 32 });
        if (document.RootElement.ValueKind != JsonValueKind.Object || WebDriverTransport.HasDuplicateProperties(document.RootElement))
        { throw Lost(); }
        await VerifyAsync(token);
        return document.RootElement.Clone();
    }

    private async ValueTask WaitSubscribeAsync(ClientWebSocket socket, CancellationToken token)
    {
        for (var index = 0; index < 1024; index++)
        {
            var message = await ReadAsync(socket, token);
            if (!message.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.Number || !id.TryGetInt32(out var number) || number != 1) { continue; }
            if (String(message, "type") == "success") { return; }
            if (String(message, "type") == "error") { throw ProtocolError(String(message, "error")); }
            throw Lost();
        }
        throw Lost();
    }

    internal static Uri? Endpoint(JsonElement capabilities, string sessionId)
    {
        var text = String(capabilities, "webSocketUrl");
        if (text is null) { return null; }
        if (text.Length > 8192 || !Uri.TryCreate(text, UriKind.Absolute, out var uri) || uri.Scheme != "ws"
            || uri.Host is not ("localhost" or "127.0.0.1") || uri.Port is < 1 or > 65535
            || uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0
            || uri.AbsolutePath != "/session/" + Uri.EscapeDataString(sessionId))
        { throw Lost(); }
        return new UriBuilder(uri) { Host = "127.0.0.1" }.Uri;
    }

    private static string? String(JsonElement value, string name) => value.TryGetProperty(name, out var item)
        && item.ValueKind == JsonValueKind.String ? item.GetString() : null;
    private static WindowReferenceException ProtocolError(string? error) => new(error switch
    {
        "unknown command" or "unsupported operation" or "invalid argument" => ErrorCode.BROWSER_CAPABILITY_UNSUPPORTED,
        "no such frame" => ErrorCode.BROWSER_TAB_NOT_FOUND, _ => ErrorCode.BROWSER_NAVIGATION_FAILED
    });
    private static WindowReferenceException Lost() => new(ErrorCode.BROWSER_CONNECTION_LOST);
}
