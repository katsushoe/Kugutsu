using System.Text.Json;
using System.Windows.Automation;
using Kugutsu.Contracts;
using Kugutsu.Hosting;

namespace Kugutsu.App;

internal sealed class ControlStateSignalProvider(UiElementRegistry elements, UiDriverRegistry drivers,
    KugutsuOptions settings) : ISignalConditionProvider
{
    private sealed class StateSchema : ArgumentSchema
    {
        private static ArgumentSchema For(ArgumentSchema equals) => Object([
            new("kind", String(13, 13, ["control.state"])), new("windowId", String(34, 34)),
            new("elementId", String(34, 34)),
            new("property", String(8, 1, ["enabled", "visible", "focused", "toggle", "selected"])),
            new("equals", equals)]);
        private static readonly ArgumentSchema BooleanState = For(Boolean);
        private static readonly ArgumentSchema ToggleState = For(String(13, 2, ["on", "off", "indeterminate"]));
        public override bool Accepts(JsonElement value) => value.ValueKind == JsonValueKind.Object
            && value.TryGetProperty("property", out var property)
            && property.ValueKind == JsonValueKind.String
            && (property.GetString() == "toggle" ? ToggleState : BooleanState).Accepts(value);
    }
    public string Kind => "control.state";
    public ArgumentSchema Schema { get; } = new StateSchema();

    public async ValueTask ValidateAsync(JsonElement condition, CancellationToken token)
    {
        var reference = Resolve(condition, token);
        await ReadAsync(condition, reference, token);
    }

    public ValueTask<IAsyncDisposable> StartAsync(JsonElement condition, Action<SignalObservation> observe,
        CancellationToken startup, CancellationToken lifetime)
    {
        var reference = Resolve(condition, startup);
        return AsyncPollingSignalMonitor.StartAsync(settings.SignalPollIntervalMilliseconds,
            token => ReadAsync(condition, reference, token), observe, startup, lifetime);
    }

    private UiElementReference Resolve(JsonElement condition, CancellationToken token)
    {
        var reference = elements.Resolve(condition.GetProperty("elementId").GetString()!,
            condition.GetProperty("windowId").GetString()!, token);
        if (reference.Target.Driver != "uia" || reference.NativeTarget is not UiaElementHandle)
        { throw new WindowReferenceException(ErrorCode.SIGNAL_CONDITION_UNSUPPORTED); }
        return reference;
    }

    private async ValueTask<SignalObservation> ReadAsync(JsonElement condition, UiElementReference reference,
        CancellationToken token)
    {
        if (Resolve(condition, token) != reference) { throw new WindowReferenceException(ErrorCode.UI_ELEMENT_STALE); }
        var driver = drivers.Resolve("uia") as UiaDriver
            ?? throw new WindowReferenceException(ErrorCode.SIGNAL_CONDITION_UNSUPPORTED);
        var property = condition.GetProperty("property").GetString()!;
        var value = await driver.WithElementAsync(reference.Target.Window, (UiaElementHandle)reference.NativeTarget,
            element =>
            {
                bool ReadBool(AutomationProperty id) => element.GetCurrentPropertyValue(id, true) is bool result
                    ? result : throw new WindowReferenceException(ErrorCode.SIGNAL_CONDITION_UNSUPPORTED);
                object actual = property switch
                {
                    "enabled" => ReadBool(AutomationElement.IsEnabledProperty),
                    "visible" => !ReadBool(AutomationElement.IsOffscreenProperty),
                    "focused" => ReadBool(AutomationElement.HasKeyboardFocusProperty),
                    "selected" => element.TryGetCurrentPattern(SelectionItemPattern.Pattern, out var raw) && raw is SelectionItemPattern selection
                        ? selection.Current.IsSelected : throw new WindowReferenceException(ErrorCode.SIGNAL_CONDITION_UNSUPPORTED),
                    "toggle" => element.TryGetCurrentPattern(TogglePattern.Pattern, out var raw) && raw is TogglePattern toggle
                        ? toggle.Current.ToggleState switch { ToggleState.On => "on", ToggleState.Off => "off", ToggleState.Indeterminate => "indeterminate",
                            _ => throw new WindowReferenceException(ErrorCode.SIGNAL_MONITOR_FAILED) }
                        : throw new WindowReferenceException(ErrorCode.SIGNAL_CONDITION_UNSUPPORTED),
                    _ => throw new WindowReferenceException(ErrorCode.INVALID_ARGUMENT)
                };
                return JsonSerializer.SerializeToElement(actual);
            }, token);
        if (Resolve(condition, token) != reference) { throw new WindowReferenceException(ErrorCode.UI_ELEMENT_STALE); }
        var expected = condition.GetProperty("equals");
        var matched = value.ValueKind == expected.ValueKind && (value.ValueKind == JsonValueKind.String
            ? value.GetString() == expected.GetString() : value.GetBoolean() == expected.GetBoolean());
        // True and false are distinct JsonValueKinds; compare booleans as values.
        if (value.ValueKind is JsonValueKind.True or JsonValueKind.False && expected.ValueKind is JsonValueKind.True or JsonValueKind.False)
        { matched = value.GetBoolean() == expected.GetBoolean(); }
        return new(matched, ResponseJson.ToElement(new { windowId = reference.Target.Window.WindowId,
            elementId = reference.Target.ElementId, property, value }));
    }
}
