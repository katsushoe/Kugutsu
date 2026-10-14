using Kugutsu.Contracts;

namespace Kugutsu.App;

/// <summary>許可されたアプリを起動し、同じプロセス世代の表示ウィンドウを待つ。</summary>
internal sealed class WindowOpenOperation(ApplicationOperations app, ApplicationReferenceRegistry applications,
    WindowDiscovery discovery, WindowReferenceRegistry windows, WindowStateOperations states)
{
    internal OperationDefinition Definition() => new("window.open",
        ArgumentSchema.Object([
            new("launchTargetId", ArgumentSchema.String(64, 1)),
            new("initialState", ArgumentSchema.String(9, 6, ["normal", "maximized"]), false)]),
        async (args, token) =>
        {
            var application = await app.LaunchAsync(args.GetProperty("launchTargetId").GetString()!, token);
            var maximize = args.TryGetProperty("initialState", out var initial) && initial.GetString() == "maximized";
            var info = await WaitAsync(application, maximize, token);
            return ResponseJson.ToElement(new { applicationId = application.ApplicationId,
                windowId = info.WindowId, processId = application.ProcessId, window = info });
        });

    internal async ValueTask<WindowInfo> WaitAsync(ApplicationReference application, bool maximize, CancellationToken token)
    {
            while (true)
            {
                token.ThrowIfCancellationRequested();
                VerifyApplication(application, token);
                var list = discovery.List(null, Path.GetFileNameWithoutExtension(application.ExecutablePath), token);
                var matches = list.Windows.Where(window => window.ProcessId == application.ProcessId && window.Visible).ToArray();
                if (list.Truncated || matches.Length > 1)
                { throw new WindowReferenceException(ErrorCode.WINDOW_LAUNCH_FAILED); }
                if (matches.Length == 1)
                {
                    VerifyWindow(matches[0].WindowId, application, token);
                    var info = maximize ? await states.MaximizeAsync(matches[0].WindowId, token)
                        : await states.RestoreAsync(matches[0].WindowId, token);
                    VerifyWindow(info.WindowId, application, token);
                    return info;
                }
                await Task.Delay(100, token);
            }
    }

    private void VerifyApplication(ApplicationReference application, CancellationToken token)
    {
        try
        {
            if (applications.Resolve(application.ApplicationId, token) != application)
            { throw new WindowReferenceException(ErrorCode.WINDOW_LAUNCH_FAILED); }
        }
        catch (WindowReferenceException exception) when (exception.Code is ErrorCode.APP_NOT_FOUND or ErrorCode.APP_INSTANCE_UNRESOLVED)
        { throw new WindowReferenceException(ErrorCode.WINDOW_LAUNCH_FAILED); }
    }

    private void VerifyWindow(string id, ApplicationReference application, CancellationToken token)
    {
        VerifyApplication(application, token);
        var reference = windows.Resolve(id);
        if (reference.Identity.ProcessId != application.ProcessId || reference.Identity.ProcessStartedAt != application.ProcessStartedAt)
        { throw new WindowReferenceException(ErrorCode.WINDOW_LAUNCH_FAILED); }
        VerifyApplication(application, token);
        token.ThrowIfCancellationRequested();
    }
}
