using System.ComponentModel;
using Kugutsu.Contracts;

namespace Kugutsu.App;

/// <summary>管理アプリの主ウィンドウへ通常終了を要求し、プロセス終了を確認する。</summary>
internal sealed class ApplicationExitOperation(ApplicationReferenceRegistry applications,
    WindowReferenceRegistry windows, WindowStateOperations states)
{
    internal OperationDefinition Definition() => new("app.exit",
        ArgumentSchema.Object([new("applicationId", ArgumentSchema.String(34, 34))]),
        async (args, token) => await ExitAsync(args.GetProperty("applicationId").GetString()!, token),
        args => ApplicationReferenceRegistry.IsValidId(args.GetProperty("applicationId").GetString()));

    internal async ValueTask<System.Text.Json.JsonElement> ExitAsync(string id, CancellationToken token)
    {
        var reference = applications.Resolve(id, token);
        try
        {
            if (applications.HasEnded(reference, token)) { return Completed(); }
            var process = applications.GetProcess(reference, token);
            process.Refresh();
            var handle = process.MainWindowHandle;
            if (applications.HasEnded(reference, token)) { return Completed(); }
            if (handle == 0) { throw new WindowReferenceException(ErrorCode.APP_EXIT_UNSUPPORTED); }
            var window = windows.Register(handle);
            if (window.Identity.ProcessId != reference.ProcessId || window.Identity.ProcessStartedAt != reference.ProcessStartedAt)
            { throw new WindowReferenceException(ErrorCode.APP_EXIT_UNSUPPORTED); }
            applications.Resolve(id, token);
            try { await states.CloseAsync(window.WindowId, token); }
            catch (WindowReferenceException exception) when (exception.Code == ErrorCode.WINDOW_NOT_FOUND
                && applications.HasEnded(reference, token)) { return Completed(); }
            while (!applications.HasEnded(reference, token)) { await Task.Delay(50, token); }
            token.ThrowIfCancellationRequested();
            return Completed();
        }
        catch (Win32Exception exception)
        { throw new WindowReferenceException(exception.NativeErrorCode == 5 ? ErrorCode.ACCESS_DENIED : ErrorCode.APP_EXIT_UNSUPPORTED); }
        catch (InvalidOperationException) { throw new WindowReferenceException(ErrorCode.APP_EXIT_UNSUPPORTED); }

        System.Text.Json.JsonElement Completed() => ResponseJson.ToElement(new { applicationId = id, exited = true });
    }
}
