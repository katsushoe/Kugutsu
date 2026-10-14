using System.Globalization;
using Kugutsu.Contracts;
using Kugutsu.Hosting;

namespace Kugutsu.App;

internal sealed class PowerShellLaunchOperation(KugutsuOptions settings, LaunchTargetRegistry targets,
    ApplicationOperations app, WindowOpenOperation open, WindowReferenceRegistry windows, ManagedProcessTree tree,
    ShellSessionRegistry sessions, ConsoleAssociationProbe probe, WindowStateOperations states)
{
    internal OperationDefinition Definition() => new("powershell.launch", ArgumentSchema.Object([]),
        async (_, token) =>
        {
            if (settings.PowerShellLaunchTargetId is not { } shellId || !targets.IsRegistered(shellId))
            { throw new WindowReferenceException(ErrorCode.POWERSHELL_NOT_FOUND); }
            if (settings.ConsoleHostLaunchTargetId is not { } hostId || !targets.IsRegistered(hostId))
            { throw new WindowReferenceException(ErrorCode.CONSOLE_HOST_NOT_FOUND); }
            using var lease = await ResolveShellAsync(shellId, token);
            var flavor = PowerShellTargetInfo.Read(lease.Target);
            probe.EnsureAvailable(token);
            ApplicationReference host;
            try
            {
                host = await app.LaunchAsync(hostId, token, target =>
                {
                    if (target.Arguments.Count != 0 || !Path.GetFileName(target.ExecutablePath).Equals("conhost.exe", StringComparison.OrdinalIgnoreCase))
                    { throw new WindowReferenceException(ErrorCode.SESSION_TARGET_UNRESOLVED); }
                    var args = new List<string>();
                    if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000)) { args.Add("-ForceNoHandoff"); }
                    args.Add("--"); args.Add(lease.Target.ExecutablePath); args.AddRange(lease.Target.Arguments);
                    return args;
                });
            }
            catch (WindowReferenceException error) when (error.Code == ErrorCode.APP_NOT_FOUND)
            { throw new WindowReferenceException(ErrorCode.CONSOLE_HOST_NOT_FOUND); }
            var info = await open.WaitAsync(host, false, token);
            var window = windows.Resolve(info.WindowId);
            var shell = await tree.FindAsync(host, lease.Target, token);
            var binding = new ConsoleShellBinding(host, shell, window, flavor, tree, probe, states);
            ShellSessionReference session;
            try { session = await sessions.RegisterAsync(binding, token); }
            catch { binding.Dispose(); throw; }
            return ResponseJson.ToElement(new { sessionId = session.SessionId, applicationId = shell.ApplicationId,
                windowId = window.WindowId, processId = shell.ProcessId, shellProcessId = shell.ProcessId,
                shellProcessStartedAt = DateTime.FromFileTimeUtc(shell.ProcessStartedAt).ToString("O", CultureInfo.InvariantCulture),
                powerShellKind = flavor == PowerShellFlavor.WindowsPowerShell ? "windows_powershell" : "powershell7", inputVerified = true });
        });

    private async ValueTask<LaunchTargetLease> ResolveShellAsync(string id, CancellationToken token)
    {
        try { return await targets.ResolveAsync(id, token); }
        catch (WindowReferenceException error) when (error.Code == ErrorCode.APP_NOT_FOUND)
        { throw new WindowReferenceException(ErrorCode.POWERSHELL_NOT_FOUND); }
    }
}
