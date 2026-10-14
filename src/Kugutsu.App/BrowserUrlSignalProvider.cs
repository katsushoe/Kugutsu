using System.Text.Json;
using Kugutsu.Contracts;
using Kugutsu.Hosting;

namespace Kugutsu.App;

internal sealed class BrowserUrlSignalProvider(BrowserNavigationOperations navigation, BrowserOperationGate gate,
    KugutsuOptions settings) : ISignalConditionProvider
{
    public string Kind => "browser.url";
    public ArgumentSchema Schema { get; } = ArgumentSchema.Object([
        new("kind", ArgumentSchema.String(11, 11, ["browser.url"])),
        new("browser", ArgumentSchema.String(7, 4, ["chrome", "edge", "firefox"])),
        new("browserId", ArgumentSchema.String(34, 34)), new("tabId", ArgumentSchema.String(35, 35)),
        new("comparison", ArgumentSchema.String(8, 6, ["equals", "contains"])),
        new("url", ArgumentSchema.String(8192))]);

    public async ValueTask ValidateAsync(JsonElement condition, CancellationToken token)
    {
        var expected = condition.GetProperty("url").GetString()!;
        if (!DomOperations.ValidTarget(condition) || expected.Any(char.IsControl) || !ControlTextSignalProvider.ValidText(expected))
        { throw new WindowReferenceException(ErrorCode.INVALID_ARGUMENT); }
        await ReadAsync(condition, token);
    }

    public ValueTask<IAsyncDisposable> StartAsync(JsonElement condition, Action<SignalObservation> observe,
        CancellationToken startup, CancellationToken lifetime) =>
        AsyncPollingSignalMonitor.StartAsync(settings.SignalPollIntervalMilliseconds,
            token => ReadAsync(condition, token), observe, startup, lifetime);

    private async ValueTask<SignalObservation> ReadAsync(JsonElement condition, CancellationToken token)
    {
        using var lease = await gate.EnterAsync(token);
        var target = await navigation.SelectTabAsync(condition.GetProperty("browserId").GetString()!,
            BrowserSessionRegistry.Parse(condition.GetProperty("browser").GetString()!), condition.GetProperty("tabId").GetString()!, token);
        var url = await BrowserNavigationOperations.ReadUrlAsync(target.Browser.Connection, token);
        var comparison = condition.GetProperty("comparison").GetString()!;
        var expected = condition.GetProperty("url").GetString()!;
        var matched = comparison == "equals" ? string.Equals(url, expected, StringComparison.Ordinal)
            : url.Contains(expected, StringComparison.Ordinal);
        // URLs can carry credentials or tokens; retain only the comparison outcome.
        return new(matched, ResponseJson.ToElement(new { browser = condition.GetProperty("browser").GetString(),
            browserId = condition.GetProperty("browserId").GetString(), tabId = condition.GetProperty("tabId").GetString(),
            comparison, matched, urlLength = url.Length }));
    }
}
