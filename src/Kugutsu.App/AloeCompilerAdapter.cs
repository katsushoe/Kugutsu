using System.ComponentModel;
using System.Diagnostics;
using Kugutsu.Contracts;
using Kugutsu.Hosting;
using Microsoft.Extensions.Logging;

namespace Kugutsu.App;

internal sealed class AloeCompiledSession(Process process, AloeDependencyLease assets, Task drain, AloeProcessJob job,
    int instructions, int functions, ILogger logger) : IAsyncDisposable
{
    internal Process Process { get; } = process;
    internal int InstructionCount { get; } = instructions;
    internal int FunctionCount { get; } = functions;
    internal Stream Input => Process.StandardInput.BaseStream;
    internal Stream Output => Process.StandardOutput.BaseStream;
    private int _disposed;
    private int _executionStarted;
    internal void BeginExecution()
    {
        if (Volatile.Read(ref _disposed) != 0 || Interlocked.Exchange(ref _executionStarted, 1) != 0)
        { throw new WindowReferenceException(ErrorCode.ALOE_RUNTIME_ERROR); }
    }
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) { return; }
        job.Dispose();
        await StopAsync(Process, logger);
        try { await drain; }
        finally { assets.Dispose(); Process.Dispose(); }
    }
    internal static async ValueTask StopAsync(Process process, ILogger logger)
    {
        try { if (!process.HasExited) { process.Kill(entireProcessTree: true); } }
        catch (InvalidOperationException) { logger.LogDebug("Aloe helper already stopped."); }
        catch (Win32Exception) { logger.LogError("Aloe helper termination unavailable; waiting for confirmed exit."); }
        await process.WaitForExitAsync(CancellationToken.None);
    }
}

internal sealed class AloeCompilerAdapter(AloeDependencies dependencies, InstallationPaths paths,
    KugutsuOptions settings, ILogger<AloeCompilerAdapter> logger)
{
    internal async ValueTask<AloeCompiledSession> CompileAsync(string source, CancellationToken token)
    {
        if (source.Length is < 1 or > 65536 || !KeyboardOperations.ValidText(source))
        { throw new WindowReferenceException(ErrorCode.INVALID_ARGUMENT); }
        AloeDependencyLease assets;
        try { assets = dependencies.OpenVerified(token); }
        catch (AloeDependencyException) { throw new WindowReferenceException(ErrorCode.ALOE_UNAVAILABLE); }
        Process? process = null;
        Task? drain = null;
        AloeProcessJob? job = null;
        var started = false;
        try
        {
            var helper = Path.Combine(paths.BinDirectory, "Kugutsu.AloeHost.exe");
            for (string? ancestor = helper; ancestor is not null; ancestor = Path.GetDirectoryName(ancestor))
            {
                if ((File.GetAttributes(ancestor) & FileAttributes.ReparsePoint) != 0)
                { throw new WindowReferenceException(ErrorCode.ALOE_UNAVAILABLE); }
            }
            var start = new ProcessStartInfo(helper) { UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
                WorkingDirectory = paths.BinDirectory };
            start.ArgumentList.Add("--worker");
            process = new Process { StartInfo = start };
            token.ThrowIfCancellationRequested();
            if (!process.Start()) { throw new WindowReferenceException(ErrorCode.ALOE_UNAVAILABLE); }
            started = true;
            job = new AloeProcessJob(process, settings.AloeMemoryLimitMiB);
            drain = DrainAsync(process.StandardError.BaseStream);
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(TimeSpan.FromSeconds(settings.OperationTimeoutSeconds));
            try
            {
                await AloeWire.WriteAsync(process.StandardInput.BaseStream, new { command = "compile", source }, deadline.Token);
                var response = await AloeWire.ReadAsync(process.StandardOutput.BaseStream, deadline.Token);
                if (response.GetProperty("status").GetString() == "error")
                {
                    var code = response.GetProperty("code").GetString();
                    throw new WindowReferenceException(code == "ALOE_COMPILE_ERROR" ? ErrorCode.ALOE_COMPILE_ERROR
                        : code == "ALOE_UNAVAILABLE" ? ErrorCode.ALOE_UNAVAILABLE : ErrorCode.INTERNAL_ERROR,
                        System.Text.Json.JsonSerializer.SerializeToElement(new { stage = "compile" }));
                }
                if (response.GetProperty("status").GetString() != "compiled") { throw new InvalidDataException(); }
                var instructionCount = response.GetProperty("instructionCount").GetInt32();
                var functionCount = response.GetProperty("functionCount").GetInt32();
                if (instructionCount < 0 || functionCount <= 0) { throw new InvalidDataException(); }
                var session = new AloeCompiledSession(process, assets, drain, job, instructionCount, functionCount, logger);
                process = null;
                job = null;
                return session;
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested)
            { throw new WindowReferenceException(ErrorCode.TIMEOUT); }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or Win32Exception or System.Text.Json.JsonException)
        { throw new WindowReferenceException(ErrorCode.ALOE_UNAVAILABLE); }
        finally
        {
            if (process is not null)
            {
                job?.Dispose();
                if (started) { await AloeCompiledSession.StopAsync(process, logger); if (drain is not null) { await drain; } }
                process.Dispose();
            }
            if (process is not null || !started) { assets.Dispose(); }
        }
    }
    private async Task DrainAsync(Stream stream)
    {
        var buffer = new byte[1024];
        try { while (await stream.ReadAsync(buffer, CancellationToken.None) != 0) { } }
        catch (IOException) { logger.LogDebug("Aloe helper error channel closed."); }
    }
}
