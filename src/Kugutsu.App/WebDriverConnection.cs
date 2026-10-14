using System.Text.Json;
using Kugutsu.Contracts;

namespace Kugutsu.App;

/// <summary>固定したW3Cセッションと管理プロセスへコマンドを限定する。</summary>
internal sealed class WebDriverConnection(BrowserCapabilities capabilities, ApplicationReference browser,
    ApplicationReference driver, string sessionId, WebDriverTransport transport,
    ManagedProcessTree tree, Uri? bidiEndpoint = null) : IManagedBrowserConnection, IHistoryBrowserConnection
{
    public BrowserCapabilities Capabilities { get; } = capabilities;
    public ApplicationReference Application { get; } = browser;
    public ApplicationReference Driver { get; } = driver;

    public async ValueTask<bool> TraverseAsync(string handle, int delta, CancellationToken token)
    {
        if (bidiEndpoint is null) { throw new WindowReferenceException(ErrorCode.BROWSER_CAPABILITY_UNSUPPORTED); }
        return await new BrowserHistoryTraversal(bidiEndpoint, Application, Driver, VerifyConnectionAsync).TraverseAsync(handle, delta, token);
    }

    public ValueTask VerifyConnectionAsync(CancellationToken token)
    {
        try { tree.VerifyRelationship(Driver, Application, token); }
        catch (WindowReferenceException error) when (error.Code == ErrorCode.SESSION_TARGET_UNRESOLVED)
        { throw new WindowReferenceException(ErrorCode.BROWSER_CONNECTION_LOST); }
        transport.Verify(token);
        return ValueTask.CompletedTask;
    }

    public async ValueTask<IReadOnlyList<string>> GetWindowHandlesAsync(CancellationToken token)
    {
        var value = await ExecuteAsync(HttpMethod.Get, "window/handles", null, token);
        if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() > 256
            || value.EnumerateArray().Any(item => item.ValueKind != JsonValueKind.String))
        { throw new WindowReferenceException(ErrorCode.BROWSER_CONNECTION_LOST); }
        return value.EnumerateArray().Select(item => item.GetString()!).ToArray();
    }

    public async ValueTask<JsonElement> ExecuteAsync(HttpMethod method, string command, JsonElement? body, CancellationToken token)
    {
        await VerifyConnectionAsync(token);
        var path = new List<string> { "session", sessionId };
        if (command.Length != 0) { path.AddRange(command.Split('/')); }
        return await transport.SendAsync(method, path, body, token);
    }

    public void Dispose() => transport.Dispose();
}
