using Kugutsu.Contracts;

namespace Kugutsu.App;

internal sealed class UiSnapshotOperation(UiDriverRegistry drivers, WindowReferenceRegistry windows)
{
    internal OperationDefinition Definition() => new("ui.snapshot",
        ArgumentSchema.Object([
            new("windowId", ArgumentSchema.String(34, 34)),
            new("driver", ArgumentSchema.String(14, 3, ["uia", "imgui_visual", "generic_visual"])),
            new("profileId", ArgumentSchema.String(64, 1), false),
            new("includeImage", ArgumentSchema.Boolean, false),
            new("maxDepth", ArgumentSchema.Integer(0, 64), false),
            new("maxCandidates", ArgumentSchema.Integer(1, 1000), false)]),
        async (args, token) =>
        {
            var window = windows.Resolve(args.GetProperty("windowId").GetString()!);
            var driver = drivers.Resolve(args.GetProperty("driver").GetString()!);
            var query = new UiSnapshotQuery(ProfileId: args.TryGetProperty("profileId", out var profile) ? profile.GetString() : null,
                IncludeImage: args.TryGetProperty("includeImage", out var image) && image.GetBoolean(),
                MaxDepth: args.TryGetProperty("maxDepth", out var depth) ? depth.GetInt32() : 32,
                MaxCandidates: args.TryGetProperty("maxCandidates", out var count) ? count.GetInt32() : 1000);
            return ResponseJson.ToElement(await driver.SnapshotAsync(window, query, token));
        }, args => WindowReferenceRegistry.IsValidId(args.GetProperty("windowId").GetString())
            && (!args.TryGetProperty("profileId", out var profile) || args.GetProperty("driver").GetString() != "uia"
                && VisualProfileCatalog.Identifier(profile.GetString()!)));
}
