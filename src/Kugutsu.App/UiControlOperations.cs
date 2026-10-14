using Kugutsu.Contracts;

namespace Kugutsu.App;

internal sealed class UiControlOperations(UiElementRegistry elements, UiDriverRegistry drivers)
{
    internal OperationDefinition StateDefinition() => ReadDefinition("control.get_state");
    internal OperationDefinition ValueDefinition() => new("control.get_value",
        ArgumentSchema.Object([
            new("windowId", ArgumentSchema.String(34, 34)),
            new("elementId", ArgumentSchema.String(34, 34)),
            new("method", ArgumentSchema.String(9, 9, ["local_ocr"]), false)]),
        async (args, token) =>
        {
            var reference = elements.Resolve(args.GetProperty("elementId").GetString()!, args.GetProperty("windowId").GetString()!, token);
            if (reference.Target.Driver == "uia" && args.TryGetProperty("method", out _))
            { throw new WindowReferenceException(ErrorCode.INVALID_ARGUMENT); }
            return await drivers.Resolve(reference.Target.Driver).ExecuteAsync(reference.Target, "control.get_value", args, token);
        }, args => WindowReferenceRegistry.IsValidId(args.GetProperty("windowId").GetString())
            && UiElementRegistry.IsValidId(args.GetProperty("elementId").GetString()));
    internal OperationDefinition InvokeDefinition() => new("control.invoke",
        ArgumentSchema.Object([
            new("windowId", ArgumentSchema.String(34, 34)),
            new("elementId", ArgumentSchema.String(34, 34)),
            new("method", ArgumentSchema.String(16, 16, ["coordinate_click"]), false)]),
        async (args, token) =>
        {
            var reference = elements.Resolve(args.GetProperty("elementId").GetString()!, args.GetProperty("windowId").GetString()!, token);
            if (reference.Target.Driver == "uia" && args.TryGetProperty("method", out _))
            { throw new WindowReferenceException(ErrorCode.INVALID_ARGUMENT); }
            return await drivers.Resolve(reference.Target.Driver).ExecuteAsync(reference.Target, "control.invoke", args, token);
        }, args => WindowReferenceRegistry.IsValidId(args.GetProperty("windowId").GetString())
            && UiElementRegistry.IsValidId(args.GetProperty("elementId").GetString()));

    internal OperationDefinition GridSetValueDefinition() => new("grid.set_cell_value",
        ArgumentSchema.Object([
            new("windowId", ArgumentSchema.String(34, 34)),
            new("elementId", ArgumentSchema.String(34, 34)),
            new("row", ArgumentSchema.Integer(0, int.MaxValue)),
            new("column", ArgumentSchema.Integer(0, int.MaxValue)),
            new("value", ArgumentSchema.String(65536))]),
        async (args, token) =>
        {
            var reference = elements.Resolve(args.GetProperty("elementId").GetString()!, args.GetProperty("windowId").GetString()!, token);
            return await drivers.Resolve(reference.Target.Driver).ExecuteAsync(reference.Target, "grid.set_cell_value", args, token);
        }, args => WindowReferenceRegistry.IsValidId(args.GetProperty("windowId").GetString())
            && UiElementRegistry.IsValidId(args.GetProperty("elementId").GetString())
            && ValidValue(args.GetProperty("value").GetString()!));

    internal OperationDefinition GridCellDefinition() => new("grid.get_cell",
        ArgumentSchema.Object([
            new("windowId", ArgumentSchema.String(34, 34)),
            new("elementId", ArgumentSchema.String(34, 34)),
            new("row", ArgumentSchema.Integer(0, int.MaxValue)),
            new("column", ArgumentSchema.Integer(0, int.MaxValue))]),
        async (args, token) =>
        {
            var reference = elements.Resolve(args.GetProperty("elementId").GetString()!, args.GetProperty("windowId").GetString()!, token);
            return await drivers.Resolve(reference.Target.Driver).ExecuteAsync(reference.Target, "grid.get_cell", args, token);
        }, args => WindowReferenceRegistry.IsValidId(args.GetProperty("windowId").GetString())
            && UiElementRegistry.IsValidId(args.GetProperty("elementId").GetString()));

    internal OperationDefinition TextSelectionDefinition() => new("control.set_text_selection",
        ArgumentSchema.Object([
            new("windowId", ArgumentSchema.String(34, 34)),
            new("elementId", ArgumentSchema.String(34, 34)),
            new("start", ArgumentSchema.Integer(0, 65536)),
            new("length", ArgumentSchema.Integer(0, 65536))]),
        async (args, token) =>
        {
            var reference = elements.Resolve(args.GetProperty("elementId").GetString()!, args.GetProperty("windowId").GetString()!, token);
            return await drivers.Resolve(reference.Target.Driver).ExecuteAsync(reference.Target, "control.set_text_selection", args, token);
        }, args => WindowReferenceRegistry.IsValidId(args.GetProperty("windowId").GetString())
            && UiElementRegistry.IsValidId(args.GetProperty("elementId").GetString())
            && args.GetProperty("start").GetInt32() + args.GetProperty("length").GetInt32() <= 65536);

    internal OperationDefinition SelectItemsDefinition() => new("control.select_items",
        ArgumentSchema.Object([
            new("windowId", ArgumentSchema.String(34, 34)),
            new("elementId", ArgumentSchema.String(34, 34)),
            new("items", ArgumentSchema.Array(ArgumentSchema.String(34, 34), 100)),
            new("mode", ArgumentSchema.String(7, 3, ["replace", "add", "remove"]))]),
        async (args, token) =>
        {
            var reference = elements.Resolve(args.GetProperty("elementId").GetString()!, args.GetProperty("windowId").GetString()!, token);
            return await drivers.Resolve(reference.Target.Driver).ExecuteAsync(reference.Target, "control.select_items", args, token);
        }, args => WindowReferenceRegistry.IsValidId(args.GetProperty("windowId").GetString())
            && UiElementRegistry.IsValidId(args.GetProperty("elementId").GetString())
            && args.GetProperty("items").EnumerateArray().All(item => UiElementRegistry.IsValidId(item.GetString()))
            && args.GetProperty("items").EnumerateArray().Select(item => item.GetString()).Distinct(StringComparer.Ordinal).Count()
                == args.GetProperty("items").GetArrayLength());

    internal OperationDefinition ScrollDefinition() => new("control.scroll",
        ArgumentSchema.Object([
            new("windowId", ArgumentSchema.String(34, 34)),
            new("elementId", ArgumentSchema.String(34, 34)),
            new("direction", ArgumentSchema.String(5, 2, ["up", "down", "left", "right"])),
            new("step", ArgumentSchema.String(5, 5, ["small", "large"])),
            new("repeat", ArgumentSchema.Integer(1, 20))]),
        async (args, token) =>
        {
            var reference = elements.Resolve(args.GetProperty("elementId").GetString()!, args.GetProperty("windowId").GetString()!, token);
            return await drivers.Resolve(reference.Target.Driver).ExecuteAsync(reference.Target, "control.scroll", args, token);
        }, args => WindowReferenceRegistry.IsValidId(args.GetProperty("windowId").GetString())
            && UiElementRegistry.IsValidId(args.GetProperty("elementId").GetString()));

    internal OperationDefinition ExpandDefinition() => new("control.expand_collapse",
        ArgumentSchema.Object([
            new("windowId", ArgumentSchema.String(34, 34)),
            new("elementId", ArgumentSchema.String(34, 34)),
            new("action", ArgumentSchema.String(8, 6, ["expand", "collapse"]))]),
        async (args, token) =>
        {
            var reference = elements.Resolve(args.GetProperty("elementId").GetString()!, args.GetProperty("windowId").GetString()!, token);
            return await drivers.Resolve(reference.Target.Driver).ExecuteAsync(reference.Target, "control.expand_collapse", args, token);
        }, args => WindowReferenceRegistry.IsValidId(args.GetProperty("windowId").GetString())
            && UiElementRegistry.IsValidId(args.GetProperty("elementId").GetString()));

    internal OperationDefinition RangeDefinition() => new("control.set_range_value",
        ArgumentSchema.Object([
            new("windowId", ArgumentSchema.String(34, 34)),
            new("elementId", ArgumentSchema.String(34, 34)),
            new("value", ArgumentSchema.Number(-double.MaxValue, double.MaxValue))]),
        async (args, token) =>
        {
            var reference = elements.Resolve(args.GetProperty("elementId").GetString()!, args.GetProperty("windowId").GetString()!, token);
            return await drivers.Resolve(reference.Target.Driver).ExecuteAsync(reference.Target, "control.set_range_value", args, token);
        }, args => WindowReferenceRegistry.IsValidId(args.GetProperty("windowId").GetString())
            && UiElementRegistry.IsValidId(args.GetProperty("elementId").GetString()));

    internal OperationDefinition ToggleDefinition() => new("control.set_toggle",
        ArgumentSchema.Object([
            new("windowId", ArgumentSchema.String(34, 34)),
            new("elementId", ArgumentSchema.String(34, 34)),
            new("isOn", ArgumentSchema.Boolean)]),
        async (args, token) =>
        {
            var reference = elements.Resolve(args.GetProperty("elementId").GetString()!, args.GetProperty("windowId").GetString()!, token);
            return await drivers.Resolve(reference.Target.Driver).ExecuteAsync(reference.Target, "control.set_toggle", args, token);
        }, args => WindowReferenceRegistry.IsValidId(args.GetProperty("windowId").GetString())
            && UiElementRegistry.IsValidId(args.GetProperty("elementId").GetString()));

    internal OperationDefinition SelectDefinition() => new("control.select",
        ArgumentSchema.Object([
            new("windowId", ArgumentSchema.String(34, 34)),
            new("elementId", ArgumentSchema.String(34, 34)),
            new("item", ArgumentSchema.Object([
                new("automationId", ArgumentSchema.String(512, 1), false),
                new("name", ArgumentSchema.String(4096, 1), false)]), false)]),
        async (args, token) =>
        {
            var reference = elements.Resolve(args.GetProperty("elementId").GetString()!, args.GetProperty("windowId").GetString()!, token);
            return await drivers.Resolve(reference.Target.Driver).ExecuteAsync(reference.Target, "control.select", args, token);
        }, args => WindowReferenceRegistry.IsValidId(args.GetProperty("windowId").GetString())
            && UiElementRegistry.IsValidId(args.GetProperty("elementId").GetString())
            && (!args.TryGetProperty("item", out var item) || item.EnumerateObject().Any()
                && item.EnumerateObject().All(property => !property.Value.GetString()!.Contains('\0'))));

    internal OperationDefinition SendKeysDefinition() => new("control.send_keys",
        ArgumentSchema.Object([
            new("windowId", ArgumentSchema.String(34, 34)),
            new("elementId", ArgumentSchema.String(34, 34)),
            new("keySequence", ArgumentSchema.Array(ArgumentSchema.Object([
                new("key", ArgumentSchema.String(16, 1, KeyboardOperations.KeyNames)),
                new("modifiers", ArgumentSchema.Array(ArgumentSchema.String(5, 3, KeyboardOperations.ModifierNames), 4), false)]), 100, 1)),
            new("method", ArgumentSchema.String(19, 19, ["coordinate_keyboard"]), false)]),
        async (args, token) =>
        {
            var reference = elements.Resolve(args.GetProperty("elementId").GetString()!,
                args.GetProperty("windowId").GetString()!, token);
            if (reference.Target.Driver == "uia" && args.TryGetProperty("method", out _))
            { throw new WindowReferenceException(ErrorCode.INVALID_ARGUMENT); }
            return await drivers.Resolve(reference.Target.Driver).ExecuteAsync(reference.Target, "control.send_keys", args, token);
        }, args => WindowReferenceRegistry.IsValidId(args.GetProperty("windowId").GetString())
            && UiElementRegistry.IsValidId(args.GetProperty("elementId").GetString())
            && args.GetProperty("keySequence").EnumerateArray().All(item =>
                KeyboardOperations.ReadModifiers(item).Distinct(StringComparer.Ordinal).Count() == KeyboardOperations.ReadModifiers(item).Length));

    internal OperationDefinition SetValueDefinition() => new("control.set_value",
        ArgumentSchema.Object([
            new("windowId", ArgumentSchema.String(34, 34)),
            new("elementId", ArgumentSchema.String(34, 34)),
            new("value", ArgumentSchema.String(65536)),
            new("method", ArgumentSchema.String(19, 19, ["coordinate_keyboard"]), false)]),
        async (args, token) =>
        {
            var reference = elements.Resolve(args.GetProperty("elementId").GetString()!,
                args.GetProperty("windowId").GetString()!, token);
            if (reference.Target.Driver == "uia" && args.TryGetProperty("method", out _))
            { throw new WindowReferenceException(ErrorCode.INVALID_ARGUMENT); }
            return await drivers.Resolve(reference.Target.Driver).ExecuteAsync(reference.Target, "control.set_value", args, token);
        }, args => WindowReferenceRegistry.IsValidId(args.GetProperty("windowId").GetString())
            && UiElementRegistry.IsValidId(args.GetProperty("elementId").GetString())
            && ValidValue(args.GetProperty("value").GetString()!));

    private static bool ValidValue(string value)
    {
        if (value.Contains('\0')) { return false; }
        for (var index = 0; index < value.Length; index++)
        {
            if (char.IsHighSurrogate(value[index]))
            { if (++index >= value.Length || !char.IsLowSurrogate(value[index])) { return false; } }
            else if (char.IsLowSurrogate(value[index])) { return false; }
        }
        return true;
    }

    private OperationDefinition ReadDefinition(string name) => new(name,
        ArgumentSchema.Object([
            new("windowId", ArgumentSchema.String(34, 34)),
            new("elementId", ArgumentSchema.String(34, 34))]),
        async (args, token) =>
        {
            var reference = elements.Resolve(args.GetProperty("elementId").GetString()!,
                args.GetProperty("windowId").GetString()!, token);
            return await drivers.Resolve(reference.Target.Driver).ExecuteAsync(reference.Target, name, args, token);
        }, args => WindowReferenceRegistry.IsValidId(args.GetProperty("windowId").GetString())
            && UiElementRegistry.IsValidId(args.GetProperty("elementId").GetString()));
}
