using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Kugutsu.Contracts;
using Kugutsu.Hosting;
using Microsoft.Extensions.Logging;

namespace Kugutsu.App;

internal sealed class ConsoleAssociationProbe(ManagedProcessTree tree, ILogger<ConsoleAssociationProbe> logger)
{
    internal void EnsureAvailable(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var path = Path.Combine(AppContext.BaseDirectory, "Kugutsu.ConsoleProbe.exe");
        if (!File.Exists(path) || (File.GetAttributes(path) & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
        { throw new WindowReferenceException(ErrorCode.SESSION_TARGET_UNRESOLVED); }
    }

    internal async ValueTask VerifyAsync(ApplicationReference shell, WindowReference window, CancellationToken token)
    {
        EnsureAvailable(token);
        var path = Path.Combine(AppContext.BaseDirectory, "Kugutsu.ConsoleProbe.exe");
        var start = new ProcessStartInfo(path) { UseShellExecute = false, ErrorDialog = false, CreateNoWindow = true,
            RedirectStandardOutput = true, WorkingDirectory = AppContext.BaseDirectory };
        start.ArgumentList.Add(shell.ProcessId.ToString(CultureInfo.InvariantCulture));
        start.ArgumentList.Add(shell.ProcessStartedAt.ToString(CultureInfo.InvariantCulture));
        var expectedWindow = unchecked((ulong)(nuint)window.Identity.Handle);
        start.ArgumentList.Add(expectedWindow.ToString(CultureInfo.InvariantCulture));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, timeout.Token);
        Process? process = null;
        try
        {
            token.ThrowIfCancellationRequested();
            process = Process.Start(start) ?? throw new WindowReferenceException(ErrorCode.SESSION_TARGET_UNRESOLVED);
            using var output = new MemoryStream();
            var buffer = new byte[1024];
            int count;
            while ((count = await process.StandardOutput.BaseStream.ReadAsync(buffer, linked.Token)) > 0)
            {
                if (output.Length + count > 16384) { throw new WindowReferenceException(ErrorCode.SESSION_TARGET_UNRESOLVED); }
                output.Write(buffer, 0, count);
            }
            await process.WaitForExitAsync(linked.Token);
            if (process.ExitCode != 0) { throw new WindowReferenceException(ErrorCode.SESSION_TARGET_UNRESOLVED); }
            using var document = JsonDocument.Parse(output.ToArray(), new JsonDocumentOptions { MaxDepth = 4 });
            var data = document.RootElement;
            if (data.ValueKind != JsonValueKind.Object || data.EnumerateObject().Count() != 5
                || data.EnumerateObject().Select(property => property.Name).Distinct(StringComparer.Ordinal).Count() != 5
                || data.GetProperty("processId").GetInt32() != shell.ProcessId
                || data.GetProperty("processStartedAt").GetInt64() != shell.ProcessStartedAt
                || data.GetProperty("windowHandle").GetUInt64() != expectedWindow
                || data.GetProperty("probeProcessId").GetInt32() != process.Id)
            { throw new WindowReferenceException(ErrorCode.SESSION_TARGET_UNRESOLVED); }
            var ids = data.GetProperty("processIds").EnumerateArray().Select(id => id.GetUInt32()).ToArray();
            if (ids.Length is < 2 or > 1024 || !ids.Contains((uint)shell.ProcessId) || !ids.Contains((uint)process.Id)
                || ids.Distinct().Count() != ids.Length)
            { throw new WindowReferenceException(ErrorCode.SESSION_TARGET_UNRESOLVED); }
            tree.VerifyConsoleMembers(shell, ids, process.Id, token);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        { throw new WindowReferenceException(ErrorCode.SESSION_TARGET_UNRESOLVED); }
        catch (Exception error) when (error is Win32Exception or IOException or JsonException or InvalidOperationException
            or KeyNotFoundException or FormatException or OverflowException)
        { throw new WindowReferenceException(ErrorCode.SESSION_TARGET_UNRESOLVED); }
        finally
        {
            if (process is not null)
            {
                try
                {
                    if (!process.HasExited) { process.Kill(); await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(2)); }
                }
                catch (Exception error) when (error is Win32Exception or InvalidOperationException or TimeoutException)
                { logger.WriteEvent(LogLevel.Warning, LogEvent.OperationCompleted, LogOutcome.Failed,
                    operation: LogOperation.PowerShell, error: LogError.OperationFailed); }
                finally { process.Dispose(); }
            }
        }
    }
}
