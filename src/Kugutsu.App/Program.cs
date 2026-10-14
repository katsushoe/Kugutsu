using Microsoft.Extensions.Logging;

using Kugutsu.Hosting;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace Kugutsu.App;

internal static class Program
{
    private static int _failureReported;

    [STAThread]
    private static async Task<int> Main(string[] args)
    {
        if (args.Length == 1 && args[0] == "--ensure-mcp-credential")
        {
            try { CredentialLoader.EnsureCreated(); return 0; }
            catch (Exception) { return 1; }
        }
        if (args.Length == 1 && args[0] == "--detect-launch-targets")
        {
            try
            {
                var paths = InstallationPaths.FromExecutableDirectory(AppContext.BaseDirectory);
                LaunchTargetDetector.WriteConfiguration(paths);
                return 0;
            }
            catch (Exception) { return 1; }
        }
        if (args.Length == 1 && args[0] == "--install-webdrivers")
        {
            try
            {
                var paths = InstallationPaths.FromExecutableDirectory(AppContext.BaseDirectory);
                return await WebDriverInstaller.RunAsync(paths);
            }
            catch (Exception) { return 1; }
        }
        if (args.Length == 1 && args[0] == "--cleanup-setup-artifacts")
        {
            try
            {
                var paths = InstallationPaths.FromExecutableDirectory(AppContext.BaseDirectory);
                return SetupArtifactCleaner.Run(paths);
            }
            catch (Exception) { return 1; }
        }
        if (args.Length != 0) { return 2; }        using var logProvider = new StructuredLogProvider(LogComponent.App,
            static line => System.Diagnostics.Trace.WriteLine(line), static () => Environment.Exit(1));
        using var loggerFactory = LoggerFactory.Create(builder =>
            builder.SetMinimumLevel(LogLevel.Trace).AddProvider(logProvider));
        var logger = loggerFactory.CreateLogger("Kugutsu.Startup");

        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            ReportFailure(logger, args.ExceptionObject as Exception
                ?? new InvalidOperationException("Unhandled non-exception failure."));
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            args.SetObserved();
            ReportFailure(logger, args.Exception);
        };
        try
        {
            var paths = InstallationPaths.FromExecutableDirectory(AppContext.BaseDirectory);
            logProvider.OpenFile(paths);
            var settings = SettingsLoader.Load(paths);
            logProvider.ApplySettings(settings);
            logger.WriteEvent(LogLevel.Information, LogEvent.Startup, LogOutcome.Started);
            using var instanceLease = SingleInstanceLease.TryAcquire();
            if (instanceLease is null)
            {
                logger.WriteEvent(LogLevel.Error, LogEvent.StartupFailure, LogOutcome.Failed,
                    error: LogError.InstanceAlreadyRunning);
                logger.WriteEvent(LogLevel.Error, LogEvent.Shutdown, LogOutcome.Failed);
                return 1;
            }
            using var credential = CredentialLoader.Load();
            await using (var host = HttpHost.Create(settings, paths, logProvider, credential))
            {
                await HttpHost.StartAsync(host);
                logger.WriteEvent(LogLevel.Information, LogEvent.Startup, LogOutcome.Succeeded);
                await RunTrayMessageLoopAsync(logger, host.Services.GetRequiredService<OperationRequestQueue>(),
                    host.Lifetime.ApplicationStopping);
                await HttpHost.StopAsync(host);
            }
            logger.WriteEvent(LogLevel.Information, LogEvent.Shutdown, LogOutcome.Succeeded);
            return 0;
        }
        catch (HttpHostShutdownException exception)
        {
            logger.WriteEvent(LogLevel.Error, LogEvent.Shutdown, LogOutcome.Failed, error: exception.Reason);
            return 1;
        }
        catch (HttpHostException exception)
        {
            logger.WriteEvent(LogLevel.Error, LogEvent.StartupFailure, LogOutcome.Failed, error: exception.Reason);
            logger.WriteEvent(LogLevel.Error, LogEvent.Shutdown, LogOutcome.Failed);
            return 1;
        }
        catch (CredentialException exception)
        {
            logger.WriteEvent(LogLevel.Error, LogEvent.StartupFailure, LogOutcome.Failed, error: exception.Reason);
            logger.WriteEvent(LogLevel.Error, LogEvent.Shutdown, LogOutcome.Failed);
            return 1;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            or System.Security.SecurityException or FormatException or InvalidDataException or InvalidOperationException)
        {
            logger.WriteEvent(LogLevel.Error, LogEvent.StartupFailure, LogOutcome.Failed,
                error: AuditLog.Classify(exception));
            logger.WriteEvent(LogLevel.Error, LogEvent.Shutdown, LogOutcome.Failed);
            return 1;
        }
        catch (Exception exception)
        {
            ReportFailure(logger, exception);
            return 1;
        }
    }

    private static Task RunTrayMessageLoopAsync(ILogger logger, OperationRequestQueue queue,
        CancellationToken hostStopping)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var uiThread = new Thread(() =>
        {
            try
            {
                Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
                Application.ThreadException += (_, args) => ReportFailure(logger, args.Exception);
                ApplicationConfiguration.Initialize();
                using (var tray = new TrayApplicationContext(queue, hostStopping))
                { Application.Run(tray); }
                completion.TrySetResult();
            }
            catch (Exception exception) { completion.TrySetException(exception); }
        });
        uiThread.SetApartmentState(ApartmentState.STA);
        uiThread.Start();
        return completion.Task;
    }

    private static void ReportFailure(ILogger logger, Exception exception)
    {
        if (Interlocked.Exchange(ref _failureReported, 1) != 0)
        {
            Environment.Exit(1);
            return;
        }

        logger.WriteEvent(LogLevel.Critical, LogEvent.UnhandledFailure, LogOutcome.Failed,
            error: LogError.UnexpectedFailure);
        logger.WriteEvent(LogLevel.Critical, LogEvent.Shutdown, LogOutcome.Failed);
        MessageBox.Show(
            "Kugutsuの初期化に失敗しました。アプリケーションを終了します。",
            "Kugutsu",
            MessageBoxButtons.OK,
            MessageBoxIcon.Error);
        Environment.Exit(1);
    }
}
