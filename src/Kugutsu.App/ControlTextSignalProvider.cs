using System.Text.Json;
using System.Windows.Automation;
using Kugutsu.Contracts;
using Kugutsu.Hosting;

namespace Kugutsu.App;

internal sealed class ControlTextSignalProvider(UiElementRegistry elements, UiDriverRegistry drivers,
    KugutsuOptions settings) : ISignalConditionProvider
{
    public string Kind => "control.text";
    public ArgumentSchema Schema { get; } = ArgumentSchema.Object([
        new("kind", ArgumentSchema.String(12, 12, ["control.text"])),
        new("windowId", ArgumentSchema.String(34, 34)), new("elementId", ArgumentSchema.String(34, 34)),
        new("comparison", ArgumentSchema.String(8, 6, ["equals", "contains"])),
        new("text", ArgumentSchema.String(4096))]);

    public async ValueTask ValidateAsync(JsonElement condition, CancellationToken token)
    {
        var text = condition.GetProperty("text").GetString()!;
        if (!ValidText(text)) { throw new WindowReferenceException(ErrorCode.INVALID_ARGUMENT); }
        await ReadAsync(condition, Resolve(condition, token), token);
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
        var comparison = condition.GetProperty("comparison").GetString()!;
        var expected = condition.GetProperty("text").GetString()!;
        var result = await driver.WithElementAsync(reference.Target.Window, (UiaElementHandle)reference.NativeTarget,
            element =>
            {
                void VerifyPublic()
                {
                    if (element.GetCurrentPropertyValue(AutomationElement.IsPasswordProperty, true) is not false)
                    { throw new WindowReferenceException(ErrorCode.SIGNAL_CONDITION_UNSUPPORTED); }
                }
                VerifyPublic();
                string text;
                string source;
                if (element.TryGetCurrentPattern(ValuePattern.Pattern, out var raw) && raw is ValuePattern value)
                { text = value.Current.Value; source = "uia_value"; }
                else if (element.TryGetCurrentPattern(TextPattern.Pattern, out raw) && raw is TextPattern pattern)
                { text = pattern.DocumentRange.GetText(65537); source = "uia_text"; }
                else { throw new WindowReferenceException(ErrorCode.SIGNAL_CONDITION_UNSUPPORTED); }
                VerifyPublic();
                if (text.Length > 65536 || !ValidText(text)) { throw new WindowReferenceException(ErrorCode.SIGNAL_MONITOR_FAILED); }
                var matched = comparison == "equals" ? string.Equals(text, expected, StringComparison.Ordinal)
                    : text.Contains(expected, StringComparison.Ordinal);
                return (matched, length: text.Length, source);
            }, token);
        if (Resolve(condition, token) != reference) { throw new WindowReferenceException(ErrorCode.UI_ELEMENT_STALE); }
        return new(result.matched, ResponseJson.ToElement(new { windowId = reference.Target.Window.WindowId,
            elementId = reference.Target.ElementId, comparison, matched = result.matched, textLength = result.length, source = result.source }));
    }

    internal static bool ValidText(string text)
    {
        for (var index = 0; index < text.Length; index++)
        {
            var character = text[index];
            if (character == '\0' || char.IsLowSurrogate(character)) { return false; }
            if (char.IsHighSurrogate(character) && (++index >= text.Length || !char.IsLowSurrogate(text[index]))) { return false; }
        }
        return true;
    }
}
