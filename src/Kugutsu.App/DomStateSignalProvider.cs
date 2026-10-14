using System.Text.Json;
using Kugutsu.Contracts;
using Kugutsu.Hosting;

namespace Kugutsu.App;

internal sealed class DomStateSignalProvider(BrowserNavigationOperations navigation, BrowserOperationGate gate,
    KugutsuOptions settings) : ISignalConditionProvider
{
    public string Kind => "dom.state";
    public ArgumentSchema Schema { get; } = ArgumentSchema.Object([
        new("kind", ArgumentSchema.String(9, 9, ["dom.state"])),
        new("browser", ArgumentSchema.String(7, 4, ["chrome", "edge", "firefox"])),
        new("browserId", ArgumentSchema.String(34, 34)), new("tabId", ArgumentSchema.String(35, 35)),
        new("selector", ArgumentSchema.String(4096, 1)),
        new("framePath", ArgumentSchema.Array(ArgumentSchema.String(4096, 1), 16), false),
        new("property", ArgumentSchema.String(7, 1, ["exists", "visible", "enabled"])),
        new("equals", ArgumentSchema.Boolean)]);

    public async ValueTask ValidateAsync(JsonElement condition, CancellationToken token)
    {
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
            var property = condition.GetProperty("property").GetString()!;
            bool value;
            if (property == "exists") { value = matches.Count != 0; }
            else
            {
                if (matches.Count == 0) { throw new WindowReferenceException(ErrorCode.DOM_ELEMENT_NOT_FOUND); }
                if (matches.Count != 1) { throw new WindowReferenceException(ErrorCode.DOM_ELEMENT_AMBIGUOUS); }
                value = await DomOperations.BooleanAsync(connection, matches[0], property == "visible" ? "displayed" : "enabled", token);
            }
            await scope.SelectAsync(token);
            return new(value == condition.GetProperty("equals").GetBoolean(), ResponseJson.ToElement(new {
                browser = condition.GetProperty("browser").GetString(), browserId = condition.GetProperty("browserId").GetString(),
                tabId = condition.GetProperty("tabId").GetString(), property, value, matchedCount = matches.Count }));
        }
        return await AsyncPollingSignalMonitor.StartAsync(settings.SignalPollIntervalMilliseconds, Read, observe, startup, lifetime);
    }
}
