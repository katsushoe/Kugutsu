using System.Text.Json;
using Kugutsu.Contracts;
using Microsoft.Extensions.Logging;

namespace Kugutsu.App;

internal sealed partial class DomOperations
{
    private static readonly IReadOnlyDictionary<string, string> DomKeys = CreateDomKeys();
    private static readonly IReadOnlyDictionary<string, string> DomModifiers = new Dictionary<string, string>(StringComparer.Ordinal)
    { ["Ctrl"] = "\ue009", ["Alt"] = "\ue00a", ["Shift"] = "\ue008", ["Win"] = "\ue03d" };

    internal OperationDefinition KeysDefinition() => new("dom.send_keys", ElementSchema([
        new("keySequence", ArgumentSchema.Array(ArgumentSchema.Object([
            new("key", ArgumentSchema.String(16, 1, KeyboardOperations.KeyNames)),
            new("modifiers", ArgumentSchema.Array(ArgumentSchema.String(5, 3, KeyboardOperations.ModifierNames), 4), false)]), 100, 1))]),
        async (args, token) =>
        {
            var keys = args.GetProperty("keySequence").EnumerateArray().ToArray();
            if (keys.Any(item => !DomKeys.ContainsKey(item.GetProperty("key").GetString()!)))
            { throw new WindowReferenceException(ErrorCode.BROWSER_CAPABILITY_UNSUPPORTED); }
            var target = await ResolveAsync(args, token);
            var state = await ReadStateAsync(target.Connection, target.Reference.NativeElement, token);
            if (!state.Visible || !state.Enabled) { throw NotInteractable(); }
            await target.Connection.ExecuteAsync(HttpMethod.Post, "element/" + target.Reference.NativeElement + "/value",
                ResponseJson.ToElement(new { text = string.Empty }), token);
            var attempted = false;
            try
            {
                foreach (var item in keys)
                {
                    target = await ResolveAsync(args, token);
                    state = await ReadStateAsync(target.Connection, target.Reference.NativeElement, token);
                    if (!state.Visible || !state.Enabled) { throw NotInteractable(); }
                    var active = await target.Connection.ExecuteAsync(HttpMethod.Get, "element/active", null, token);
                    if (ReadNative(active) != target.Reference.NativeElement) { throw NotInteractable(); }
                    var strokes = new List<object>();
                    var modifiers = KeyboardOperations.ReadModifiers(item);
                    foreach (var modifier in modifiers) { strokes.Add(new { type = "keyDown", value = DomModifiers[modifier] }); }
                    var key = DomKeys[item.GetProperty("key").GetString()!];
                    strokes.Add(new { type = "keyDown", value = key });
                    strokes.Add(new { type = "keyUp", value = key });
                    foreach (var modifier in modifiers.Reverse()) { strokes.Add(new { type = "keyUp", value = DomModifiers[modifier] }); }
                    attempted = true;
                    await target.Connection.ExecuteAsync(HttpMethod.Post, "actions", ResponseJson.ToElement(new
                    { actions = new[] { new { type = "key", id = "kugutsu_dom_keyboard", actions = strokes } } }), token);
                }
            }
            finally
            {
                if (attempted)
                {
                    using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                    try { await target.Connection.ExecuteAsync(HttpMethod.Delete, "actions", null, cleanup.Token); }
                    catch (Exception error) when (error is WindowReferenceException or OperationCanceledException)
                    {
                        logger.LogWarning("[DomKeys] Keyboard release could not be verified.");
                        throw new WindowReferenceException(ErrorCode.INPUT_RELEASE_FAILED);
                    }
                }
            }
            return ResponseJson.ToElement(new { domElementId = target.Reference.DomElementId, inputSent = true, sentKeys = keys.Length });
        }, args => ValidElementTarget(args) && args.GetProperty("keySequence").EnumerateArray().All(item =>
            KeyboardOperations.ReadModifiers(item).Distinct(StringComparer.Ordinal).Count() == KeyboardOperations.ReadModifiers(item).Length));

    private static IReadOnlyDictionary<string, string> CreateDomKeys()
    {
        var keys = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Enter"] = "\ue007", ["Tab"] = "\ue004", ["Escape"] = "\ue00c", ["Backspace"] = "\ue003", ["Space"] = " ",
            ["Insert"] = "\ue016", ["Delete"] = "\ue017", ["Home"] = "\ue011", ["End"] = "\ue010",
            ["PageUp"] = "\ue00e", ["PageDown"] = "\ue00f", ["Left"] = "\ue012", ["Up"] = "\ue013",
            ["Right"] = "\ue014", ["Down"] = "\ue015"
        };
        for (var letter = 'A'; letter <= 'Z'; letter++) { keys.Add(letter.ToString(), char.ToLowerInvariant(letter).ToString()); }
        for (var digit = '0'; digit <= '9'; digit++) { keys.Add(digit.ToString(), digit.ToString()); }
        for (var index = 1; index <= 12; index++) { keys.Add("F" + index.ToString(System.Globalization.CultureInfo.InvariantCulture), ((char)(0xe030 + index)).ToString()); }
        return keys;
    }
}
