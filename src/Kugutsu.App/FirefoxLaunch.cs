using System.Diagnostics;
using System.Text.Json;
using Kugutsu.Contracts;

namespace Kugutsu.App;

internal sealed partial class BrowserLaunchOperation
{
    private async ValueTask<JsonElement> LaunchFirefoxAsync(string url, CancellationToken token)
    {
        if (!settings.BrowserLaunchTargets.TryGetValue("firefox", out var targetId) || !targets.IsRegistered(targetId))
        { throw new WindowReferenceException(ErrorCode.BROWSER_NOT_INSTALLED); }
        if (!drivers.IsRegistered(BrowserKind.Firefox)) { throw Incompatible(); }
        using var browserLease = await targets.ResolveAsync(targetId, token);
        using var driverLease = await drivers.ResolveAsync(BrowserKind.Firefox, token);
        var target = browserLease.Target;
        if (!Path.GetFileName(target.ExecutablePath).Equals("firefox.exe", StringComparison.OrdinalIgnoreCase)
            || target.Arguments.Count != 0
            || !Path.GetFileName(driverLease.Target.ExecutablePath).Equals("geckodriver.exe", StringComparison.OrdinalIgnoreCase))
        { throw Incompatible(); }
        var browserVersion = ReadVersion(target.ExecutablePath);
        var driverVersion = await driverVersions.ReadGeckoAsync(driverLease.Target, token);
        if (browserVersion.Major < 115 || driverVersion != new Version(0, 36, 0)) { throw Incompatible(); }
        var profile = CreateProfile(token);
        var port = ReservePort();
        var driver = await drivers.LaunchAsync(BrowserKind.Firefox,
            ["--host", "127.0.0.1", "--port", port.ToString(System.Globalization.CultureInfo.InvariantCulture), "--log", "fatal"], token);
        var transport = new WebDriverTransport(driver, port, applications);
        WebDriverConnection? connection = null;
        var transferred = false;
        try
        {
            await WaitForDriverAsync(transport, driver, token);
            var options = new Dictionary<string, object>
            {
                ["browserName"] = "firefox", ["pageLoadStrategy"] = "eager",
                ["moz:firefoxOptions"] = new { binary = target.ExecutablePath,
                    args = new[] { "-profile", profile, "-no-remote" }, log = new { level = "fatal" } }
            };
            if (browserVersion.Major >= 141) { options["webSocketUrl"] = true; }
            var response = await transport.SendAsync(HttpMethod.Post, ["session"],
                ResponseJson.ToElement(new { capabilities = new { alwaysMatch = options } }), token);
            if (response.ValueKind != JsonValueKind.Object || !response.TryGetProperty("sessionId", out var id)
                || id.ValueKind != JsonValueKind.String || id.GetString() is not { Length: > 0 and <= 1024 } sessionId
                || sessionId.Any(char.IsControl) || !response.TryGetProperty("capabilities", out var caps)
                || caps.ValueKind != JsonValueKind.Object || ReadString(caps, "browserName") != "firefox"
                || !Version.TryParse(ReadString(caps, "browserVersion"), out var actualBrowser) || !EquivalentVersion(actualBrowser, browserVersion)
                || !Version.TryParse(ReadString(caps, "moz:geckodriverVersion"), out var actualDriver) || actualDriver != driverVersion
                || !string.Equals(ReadString(caps, "moz:profile"), profile, StringComparison.OrdinalIgnoreCase)
                || !caps.TryGetProperty("moz:processID", out var processId) || processId.ValueKind != JsonValueKind.Number
                || !processId.TryGetInt32(out var pid) || pid <= 0) { throw Incompatible(); }
            ApplicationReference browser;
            using (var reservation = applications.Reserve(token))
            {
                Process? process = null;
                try
                {
                    process = Process.GetProcessById(pid);
                    browser = reservation.Register(process, target, token);
                    process = null;
                }
                finally { process?.Dispose(); }
            }
            tree.VerifyRelationship(driver, browser, token);
            connection = new(new(BrowserKind.Firefox, actualBrowser.ToString(), driverVersion.ToString(), true),
                browser, driver, sessionId, transport, tree, BrowserHistoryTraversal.Endpoint(caps, sessionId));
            var handles = await connection.GetWindowHandlesAsync(token);
            if (handles.Count != 1) { throw LaunchFailed(); }
            await connection.ExecuteAsync(HttpMethod.Post, "window", ResponseJson.ToElement(new { handle = handles[0] }), token);
            if (url != "about:blank")
            { await connection.ExecuteAsync(HttpMethod.Post, "url", ResponseJson.ToElement(new { url }), token); }
            var registered = await sessions.RegisterAsync(connection, token);
            transferred = true;
            return ResponseJson.ToElement(new { browser = "firefox", browserId = registered.Browser.BrowserId,
                tabId = registered.Tabs[0].TabId, applicationId = browser.ApplicationId, processId = browser.ProcessId,
                browserVersion = actualBrowser.ToString(), driverVersion = driverVersion.ToString(),
                navigationCompleted = url != "about:blank", profile = "dedicated" });
        }
        catch (WindowReferenceException error) when (error.Code is ErrorCode.APP_NOT_FOUND or ErrorCode.APP_INSTANCE_UNRESOLVED
            or ErrorCode.SESSION_TARGET_UNRESOLVED) { throw LaunchFailed(); }
        catch (Exception error) when (error is ArgumentException or System.ComponentModel.Win32Exception) { throw LaunchFailed(); }
        finally
        {
            if (!transferred) { if (connection is not null) { connection.Dispose(); } else { transport.Dispose(); } }
        }
    }

    private static bool EquivalentVersion(Version first, Version second) => first.Major == second.Major
        && first.Minor == second.Minor && Math.Max(first.Build, 0) == Math.Max(second.Build, 0)
        && Math.Max(first.Revision, 0) == Math.Max(second.Revision, 0);
    private static WindowReferenceException Incompatible() => new(ErrorCode.BROWSER_DRIVER_INCOMPATIBLE);
}
