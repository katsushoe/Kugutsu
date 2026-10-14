using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using Kugutsu.Contracts;
using Microsoft.Extensions.Logging;

namespace Kugutsu.App;

/// <summary>信頼済みGeckoDriverの固定版照会を別プロセスで上限付き実行する。</summary>
internal sealed class BrowserDriverVersion(ILogger<BrowserDriverVersion> logger)
{
    internal async ValueTask<Version> ReadGeckoAsync(RegisteredLaunchTarget target, CancellationToken token)
    {
        using var process = new Process { StartInfo = new(target.ExecutablePath)
        {
            UseShellExecute = false, ErrorDialog = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
            WorkingDirectory = Path.GetDirectoryName(target.ExecutablePath)!
        } };
        process.StartInfo.ArgumentList.Add("--version");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(3));
        var started = false;
        try
        {
            token.ThrowIfCancellationRequested();
            if (!process.Start()) { throw Incompatible(); }
            started = true;
            var output = ReadBoundedAsync(process.StandardOutput.BaseStream, timeout.Token);
            var errors = ReadBoundedAsync(process.StandardError.BaseStream, timeout.Token);
            await Task.WhenAll(output, errors, process.WaitForExitAsync(timeout.Token));
            if (process.ExitCode != 0) { throw Incompatible(); }
            var match = Regex.Match(await output, @"\Ageckodriver (\d+\.\d+\.\d+)(?: |\r|\n|$)",
                RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
            if (!match.Success || !Version.TryParse(match.Groups[1].Value, out var version)) { throw Incompatible(); }
            return version;
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { throw Incompatible(); }
        catch (Exception error) when (error is Win32Exception or IOException or RegexMatchTimeoutException)
        { throw Incompatible(); }
        finally
        {
            if (started)
            {
                try
                {
                    if (!process.HasExited)
                    {
                        process.Kill(false); // この固定版照会のためだけに起動した補助プロセス。
                        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(2));
                    }
                }
                catch (Exception error) when (error is Win32Exception or InvalidOperationException or TimeoutException)
                { logger.LogWarning("[BrowserDriverVersion] Probe cleanup failed."); }
            }
        }
    }

    private static async Task<string> ReadBoundedAsync(Stream stream, CancellationToken token)
    {
        using var memory = new MemoryStream();
        var buffer = new byte[1024];
        int count;
        while ((count = await stream.ReadAsync(buffer, token)) != 0)
        {
            if (memory.Length + count > 8192) { throw Incompatible(); }
            memory.Write(buffer, 0, count);
        }
        return Encoding.UTF8.GetString(memory.ToArray());
    }

    private static WindowReferenceException Incompatible() => new(ErrorCode.BROWSER_DRIVER_INCOMPATIBLE);
}
