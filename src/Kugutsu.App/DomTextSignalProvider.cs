using System.Text.Json;
using Kugutsu.Contracts;
using Kugutsu.Hosting;

namespace Kugutsu.App;

internal sealed class DomTextSignalProvider(BrowserNavigationOperations navigation, BrowserOperationGate gate,
    KugutsuOptions settings) : ISignalConditionProvider
{
    public string Kind => "dom.text";
    public ArgumentSchema Schema { get; } = ArgumentSchema.Object([
        new("kind", ArgumentSchema.String(8, 8, ["dom.text"])),
        new("browser", ArgumentSchema.String(7, 4, ["chrome", "edge", "firefox"])),
        new("browserId", ArgumentSchema.String(34, 34)), new("tabId", ArgumentSchema.String(35, 35)),
        new("selector", ArgumentSchema.String(4096, 1)),
        new("framePath", ArgumentSchema.Array(ArgumentSchema.String(4096, 1), 16), false),
        new("comparison", ArgumentSchema.String(8, 6, ["equals", "contains"])),
        new("text", ArgumentSchema.String(4096))]);

    public async ValueTask ValidateAsync(JsonElement condition, CancellationToken token)
    {
        if (!ControlTextSignalProvider.ValidText(condition.GetProperty("text").GetString()!))
        { throw new WindowReferenceException(ErrorCode.INVALID_ARGUMENT); }
        using var lease = await gate.EnterAsync(token);
        await DomSignalScope.CreateAsync(navigation, condition, token);
    }

    public async ValueTask<IAsyncDisposable> StartAsync(JsonElement condition, Action<SignalObservation> observe,
        CancellationToken startup, CancellationToken lifetime)
    {
        DomSignalScope scope;
        using (var lease = await gate.EnterAsync(startup)) { scope = await DomSignalScope.CreateAsync(navigation, condition, startup); }
        async ValueTask<SignalObservation> Read(CancellationToken token)
        {
            using var lease = await gate.EnterAsync(token);
            var connection = await scope.SelectAsync(token);
            var matches = await DomOperations.FindNativeAsync(connection, condition.GetProperty("selector").GetString()!, token);
            if (matches.Count == 0) { throw new WindowReferenceException(ErrorCode.DOM_ELEMENT_NOT_FOUND); }
            if (matches.Count != 1) { throw new WindowReferenceException(ErrorCode.DOM_ELEMENT_AMBIGUOUS); }
            async ValueTask VerifyPublic()
            {
                var state = await DomOperations.ReadStateAsync(connection, matches[0], token);
                if (state.InputType?.Equals("password", StringComparison.OrdinalIgnoreCase) == true)
                { throw new WindowReferenceException(ErrorCode.SIGNAL_CONDITION_UNSUPPORTED); }
            }
            await VerifyPublic();
            var response = await DomOperations.ElementAsync(connection, matches[0], "text", token);
            if (response.ValueKind != JsonValueKind.String || response.GetString() is not { Length: <= 65536 } text
                || !ControlTextSignalProvider.ValidText(text)) { throw new WindowReferenceException(ErrorCode.SIGNAL_MONITOR_FAILED); }
            await VerifyPublic();
            await scope.SelectAsync(token);
            var comparison = condition.GetProperty("comparison").GetString()!;
            var expected = condition.GetProperty("text").GetString()!;
            var matched = comparison == "equals" ? string.Equals(text, expected, StringComparison.Ordinal)
                : text.Contains(expected, StringComparison.Ordinal);
            return new(matched, ResponseJson.ToElement(new { browser = condition.GetProperty("browser").GetString(),
                browserId = condition.GetProperty("browserId").GetString(), tabId = condition.GetProperty("tabId").GetString(),
                comparison, matched, textLength = text.Length, source = "webdriver_rendered_text" }));
        }
        return await AsyncPollingSignalMonitor.StartAsync(settings.SignalPollIntervalMilliseconds, Read, observe, startup, lifetime);
    }
}
