using System.Collections.Frozen;
using System.Text.Json;
using Kugutsu.Contracts;
using Kugutsu.Hosting;

namespace Kugutsu.App;

/// <summary>設定と実装の登録を分け、選択したドライバだけを解決する。</summary>
internal sealed class UiDriverRegistry
{
    private static readonly string[] KnownDrivers = ["uia", "imgui_visual", "generic_visual"];
    private readonly FrozenSet<string> _enabled;
    private readonly FrozenDictionary<string, IUiDriver> _drivers;

    public UiDriverRegistry(KugutsuOptions settings, IEnumerable<IUiDriver> drivers)
    {
        _enabled = settings.EnabledUiDrivers.ToFrozenSet(StringComparer.Ordinal);
        var registered = new Dictionary<string, IUiDriver>(StringComparer.Ordinal);
        foreach (var driver in drivers)
        {
            ArgumentNullException.ThrowIfNull(driver);
            var info = driver.Info;
            if (!KnownDrivers.Contains(info.Driver, StringComparer.Ordinal) || !registered.TryAdd(info.Driver, driver))
            { throw new ArgumentException("Invalid UI driver registration.", nameof(drivers)); }
        }
        _drivers = registered.ToFrozenDictionary(StringComparer.Ordinal);
    }

    internal OperationDefinition ListDefinition() => new("ui.list_drivers", ArgumentSchema.Object([]),
        (_, token) =>
        {
            token.ThrowIfCancellationRequested();
            var rows = KnownDrivers.Select(name =>
            {
                token.ThrowIfCancellationRequested();
                var enabled = _enabled.Contains(name);
                var registered = _drivers.TryGetValue(name, out var driver);
                var info = driver is null ? null : ReadInfo(name, driver);
                var available = enabled && info?.Available == true;
                var reason = !enabled ? "disabled" : !registered ? "not_implemented"
                    : !available ? "not_ready" : "ready";
                return new
                {
                    driver = name, enabled, registered, available, reason,
                    unavailableReason = available ? (ErrorCode?)null
                        : !enabled ? ErrorCode.UI_DRIVER_UNAVAILABLE : info?.UnavailableReason ?? ErrorCode.UI_DRIVER_UNAVAILABLE,
                    discoveryCapabilities = enabled && info is not null ? info.DiscoveryCapabilities : [],
                    supportedOperations = enabled && info is not null ? info.SupportedOperations : [],
                    profiles = enabled && info is not null ? info.Profiles : []
                };
            }).ToArray();
            token.ThrowIfCancellationRequested();
            return ValueTask.FromResult<JsonElement?>(ResponseJson.ToElement(new { drivers = rows }));
        });

    internal IUiDriver Resolve(string name)
    {
        if (!KnownDrivers.Contains(name, StringComparer.Ordinal))
        { throw new WindowReferenceException(ErrorCode.INVALID_ARGUMENT); }
        if (!_enabled.Contains(name) || !_drivers.TryGetValue(name, out var driver))
        { throw new WindowReferenceException(ErrorCode.UI_DRIVER_UNAVAILABLE); }
        var info = ReadInfo(name, driver);
        if (!info.Available) { throw new WindowReferenceException(info.UnavailableReason ?? ErrorCode.UI_DRIVER_UNAVAILABLE); }
        return driver;
    }

    private static UiDriverInfo ReadInfo(string name, IUiDriver driver)
    {
        var info = driver.Info;
        if (info.Driver != name) { throw new InvalidOperationException("UI driver identity changed."); }
        return info;
    }
}
