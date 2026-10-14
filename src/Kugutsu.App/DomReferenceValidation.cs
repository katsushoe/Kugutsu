using System.Text.Json;
using Kugutsu.Contracts;

namespace Kugutsu.App;

internal sealed record ResolvedDomElement(DomElementReference Reference, IManagedBrowserConnection Connection);

internal sealed partial class DomOperations
{
    /// <summary>保存した各文書ルートとフレームIDを再照合し、対象要素を再検索せず復元する。</summary>
    internal async ValueTask<ResolvedDomElement> ResolveAsync(JsonElement args, CancellationToken token)
    {
        var id = args.GetProperty("domElementId").GetString()!;
        var kind = BrowserSessionRegistry.Parse(args.GetProperty("browser").GetString()!);
        var browserId = args.GetProperty("browserId").GetString()!;
        var tabId = args.GetProperty("tabId").GetString()!;
        var reference = elements.Resolve(id, kind, browserId, tabId, token);
        try
        {
            var target = await navigation.SelectTabAsync(browserId, kind, tabId, token);
            var connection = target.Browser.Connection;
            foreach (var frame in reference.Frames)
            {
                await VerifyRootAsync(connection, frame.DocumentRoot, token);
                var tag = await TextAsync(connection, frame.Element, "name", token);
                if (tag is not ("iframe" or "frame")) { throw Stale(); }
                await connection.ExecuteAsync(HttpMethod.Post, "frame", ResponseJson.ToElement(new { id = Native(frame.Element) }), token);
            }
            await VerifyRootAsync(connection, reference.DocumentRoot, token);
            await TextAsync(connection, reference.NativeElement, "name", token);
            return new(reference, connection);
        }
        catch (WindowReferenceException error) when (error.Code is ErrorCode.DOM_ELEMENT_STALE or ErrorCode.DOM_ELEMENT_NOT_FOUND
            or ErrorCode.DOM_FRAME_NOT_FOUND or ErrorCode.BROWSER_TAB_NOT_FOUND or ErrorCode.BROWSER_NOT_FOUND)
        { elements.Invalidate(id); throw Stale(); }
    }

    private static async ValueTask VerifyRootAsync(IManagedBrowserConnection connection, string expected, CancellationToken token)
    {
        if (await ReadRootAsync(connection, token) != expected) { throw Stale(); }
        await TextAsync(connection, expected, "name", token);
    }

    internal static bool ValidElementTarget(JsonElement args) => ValidTarget(args)
        && DomElementRegistry.ValidId(args.GetProperty("domElementId").GetString());
    private static WindowReferenceException Stale() => new(ErrorCode.DOM_ELEMENT_STALE);
}
