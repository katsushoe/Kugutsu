using System.Globalization;
using Kugutsu.Contracts;
using Kugutsu.Hosting;

namespace Kugutsu.App;

internal sealed class TerminalLaunchOperation(KugutsuOptions settings, LaunchTargetRegistry targets,
    ApplicationOperations app, WindowOpenOperation open, WindowReferenceRegistry windows, ManagedProcessTree tree,
    ShellSessionRegistry sessions, UiDriverRegistry drivers)
{
    internal OperationDefinition Definition() => new("terminal.launch", ArgumentSchema.Object([]),
        async (_, token) =>
        {
            if (settings.TerminalLaunchTargetId is not { } terminalId || !targets.IsRegistered(terminalId))
            { throw new WindowReferenceException(ErrorCode.TERMINAL_NOT_INSTALLED); }
            if (settings.PowerShellLaunchTargetId is not { } shellId || !targets.IsRegistered(shellId))
            { throw new WindowReferenceException(ErrorCode.POWERSHELL_NOT_FOUND); }
            if (drivers.Resolve("uia") is not UiaDriver uia)
            { throw new WindowReferenceException(ErrorCode.UI_DRIVER_UNAVAILABLE); }
            using var shellLease = await ResolveShellAsync(shellId, token);
            var flavor = PowerShellTargetInfo.Read(shellLease.Target);
            if (shellLease.Target.ExecutablePath.Contains(';') || shellLease.Target.Arguments.Any(argument => argument.Contains(';')))
            { throw new WindowReferenceException(ErrorCode.TERMINAL_LAUNCH_FAILED); }
            var marker = "Kugutsu_" + Guid.NewGuid().ToString("N");
            ApplicationReference application;
            try
            {
                application = await app.LaunchAsync(terminalId, token, target =>
                {
                    if (target.Arguments.Count != 0 || Path.GetFileName(target.ExecutablePath).ToLowerInvariant() is not
                        ("windowsterminal.exe" or "wt.exe" or "wtd.exe"))
                    { throw new WindowReferenceException(ErrorCode.TERMINAL_LAUNCH_FAILED); }
                    return new[] { "-w", "new", "new-tab", "--title", marker, "--suppressApplicationTitle", shellLease.Target.ExecutablePath }
                        .Concat(shellLease.Target.Arguments).ToArray();
                });
            }
            catch (WindowReferenceException exception) when (exception.Code == ErrorCode.APP_NOT_FOUND)
            { throw new WindowReferenceException(ErrorCode.TERMINAL_NOT_INSTALLED); }
            catch (WindowReferenceException exception) when (exception.Code is ErrorCode.APP_LAUNCH_FAILED or ErrorCode.APP_INSTANCE_UNRESOLVED)
            { throw new WindowReferenceException(ErrorCode.TERMINAL_LAUNCH_FAILED); }
            var info = await open.WaitAsync(application, false, token);
            var window = windows.Resolve(info.WindowId);
            var shell = await tree.FindAsync(application, shellLease.Target, token);
            var binding = await TerminalShellBinding.CreateAsync(uia, tree, marker, application, shell, window, flavor, token);
            ShellSessionReference session;
            try { session = await sessions.RegisterAsync(binding, token); }
            catch { binding.Dispose(); throw; }
            return ResponseJson.ToElement(new { sessionId = session.SessionId, applicationId = application.ApplicationId,
                windowId = window.WindowId, processId = application.ProcessId, shellProcessId = shell.ProcessId,
                shellProcessStartedAt = DateTime.FromFileTimeUtc(shell.ProcessStartedAt).ToString("O", CultureInfo.InvariantCulture),
                powerShellKind = flavor == PowerShellFlavor.WindowsPowerShell ? "windows_powershell" : "powershell7", inputVerified = true });
        });

    private async ValueTask<LaunchTargetLease> ResolveShellAsync(string id, CancellationToken token)
    {
        try { return await targets.ResolveAsync(id, token); }
        catch (WindowReferenceException exception) when (exception.Code == ErrorCode.APP_NOT_FOUND)
        { throw new WindowReferenceException(ErrorCode.POWERSHELL_NOT_FOUND); }
    }
}
