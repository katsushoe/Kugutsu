using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using Kugutsu.Contracts;

namespace Kugutsu.App;

internal sealed class ApplicationOperations(LaunchTargetRegistry targets, ApplicationReferenceRegistry applications)
{
    internal OperationDefinition LaunchDefinition() => new("app.launch",
        ArgumentSchema.Object([new("launchTargetId", ArgumentSchema.String(64, 1))]),
        async (args, token) => ToResult(await LaunchAsync(args.GetProperty("launchTargetId").GetString()!, token)));

    internal async ValueTask<ApplicationReference> LaunchAsync(string targetId, CancellationToken token,
        Func<RegisteredLaunchTarget, IReadOnlyList<string>>? argumentFactory = null, bool createNoWindow = false)
    {
        Process? process = null;
        try
        {
            using var reservation = applications.Reserve(token);
            using var lease = await targets.ResolveAsync(targetId, token);
            var start = new ProcessStartInfo(lease.Target.ExecutablePath)
            {
                UseShellExecute = false, ErrorDialog = false, CreateNoWindow = createNoWindow,
                RedirectStandardOutput = createNoWindow, RedirectStandardError = createNoWindow,
                WorkingDirectory = Path.GetDirectoryName(lease.Target.ExecutablePath)!
            };
            foreach (var argument in argumentFactory?.Invoke(lease.Target) ?? lease.Target.Arguments) { start.ArgumentList.Add(argument); }
            token.ThrowIfCancellationRequested();
            process = Process.Start(start) ?? throw new WindowReferenceException(ErrorCode.APP_LAUNCH_FAILED);
            if (createNoWindow) { process.BeginOutputReadLine(); process.BeginErrorReadLine(); }
            // 短命なランチャーや単一起動転送を既存アプリへ推測で関連付けない。
            await Task.Delay(250, token);
            var reference = reservation.Register(process, lease.Target, token);
            process = null; // プロセスハンドルの所有権を参照レジストリへ移す。
            return reference;
        }
        catch (Win32Exception exception)
        { throw new WindowReferenceException(exception.NativeErrorCode == 5 ? ErrorCode.ACCESS_DENIED : ErrorCode.APP_LAUNCH_FAILED); }
        catch (UnauthorizedAccessException) { throw new WindowReferenceException(ErrorCode.ACCESS_DENIED); }
        catch (System.Security.SecurityException) { throw new WindowReferenceException(ErrorCode.ACCESS_DENIED); }
        catch (InvalidOperationException) { throw new WindowReferenceException(ErrorCode.APP_INSTANCE_UNRESOLVED); }
        finally { process?.Dispose(); }
    }

    internal static System.Text.Json.JsonElement ToResult(ApplicationReference reference) => ResponseJson.ToElement(new
    {
        applicationId = reference.ApplicationId, launchTargetId = reference.LaunchTargetId,
        processId = reference.ProcessId, processStartedAt = DateTime.FromFileTimeUtc(reference.ProcessStartedAt).ToString("O", CultureInfo.InvariantCulture)
    });
}
