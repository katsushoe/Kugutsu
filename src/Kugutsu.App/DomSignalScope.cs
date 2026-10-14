using System.Text.Json;
using Kugutsu.Contracts;

namespace Kugutsu.App;

/// <summary>Conditions search again only inside the document and native frames captured at registration.</summary>
internal sealed class DomSignalScope(BrowserNavigationOperations navigation, JsonElement condition,
    IReadOnlyList<DomFrameReference> frames, string root)
{
    internal static bool ValidCondition(JsonElement condition) => DomOperations.ValidTarget(condition)
        && condition.GetProperty("selector").GetString() is { } selector && ValidSelector(selector)
        && (!condition.TryGetProperty("framePath", out var path) || path.EnumerateArray().All(item => ValidSelector(item.GetString()!)));
    private static bool ValidSelector(string text) => !string.IsNullOrWhiteSpace(text) && !text.Contains('\0');

    internal static async ValueTask<DomSignalScope> CreateAsync(BrowserNavigationOperations navigation,
        JsonElement condition, CancellationToken token)
    {
        if (!ValidCondition(condition)) { throw new WindowReferenceException(ErrorCode.INVALID_ARGUMENT); }
        var target = await navigation.SelectTabAsync(condition.GetProperty("browserId").GetString()!,
            BrowserSessionRegistry.Parse(condition.GetProperty("browser").GetString()!), condition.GetProperty("tabId").GetString()!, token);
        var connection = target.Browser.Connection;
        var root = await DomOperations.ReadRootAsync(connection, token);
        var frames = new List<DomFrameReference>();
        if (condition.TryGetProperty("framePath", out var path))
        {
            foreach (var selector in path.EnumerateArray())
            {
                var matches = await DomOperations.FindNativeAsync(connection, selector.GetString()!, token);
                if (matches.Count == 0) { throw new WindowReferenceException(ErrorCode.DOM_FRAME_NOT_FOUND); }
                if (matches.Count != 1) { throw new WindowReferenceException(ErrorCode.DOM_ELEMENT_AMBIGUOUS); }
                var tag = await DomOperations.TextAsync(connection, matches[0], "name", token);
                if (tag is not ("iframe" or "frame")) { throw new WindowReferenceException(ErrorCode.DOM_FRAME_NOT_FOUND); }
                frames.Add(new(matches[0], root));
                await connection.ExecuteAsync(HttpMethod.Post, "frame", ResponseJson.ToElement(new { id = DomOperations.Native(matches[0]) }), token);
                root = await DomOperations.ReadRootAsync(connection, token);
            }
        }
        var scope = new DomSignalScope(navigation, condition.Clone(), frames.AsReadOnly(), root);
        await scope.SelectAsync(token);
        return scope;
    }

    internal async ValueTask<IManagedBrowserConnection> SelectAsync(CancellationToken token)
    {
        var target = await navigation.SelectTabAsync(condition.GetProperty("browserId").GetString()!,
            BrowserSessionRegistry.Parse(condition.GetProperty("browser").GetString()!), condition.GetProperty("tabId").GetString()!, token);
        var connection = target.Browser.Connection;
        foreach (var frame in frames)
        {
            await VerifyRootAsync(connection, frame.DocumentRoot, token);
            var tag = await DomOperations.TextAsync(connection, frame.Element, "name", token);
            if (tag is not ("iframe" or "frame")) { throw new WindowReferenceException(ErrorCode.SIGNAL_TARGET_INVALIDATED); }
            await connection.ExecuteAsync(HttpMethod.Post, "frame", ResponseJson.ToElement(new { id = DomOperations.Native(frame.Element) }), token);
        }
        await VerifyRootAsync(connection, root, token);
        return connection;
    }

    private static async ValueTask VerifyRootAsync(IManagedBrowserConnection connection, string expected, CancellationToken token)
    {
        if (await DomOperations.ReadRootAsync(connection, token) != expected)
        { throw new WindowReferenceException(ErrorCode.SIGNAL_TARGET_INVALIDATED); }
        await DomOperations.TextAsync(connection, expected, "name", token);
    }
}
