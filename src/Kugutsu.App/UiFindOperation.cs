using Kugutsu.Contracts;

namespace Kugutsu.App;

internal sealed class UiFindOperation(UiDriverRegistry drivers, WindowReferenceRegistry windows)
{
    private static readonly string[] ControlTypes = typeof(System.Windows.Automation.ControlType)
        .GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
        .Where(field => field.FieldType == typeof(System.Windows.Automation.ControlType))
        .Select(field => field.Name).ToArray();
    internal OperationDefinition Definition() => new("control.find",
        ArgumentSchema.Object([
            new("windowId", ArgumentSchema.String(34, 34)),
            new("driver", ArgumentSchema.String(14, 3, ["uia", "imgui_visual", "generic_visual"]), false),
            new("profileId", ArgumentSchema.String(64, 1), false),
            new("semanticRole", ArgumentSchema.String(64, 1), false),
            new("automationId", ArgumentSchema.String(512), false),
            new("name", ArgumentSchema.String(4096), false),
            new("controlType", ArgumentSchema.String(64, 1, ControlTypes), false),
            new("maxCandidates", ArgumentSchema.Integer(1, 1000), false)]),
        async (args, token) =>
        {
            var driver = drivers.Resolve(args.TryGetProperty("driver", out var selected) ? selected.GetString()! : "uia");
            var window = windows.Resolve(args.GetProperty("windowId").GetString()!);
            string? Read(string name) => args.TryGetProperty(name, out var value) ? value.GetString() : null;
            var query = new UiFindQuery(Read("automationId"), Read("name"), Read("controlType"), Read("semanticRole"), Read("profileId"),
                MaxCandidates: args.TryGetProperty("maxCandidates", out var maximum) ? maximum.GetInt32() : 1000);
            return ResponseJson.ToElement(await driver.FindAsync(window, query, token));
        },
        args => WindowReferenceRegistry.IsValidId(args.GetProperty("windowId").GetString())
            && args.EnumerateObject().All(property => property.Value.ValueKind != System.Text.Json.JsonValueKind.String
                || !property.Value.GetString()!.Contains('\0'))
            && ValidImageArguments(args));

    private static bool ValidImageArguments(System.Text.Json.JsonElement args)
    {
        var driver = args.TryGetProperty("driver", out var selected) ? selected.GetString() : "uia";
        if (driver == "uia") { return !args.TryGetProperty("profileId", out _) && !args.TryGetProperty("semanticRole", out _); }
        return !args.TryGetProperty("automationId", out _)
            && (!args.TryGetProperty("controlType", out var type) || type.GetString() is "Edit" or "Button")
            && (!args.TryGetProperty("profileId", out var profile) || VisualProfileCatalog.Identifier(profile.GetString()!))
            && (!args.TryGetProperty("semanticRole", out var role) || VisualProfileCatalog.Identifier(role.GetString()!));
    }
}
