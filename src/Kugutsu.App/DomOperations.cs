using System.Text.Json;
using Kugutsu.Contracts;
using Microsoft.Extensions.Logging;

namespace Kugutsu.App;

internal sealed record DomElementState(string TagName, string? InputType, bool Visible, bool Enabled);

/// <summary>CSS探索とフレーム選択をWebDriverへ渡し、本文や入力値を取得せず参照を発行する。</summary>
internal sealed partial class DomOperations(BrowserNavigationOperations navigation, DomElementRegistry elements, ILogger<DomOperations> logger)
{
    internal const string ElementKey = "element-6066-11e4-a52e-4f735466cecf";

    internal OperationDefinition FindDefinition() => new("dom.find", ArgumentSchema.Object([
        new("browser", ArgumentSchema.String(7, 4, ["chrome", "edge", "firefox"])),
        new("browserId", ArgumentSchema.String(34, 34)), new("tabId", ArgumentSchema.String(35, 35)),
        new("selector", ArgumentSchema.String(4096, 1)),
        new("framePath", ArgumentSchema.Array(ArgumentSchema.String(4096, 1), 16), false),
        new("maxResults", ArgumentSchema.Integer(1, 1000), false)]),
        async (args, token) =>
        {
            var kind = BrowserSessionRegistry.Parse(args.GetProperty("browser").GetString()!);
            var browserId = args.GetProperty("browserId").GetString()!;
            var tabId = args.GetProperty("tabId").GetString()!;
            var target = await navigation.SelectTabAsync(browserId, kind, tabId, token);
            var connection = target.Browser.Connection;
            var frames = new List<DomFrameReference>();
            var root = await ReadRootAsync(connection, token);
            if (args.TryGetProperty("framePath", out var path))
            {
                foreach (var selector in path.EnumerateArray())
                {
                    var matches = await FindNativeAsync(connection, selector.GetString()!, token);
                    if (matches.Count == 0) { throw new WindowReferenceException(ErrorCode.DOM_FRAME_NOT_FOUND); }
                    if (matches.Count != 1) { throw new WindowReferenceException(ErrorCode.DOM_ELEMENT_AMBIGUOUS); }
                    var tag = await TextAsync(connection, matches[0], "name", token);
                    if (tag is not ("iframe" or "frame")) { throw new WindowReferenceException(ErrorCode.DOM_FRAME_NOT_FOUND); }
                    frames.Add(new(matches[0], root));
                    await connection.ExecuteAsync(HttpMethod.Post, "frame", ResponseJson.ToElement(new { id = Native(matches[0]) }), token);
                    root = await ReadRootAsync(connection, token);
                }
            }
            var found = await FindNativeAsync(connection, args.GetProperty("selector").GetString()!, token);
            var maximum = args.TryGetProperty("maxResults", out var max) ? max.GetInt32() : 100;
            var selected = found.Take(maximum).ToArray();
            var states = new List<DomElementState>();
            foreach (var item in selected) { states.Add(await ReadStateAsync(connection, item, token)); }
            // 候補の状態取得中に文書が入れ替わった場合は参照を発行しない。
            if (await ReadRootAsync(connection, token) != root) { throw new WindowReferenceException(ErrorCode.DOM_ELEMENT_STALE); }
            var registered = elements.Register(kind, browserId, tabId, frames, root, selected, token);
            return ResponseJson.ToElement(new { browser = BrowserSessionRegistry.Name(kind), browserId, tabId,
                elements = registered.Select((item, index) => new { domElementId = item.DomElementId,
                    tagName = states[index].TagName, inputType = states[index].InputType,
                    visible = states[index].Visible, enabled = states[index].Enabled }).ToArray(),
                matchedCount = found.Count, truncated = found.Count > selected.Length });
        }, args => ValidTarget(args) && ValidSelector(args.GetProperty("selector").GetString()!)
            && (!args.TryGetProperty("framePath", out var frames) || frames.EnumerateArray().All(item => ValidSelector(item.GetString()!))));

    internal static bool ValidTarget(JsonElement args) => BrowserSessionRegistry.ValidBrowserId(args.GetProperty("browserId").GetString())
        && BrowserSessionRegistry.ValidTabId(args.GetProperty("tabId").GetString());
    private static bool ValidSelector(string selector) => !string.IsNullOrWhiteSpace(selector) && !selector.Contains('\0');
    internal static object Native(string id) => new Dictionary<string, string> { [ElementKey] = id };

    internal static async ValueTask<IReadOnlyList<string>> FindNativeAsync(IManagedBrowserConnection connection, string selector, CancellationToken token)
    {
        var value = await connection.ExecuteAsync(HttpMethod.Post, "elements", ResponseJson.ToElement(new { @using = "css selector", value = selector }), token);
        if (value.ValueKind != JsonValueKind.Array) { throw Lost(); }
        if (value.GetArrayLength() > 10000) { throw new WindowReferenceException(ErrorCode.DOM_SEARCH_LIMIT_EXCEEDED); }
        var result = value.EnumerateArray().Select(ReadNative).ToArray();
        if (result.Distinct(StringComparer.Ordinal).Count() != result.Length) { throw Lost(); }
        return result;
    }

    private static string ReadNative(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty(ElementKey, out var id) || id.ValueKind != JsonValueKind.String
            || id.GetString() is not { Length: > 0 and <= 1024 } text || text.Any(char.IsControl) || text.Contains('/')) { throw Lost(); }
        return text;
    }

    internal static async ValueTask<string> ReadRootAsync(IManagedBrowserConnection connection, CancellationToken token)
    {
        var roots = await FindNativeAsync(connection, ":root", token);
        if (roots.Count != 1) { throw new WindowReferenceException(ErrorCode.DOM_ELEMENT_STALE); }
        return roots[0];
    }

    internal static async ValueTask<DomElementState> ReadStateAsync(IManagedBrowserConnection connection, string id, CancellationToken token)
    {
        var tag = await TextAsync(connection, id, "name", token);
        string? type = null;
        if (tag == "input")
        {
            var value = await ElementAsync(connection, id, "property/type", token);
            if (value.ValueKind != JsonValueKind.String || value.GetString() is not { Length: <= 128 } text) { throw Lost(); }
            type = text;
        }
        var visible = await BooleanAsync(connection, id, "displayed", token);
        var enabled = await BooleanAsync(connection, id, "enabled", token);
        return new(tag, type, visible, enabled);
    }

    internal static ValueTask<JsonElement> ElementAsync(IManagedBrowserConnection connection, string id, string command, CancellationToken token) =>
        connection.ExecuteAsync(HttpMethod.Get, "element/" + id + "/" + command, null, token);
    internal static async ValueTask<string> TextAsync(IManagedBrowserConnection connection, string id, string command, CancellationToken token)
    {
        var result = await ElementAsync(connection, id, command, token);
        if (result.ValueKind != JsonValueKind.String || result.GetString() is not { Length: <= 4096 } text) { throw Lost(); }
        return text;
    }
    internal static async ValueTask<bool> BooleanAsync(IManagedBrowserConnection connection, string id, string command, CancellationToken token)
    {
        var result = await ElementAsync(connection, id, command, token);
        if (result.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) { throw Lost(); }
        return result.GetBoolean();
    }
    private static WindowReferenceException Lost() => new(ErrorCode.BROWSER_CONNECTION_LOST);
}
