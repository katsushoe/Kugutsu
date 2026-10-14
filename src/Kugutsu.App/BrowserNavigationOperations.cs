using System.Text.Json;
using Kugutsu.Contracts;
using Microsoft.Extensions.Logging;

namespace Kugutsu.App;

/// <summary>種類と管理タブを照合し、W3Cの文書遷移完了応答を待つ。</summary>
internal sealed class BrowserNavigationOperations(BrowserSessionRegistry sessions, ILogger<BrowserNavigationOperations> logger,
    DomElementRegistry elements)
{
    internal OperationDefinition BackDefinition() => HistoryDefinition("browser.back", -1);
    internal OperationDefinition ForwardDefinition() => HistoryDefinition("browser.forward", 1);

    private OperationDefinition HistoryDefinition(string name, int delta) => new(name, ArgumentSchema.Object([
        new("browser", ArgumentSchema.String(7, 4, ["chrome", "edge", "firefox"])),
        new("browserId", ArgumentSchema.String(34, 34)), new("tabId", ArgumentSchema.String(35, 35))]),
        async (args, token) =>
        {
            var browserId = args.GetProperty("browserId").GetString()!;
            var tabId = args.GetProperty("tabId").GetString()!;
            var kind = BrowserSessionRegistry.Parse(args.GetProperty("browser").GetString()!);
            var target = await SelectTabAsync(browserId, kind, tabId, token);
            try
            {
                if (target.Browser.Connection is not IHistoryBrowserConnection history)
                { throw new WindowReferenceException(ErrorCode.BROWSER_CAPABILITY_UNSUPPORTED); }
                var moved = await history.TraverseAsync(target.Tab.NativeHandle, delta, token);
                if (moved)
                {
                    elements.InvalidateTab(browserId, tabId);
                    await WaitDocumentAsync(target.Browser.Connection, token);
                }
                var current = await ReadUrlAsync(target.Browser.Connection, token);
                return ResponseJson.ToElement(new { browser = BrowserSessionRegistry.Name(kind), browserId, tabId,
                    moved, url = current, navigationCompleted = true });
            }
            catch (WindowReferenceException error)
            {
                var observed = await ObserveUrlAsync(target.Browser.Connection, token);
                throw new WindowReferenceException(error.Code, ResponseJson.ToElement(new
                    { browserId, tabId, currentUrl = observed, currentUrlObserved = observed is not null, navigationCompleted = false }));
            }
        }, args => BrowserSessionRegistry.ValidBrowserId(args.GetProperty("browserId").GetString())
            && BrowserSessionRegistry.ValidTabId(args.GetProperty("tabId").GetString()));

    private static async ValueTask WaitDocumentAsync(IManagedBrowserConnection connection, CancellationToken token)
    {
        while (true)
        {
            var state = await connection.ExecuteAsync(HttpMethod.Post, "execute/sync",
                ResponseJson.ToElement(new { script = "return document.readyState;", args = Array.Empty<object>() }), token);
            if (state.ValueKind != JsonValueKind.String) { throw new WindowReferenceException(ErrorCode.BROWSER_CONNECTION_LOST); }
            if (state.GetString() is "interactive" or "complete") { return; }
            await Task.Delay(50, token);
        }
    }

    internal OperationDefinition NavigateDefinition() => new("browser.navigate", ArgumentSchema.Object([
        new("browser", ArgumentSchema.String(7, 4, ["chrome", "edge", "firefox"])),
        new("browserId", ArgumentSchema.String(34, 34)), new("tabId", ArgumentSchema.String(35, 35)),
        new("url", ArgumentSchema.String(8192, 1))]),
        async (args, token) =>
        {
            var browserId = args.GetProperty("browserId").GetString()!;
            var tabId = args.GetProperty("tabId").GetString()!;
            var kind = BrowserSessionRegistry.Parse(args.GetProperty("browser").GetString()!);
            var target = await SelectTabAsync(browserId, kind, tabId, token);
            try
            {
                var previous = await ReadUrlAsync(target.Browser.Connection, token);
                elements.InvalidateTab(browserId, tabId);
                await target.Browser.Connection.ExecuteAsync(HttpMethod.Post, "url",
                    ResponseJson.ToElement(new { url = args.GetProperty("url").GetString()! }), token);
                var current = await ReadUrlAsync(target.Browser.Connection, token);
                return ResponseJson.ToElement(new { browser = BrowserSessionRegistry.Name(kind), browserId, tabId,
                    url = current, navigationCompleted = true, pageLoadStrategy = "eager", urlChanged = previous != current });
            }
            catch (WindowReferenceException error)
            {
                var observed = await ObserveUrlAsync(target.Browser.Connection, token);
                throw new WindowReferenceException(error.Code, ResponseJson.ToElement(new
                    { browserId, tabId, currentUrl = observed, currentUrlObserved = observed is not null, navigationCompleted = false }));
            }
        }, args => BrowserSessionRegistry.ValidBrowserId(args.GetProperty("browserId").GetString())
            && BrowserSessionRegistry.ValidTabId(args.GetProperty("tabId").GetString())
            && BrowserSessionRegistry.ValidUrl(args.GetProperty("url").GetString()));

    internal async ValueTask<ResolvedBrowserTab> SelectTabAsync(string browserId, BrowserKind kind, string tabId, CancellationToken token)
    {
        var target = await sessions.ResolveTabAsync(browserId, kind, tabId, token);
        var connection = target.Browser.Connection;
        await connection.ExecuteAsync(HttpMethod.Post, "window", ResponseJson.ToElement(new { handle = target.Tab.NativeHandle }), token);
        var current = await connection.ExecuteAsync(HttpMethod.Get, "window", null, token);
        if (current.ValueKind != JsonValueKind.String || current.GetString() != target.Tab.NativeHandle)
        { throw new WindowReferenceException(ErrorCode.BROWSER_TAB_NOT_FOUND); }
        await connection.ExecuteAsync(HttpMethod.Post, "frame", ResponseJson.ToElement(new { id = (object?)null }), token);
        return target;
    }

    internal static async ValueTask<string> ReadUrlAsync(IManagedBrowserConnection connection, CancellationToken token)
    {
        var value = await connection.ExecuteAsync(HttpMethod.Get, "url", null, token);
        if (value.ValueKind != JsonValueKind.String || value.GetString() is not { Length: <= 8192 } url || url.Any(char.IsControl))
        { throw new WindowReferenceException(ErrorCode.BROWSER_CONNECTION_LOST); }
        return url;
    }

    private async ValueTask<string?> ObserveUrlAsync(IManagedBrowserConnection connection, CancellationToken token)
    {
        if (token.IsCancellationRequested) { return null; }
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(1));
        try { return await ReadUrlAsync(connection, timeout.Token); }
        catch (WindowReferenceException) { logger.LogDebug("[BrowserNavigation] Current URL unavailable."); return null; }
        catch (OperationCanceledException) { logger.LogDebug("[BrowserNavigation] Current URL observation cancelled."); return null; }
    }
}
