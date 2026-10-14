using System.Text.Json;
using Kugutsu.Contracts;

namespace Kugutsu.App;

internal sealed partial class DomOperations
{
    internal OperationDefinition ValueDefinition() => new("dom.get_value", ElementSchema([]),
        async (args, token) =>
        {
            var target = await ResolveAsync(args, token);
            var state = await ReadStateAsync(target.Connection, target.Reference.NativeElement, token);
            if (state.TagName == "input" && state.InputType == "password")
            {
                return ResponseJson.ToElement(new { domElementId = target.Reference.DomElementId,
                    value = (string?)null, redacted = true, utf16Length = (int?)null });
            }
            var result = await target.Connection.ExecuteAsync(HttpMethod.Post, "execute/sync", ResponseJson.ToElement(new
            {
                script = "const e=arguments[0];const tag=e.tagName.toLowerCase();const supported=tag==='input'||tag==='textarea'||e.isContentEditable===true;const password=tag==='input'&&e.type==='password';if(!supported||password)return {supported,password,value:null,length:null,tooLarge:false};const v=(tag==='input'||tag==='textarea')?e.value:e.textContent;return {supported,password,value:v.length>65536?null:v,length:v.length,tooLarge:v.length>65536};",
                args = new object[] { Native(target.Reference.NativeElement) }
            }), token);
            if (result.ValueKind != JsonValueKind.Object) { throw Lost(); }
            var supported = ReadFlag(result, "supported");
            var password = ReadFlag(result, "password");
            var tooLarge = ReadFlag(result, "tooLarge");
            if (!supported) { throw NotInteractable(); }
            if (!result.TryGetProperty("value", out var value) || !result.TryGetProperty("length", out var length)) { throw Lost(); }
            if (password)
            {
                if (value.ValueKind != JsonValueKind.Null || length.ValueKind != JsonValueKind.Null || tooLarge) { throw Lost(); }
                return ResponseJson.ToElement(new { domElementId = target.Reference.DomElementId,
                    value = (string?)null, redacted = true, utf16Length = (int?)null });
            }
            if (tooLarge) { throw new WindowReferenceException(ErrorCode.DOM_VALUE_TOO_LARGE); }
            if (value.ValueKind != JsonValueKind.String || value.GetString() is not { Length: <= 65536 } text
                || length.ValueKind != JsonValueKind.Number || !length.TryGetInt32(out var count) || count != text.Length) { throw Lost(); }
            await ResolveAsync(args, token);
            return ResponseJson.ToElement(new { domElementId = target.Reference.DomElementId,
                value = text, redacted = false, utf16Length = (int?)count });
        }, ValidElementTarget);

    private static bool ReadFlag(JsonElement value, string name)
    {
        if (!value.TryGetProperty(name, out var flag) || flag.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) { throw Lost(); }
        return flag.GetBoolean();
    }
}
