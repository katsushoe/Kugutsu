using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Kugutsu.Contracts;
using Kugutsu.Hosting;

namespace Kugutsu.App;

/// <summary>許可済みChromiumブラウザと専用WebDriverを新しいプロファイルで起動する。</summary>
internal sealed partial class BrowserLaunchOperation(KugutsuOptions settings, InstallationPaths paths,
    LaunchTargetRegistry targets, BrowserDriverFiles drivers, ApplicationReferenceRegistry applications,
    ManagedProcessTree tree, BrowserSessionRegistry sessions, BrowserDriverVersion driverVersions)
{
    internal OperationDefinition Definition() => new("browser.launch", ArgumentSchema.Object([
        new("browser", ArgumentSchema.String(7, 4, ["chrome", "edge", "firefox"])),
        new("url", ArgumentSchema.String(8192, 1), false)]),
        async (args, token) =>
        {
            var kind = BrowserSessionRegistry.Parse(args.GetProperty("browser").GetString()!);
            var url = args.TryGetProperty("url", out var value) ? value.GetString()! : "about:blank";
            return kind == BrowserKind.Firefox ? await LaunchFirefoxAsync(url, token) : await LaunchChromiumAsync(kind, url, token);
        }, args => !args.TryGetProperty("url", out var url) || BrowserSessionRegistry.ValidUrl(url.GetString()));

    private async ValueTask<JsonElement> LaunchChromiumAsync(BrowserKind kind, string url, CancellationToken token)
    {
        var name = BrowserSessionRegistry.Name(kind);
        var edge = kind == BrowserKind.Edge;
        if (!settings.BrowserLaunchTargets.TryGetValue(name, out var targetId) || !targets.IsRegistered(targetId))
        { throw new WindowReferenceException(ErrorCode.BROWSER_NOT_INSTALLED); }
        if (!drivers.IsRegistered(kind)) { throw new WindowReferenceException(ErrorCode.BROWSER_DRIVER_INCOMPATIBLE); }
        using var browserLease = await targets.ResolveAsync(targetId, token);
        using var driverLease = await drivers.ResolveAsync(kind, token);
        var target = browserLease.Target;
        if (!Path.GetFileName(target.ExecutablePath).Equals(edge ? "msedge.exe" : "chrome.exe", StringComparison.OrdinalIgnoreCase) || target.Arguments.Count != 0)
        { throw new WindowReferenceException(ErrorCode.BROWSER_DRIVER_INCOMPATIBLE); }
        if (!Path.GetFileName(driverLease.Target.ExecutablePath).Equals(edge ? "msedgedriver.exe" : "chromedriver.exe", StringComparison.OrdinalIgnoreCase))
        { throw new WindowReferenceException(ErrorCode.BROWSER_DRIVER_INCOMPATIBLE); }
        var browserVersion = ReadVersion(target.ExecutablePath);
        var driverVersion = ReadVersion(driverLease.Target.ExecutablePath);
        if (browserVersion.Major < (edge ? 79 : 115) || !SameBuild(browserVersion, driverVersion))
        { throw new WindowReferenceException(ErrorCode.BROWSER_DRIVER_INCOMPATIBLE); }
        var profile = CreateProfile(token);
        var port = ReservePort();
        var driver = await drivers.LaunchAsync(kind,
            [$"--port={port}", "--allowed-ips=127.0.0.1", "--log-level=OFF"], token);
        var transport = new WebDriverTransport(driver, port, applications);
        WebDriverConnection? connection = null;
        var transferred = false;
        try
        {
            await WaitForDriverAsync(transport, driver, token);
            var options = new Dictionary<string, object>
            {
                ["browserName"] = edge ? "MicrosoftEdge" : "chrome", ["pageLoadStrategy"] = "eager",
                [edge ? "ms:edgeOptions" : "goog:chromeOptions"] = new { binary = target.ExecutablePath,
                    args = new[] { "--user-data-dir=" + profile, "--no-first-run", "--no-default-browser-check", "--remote-debugging-pipe" } }
            };
            if (browserVersion.Major >= 126) { options["webSocketUrl"] = true; }
            var response = await transport.SendAsync(HttpMethod.Post, ["session"],
                ResponseJson.ToElement(new { capabilities = new { alwaysMatch = options } }), token);
            if (response.ValueKind != JsonValueKind.Object || !response.TryGetProperty("sessionId", out var id)
                || id.ValueKind != JsonValueKind.String || id.GetString() is not { Length: > 0 and <= 1024 } sessionId
                || !response.TryGetProperty("capabilities", out var capabilities)) { throw LaunchFailed(); }
            if (sessionId.Any(char.IsControl)) { throw LaunchFailed(); }
            var pid = VerifyCapabilities(kind, capabilities, browserVersion, driverVersion, profile);
            ApplicationReference browser;
            if (pid is null)
            {
                browser = await tree.FindAsync(driver, target, token, process =>
                { process.Refresh(); return process.MainWindowHandle != 0; });
            }
            else
            {
                using var reservation = applications.Reserve(token);
                Process? process = null;
                try
                {
                    process = Process.GetProcessById(pid.Value);
                    browser = reservation.Register(process, target, token);
                    process = null;
                }
                finally { process?.Dispose(); }
            }
            tree.VerifyRelationship(driver, browser, token);
            connection = new(new(kind, browserVersion.ToString(), driverVersion.ToString(), true),
                browser, driver, sessionId, transport, tree, BrowserHistoryTraversal.Endpoint(capabilities, sessionId));
            var handles = await connection.GetWindowHandlesAsync(token);
            if (handles.Count != 1) { throw LaunchFailed(); }
            await connection.ExecuteAsync(HttpMethod.Post, "window", ResponseJson.ToElement(new { handle = handles[0] }), token);
            if (url != "about:blank")
            { await connection.ExecuteAsync(HttpMethod.Post, "url", ResponseJson.ToElement(new { url }), token); }
            var registered = await sessions.RegisterAsync(connection, token);
            transferred = true;
            return ResponseJson.ToElement(new { browser = name, browserId = registered.Browser.BrowserId,
                tabId = registered.Tabs[0].TabId, applicationId = browser.ApplicationId, processId = browser.ProcessId,
                browserVersion = browserVersion.ToString(), driverVersion = driverVersion.ToString(),
                navigationCompleted = url != "about:blank", profile = "dedicated" });
        }
        catch (WindowReferenceException error) when (error.Code is ErrorCode.APP_NOT_FOUND or ErrorCode.APP_INSTANCE_UNRESOLVED
            or ErrorCode.SESSION_TARGET_UNRESOLVED) { throw LaunchFailed(); }
        catch (Exception error) when (error is ArgumentException or System.ComponentModel.Win32Exception)
        { throw LaunchFailed(); }
        finally
        {
            // 起動後の不明な状態で別プロセスを終了せず、プロファイルも削除しない。
            if (!transferred) { if (connection is not null) { connection.Dispose(); } else { transport.Dispose(); } }
        }
    }

    private static int? VerifyCapabilities(BrowserKind kind, JsonElement capabilities, Version browserVersion, Version driverVersion, string profile)
    {
        var edge = kind == BrowserKind.Edge;
        if (capabilities.ValueKind != JsonValueKind.Object || ReadString(capabilities, "browserName") != (edge ? "MicrosoftEdge" : "chrome")
            || !Version.TryParse(ReadString(capabilities, "browserVersion"), out var actualBrowser) || actualBrowser != browserVersion
            || !capabilities.TryGetProperty(edge ? "msedge" : "chrome", out var details) || details.ValueKind != JsonValueKind.Object
            || !Version.TryParse(ReadString(details, edge ? "msedgedriverVersion" : "chromedriverVersion")?.Split(' ')[0], out var actualDriver)
            || actualDriver != driverVersion || !string.Equals(ReadString(details, "userDataDir"), profile, StringComparison.OrdinalIgnoreCase))
        { throw new WindowReferenceException(ErrorCode.BROWSER_DRIVER_INCOMPATIBLE); }
        if (edge) { return null; }
        if (!capabilities.TryGetProperty("goog:processID", out var processId) || processId.ValueKind != JsonValueKind.Number
            || !processId.TryGetInt32(out var pid) || pid <= 0)
        { throw new WindowReferenceException(ErrorCode.BROWSER_DRIVER_INCOMPATIBLE); }
        return pid;
    }

    private static string? ReadString(JsonElement value, string name) => value.TryGetProperty(name, out var item)
        && item.ValueKind == JsonValueKind.String ? item.GetString() : null;

    private static Version ReadVersion(string path)
    {
        var value = FileVersionInfo.GetVersionInfo(path).FileVersion;
        if (!Version.TryParse(value?.Split(' ')[0], out var version) || version.Build < 0 || version.Revision < 0)
        { throw new WindowReferenceException(ErrorCode.BROWSER_DRIVER_INCOMPATIBLE); }
        return version;
    }

    private static bool SameBuild(Version first, Version second) => first.Major == second.Major
        && first.Minor == second.Minor && first.Build == second.Build;

    private string CreateProfile(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var parent = Path.Combine(paths.DataDirectory, "browser-profiles");
        EnsureNormalDirectory(paths.RootDirectory);
        EnsureNormalDirectory(paths.DataDirectory);
        Directory.CreateDirectory(parent);
        EnsureNormalDirectory(parent);
        var profile = Path.Combine(parent, Guid.NewGuid().ToString("N"));
        if (Directory.Exists(profile) || File.Exists(profile)) { throw LaunchFailed(); }
        Directory.CreateDirectory(profile);
        EnsureNormalDirectory(profile);
        return profile;
    }

    private static void EnsureNormalDirectory(string path)
    {
        string? current = path;
        while (current is not null)
        {
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) { throw LaunchFailed(); }
            current = Path.GetDirectoryName(current);
        }
    }

    private static int ReservePort()
    {
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)socket.LocalEndPoint!).Port;
    }

    private async ValueTask WaitForDriverAsync(WebDriverTransport transport, ApplicationReference driver, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        try
        {
            while (true)
            {
                timeout.Token.ThrowIfCancellationRequested();
                applications.Resolve(driver.ApplicationId, timeout.Token);
                try { transport.Verify(timeout.Token); return; }
                catch (WindowReferenceException error) when (error.Code == ErrorCode.BROWSER_CONNECTION_LOST)
                { await Task.Delay(50, timeout.Token); }
            }
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { throw LaunchFailed(); }
    }

    private static WindowReferenceException LaunchFailed() => new(ErrorCode.BROWSER_LAUNCH_FAILED);
}
