using Kugutsu.Contracts;

namespace Kugutsu.App;

internal sealed partial class DomOperations
{
    internal OperationDefinition ClickDefinition() => new("dom.click", ElementSchema([]),
        async (args, token) =>
        {
            var target = await ResolveAsync(args, token);
            var state = await ReadStateAsync(target.Connection, target.Reference.NativeElement, token);
            if (!state.Visible || !state.Enabled) { throw NotInteractable(); }
            target = await ResolveAsync(args, token);
            // WebDriverのスクロールと中心点の遮蔽判定を使い、JSクリックへ置き換えない。
            await target.Connection.ExecuteAsync(HttpMethod.Post, "element/" + target.Reference.NativeElement + "/click",
                ResponseJson.ToElement(new { }), token);
            return ResponseJson.ToElement(new { domElementId = target.Reference.DomElementId, clickSent = true });
        }, ValidElementTarget);
}
