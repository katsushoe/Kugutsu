using System.Text.Json;
using Kugutsu.Contracts;

namespace Kugutsu.App;

internal sealed partial class DomOperations
{
    internal OperationDefinition FillDefinition() => new("dom.fill", ElementSchema([new("text", ArgumentSchema.String(65536))]),
        async (args, token) =>
        {
            var target = await ResolveAsync(args, token);
            var text = args.GetProperty("text").GetString()!;
            var state = await ReadStateAsync(target.Connection, target.Reference.NativeElement, token);
            if (!state.Visible || !state.Enabled) { throw NotInteractable(); }
            var input = state.TagName == "input";
            var textarea = state.TagName == "textarea";
            if (input && state.InputType is not ("text" or "search" or "email" or "url" or "tel" or "password" or "number"
                or "date" or "datetime-local" or "month" or "time" or "week")) { throw NotInteractable(); }
            if (input && text.IndexOfAny(['\r', '\n']) >= 0) { throw new WindowReferenceException(ErrorCode.INVALID_ARGUMENT); }
            if (input || textarea)
            {
                if (await BooleanAsync(target.Connection, target.Reference.NativeElement, "property/readOnly", token)) { throw NotInteractable(); }
            }
            else if (!await BooleanAsync(target.Connection, target.Reference.NativeElement, "property/isContentEditable", token)) { throw NotInteractable(); }
            // 事前照会中の文書変更を再照合してから、同じネイティブ要素へ入力する。
            target = await ResolveAsync(args, token);
            await target.Connection.ExecuteAsync(HttpMethod.Post, "element/" + target.Reference.NativeElement + "/clear",
                ResponseJson.ToElement(new { }), token);
            target = await ResolveAsync(args, token);
            if (text.Length != 0)
            {
                await target.Connection.ExecuteAsync(HttpMethod.Post, "element/" + target.Reference.NativeElement + "/value",
                    ResponseJson.ToElement(new { text }), token);
            }
            target = await ResolveAsync(args, token);
            var verification = await target.Connection.ExecuteAsync(HttpMethod.Post, "execute/sync", ResponseJson.ToElement(new
            {
                script = "const e=arguments[0],t=arguments[1];const p=e.tagName==='INPUT'&&e.type==='password';const v=(e.tagName==='INPUT'||e.tagName==='TEXTAREA')?e.value:e.textContent;return {password:p,length:v.length,matches:p?null:v===t};",
                args = new object[] { Native(target.Reference.NativeElement), text }
            }), token);
            if (verification.ValueKind != JsonValueKind.Object || !verification.TryGetProperty("password", out var password)
                || password.ValueKind is not (JsonValueKind.True or JsonValueKind.False)
                || !verification.TryGetProperty("length", out var length) || length.ValueKind != JsonValueKind.Number
                || !length.TryGetInt32(out var count) || count != text.Length
                || !verification.TryGetProperty("matches", out var matches)
                || (password.GetBoolean() ? matches.ValueKind != JsonValueKind.Null : matches.ValueKind != JsonValueKind.True))
            { throw new WindowReferenceException(ErrorCode.DOM_VALUE_VERIFICATION_FAILED); }
            return ResponseJson.ToElement(new { domElementId = target.Reference.DomElementId, inputSent = true,
                utf16Length = text.Length, valueRedacted = password.GetBoolean(),
                verification = password.GetBoolean() ? "length_only" : "exact_match" });
        }, args => ValidElementTarget(args) && ValidDomText(args.GetProperty("text").GetString()!));

    internal static ArgumentSchema ElementSchema(IEnumerable<ArgumentProperty> extra) => ArgumentSchema.Object(new ArgumentProperty[]
    {
        new("browser", ArgumentSchema.String(7, 4, ["chrome", "edge", "firefox"])),
        new("browserId", ArgumentSchema.String(34, 34)), new("tabId", ArgumentSchema.String(35, 35)),
        new("domElementId", ArgumentSchema.String(34, 34))
    }.Concat(extra));

    internal static bool ValidDomText(string text) => (text.Length == 0 || KeyboardOperations.ValidText(text))
        && !text.Any(character => character is >= '\ue000' and <= '\ue05d' || char.IsControl(character) && character is not ('\r' or '\n'));
    private static WindowReferenceException NotInteractable() => new(ErrorCode.DOM_ELEMENT_NOT_INTERACTABLE);
}
