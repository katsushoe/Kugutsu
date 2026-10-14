using System.ComponentModel;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using Kugutsu.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Win32.SafeHandles;

namespace Kugutsu.ConsoleProbe;

/// <summary>短命な別プロセスでコンソールの対応関係だけを確認する。入力・終了要求は行わない。</summary>
internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        using var provider = new StructuredLogProvider(LogComponent.ConsoleProbe,
            static line => System.Diagnostics.Trace.WriteLine(line), static () => Environment.Exit(1));
        using var factory = LoggerFactory.Create(builder => builder.SetMinimumLevel(LogLevel.Trace).AddProvider(provider));
        var logger = factory.CreateLogger("Kugutsu.ConsoleProbe");
        AppDomain.CurrentDomain.UnhandledException += (_, _) =>
        {
            logger.WriteEvent(LogLevel.Error, LogEvent.UnhandledFailure, LogOutcome.Failed, error: LogError.UnexpectedFailure);
            Environment.Exit(1);
        };
        TaskScheduler.UnobservedTaskException += (_, error) =>
        {
            error.SetObserved();
            logger.WriteEvent(LogLevel.Error, LogEvent.UnhandledFailure, LogOutcome.Failed, error: LogError.UnexpectedFailure);
            Environment.Exit(1);
        };
        try
        {
            var paths = InstallationPaths.FromExecutableDirectory(AppContext.BaseDirectory);
            provider.OpenFile(paths);
            provider.ApplySettings(SettingsLoader.Load(paths));
            logger.WriteEvent(LogLevel.Information, LogEvent.Startup, LogOutcome.Started);
            if (args.Length != 3 || !uint.TryParse(args[0], NumberStyles.None, CultureInfo.InvariantCulture, out var pid) || pid == 0
                || !long.TryParse(args[1], NumberStyles.None, CultureInfo.InvariantCulture, out var started) || started <= 0
                || !ulong.TryParse(args[2], NumberStyles.None, CultureInfo.InvariantCulture, out var window) || window == 0)
            { throw new ArgumentException("Invalid probe arguments."); }
            await using var output = Console.OpenStandardOutput();
            if (GetConsoleWindow() != 0) { throw new InvalidOperationException("Probe already has a console."); }
            using var process = OpenProcess(0x1000, false, pid);
            if (process.IsInvalid || !GetProcessTimes(process, out var actual, out _, out _, out _) || actual != started)
            { throw new InvalidOperationException("Probe target changed."); }
            if (!AttachConsole(pid)) { throw new Win32Exception(); }
            uint[] clients;
            try
            {
                if (unchecked((ulong)(nuint)GetConsoleWindow()) != window)
                { throw new InvalidOperationException("Console window differs."); }
                var ids = new uint[1024];
                var count = GetConsoleProcessList(ids, (uint)ids.Length);
                if (count is 0 or > 1024 || !GetProcessTimes(process, out actual, out _, out _, out _) || actual != started)
                { throw new InvalidOperationException("Console association unavailable."); }
                clients = ids[..(int)count];
                if (!clients.Contains(pid) || !clients.Contains((uint)Environment.ProcessId) || clients.Any(id => id == 0)
                    || clients.Distinct().Count() != clients.Length)
                { throw new InvalidOperationException("Console association differs."); }
            }
            finally { if (!FreeConsole()) { throw new Win32Exception(); } }
            await JsonSerializer.SerializeAsync(output, new { processId = pid, processStartedAt = started,
                windowHandle = window, probeProcessId = Environment.ProcessId, processIds = clients });
            await output.FlushAsync();
            logger.WriteEvent(LogLevel.Information, LogEvent.Shutdown, LogOutcome.Succeeded);
            return 0;
        }
        catch (Exception error)
        {
            logger.WriteEvent(LogLevel.Error, LogEvent.OperationCompleted, LogOutcome.Failed, error: AuditLog.Classify(error));
            logger.WriteEvent(LogLevel.Error, LogEvent.Shutdown, LogOutcome.Failed);
            return 1;
        }
    }

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint pid);
    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessTimes(SafeProcessHandle process, out long created, out long exited, out long kernel, out long user);
    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AttachConsole(uint processId);
    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FreeConsole();
    [DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern nint GetConsoleWindow();
    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    private static extern uint GetConsoleProcessList([Out] uint[] processIds, uint count);
}
