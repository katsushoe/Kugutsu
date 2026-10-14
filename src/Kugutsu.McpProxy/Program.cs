using Microsoft.Extensions.Logging;

using Kugutsu.Hosting;

namespace Kugutsu.McpProxy;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        using var logProvider = new StructuredLogProvider(LogComponent.Proxy,
            static line => Console.Error.WriteLine(line), static () => Environment.Exit(1));
        using var loggerFactory = LoggerFactory.Create(builder =>
            builder.SetMinimumLevel(LogLevel.Trace).AddProvider(logProvider));
        var logger = loggerFactory.CreateLogger("Kugutsu.McpProxy.Startup");

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
            var serverUrlOverride = ProxyArguments.Parse(args);
            var settings = SettingsLoader.Load(paths, serverUrlOverride);
            logProvider.ApplySettings(settings);
            logger.WriteEvent(LogLevel.Information, LogEvent.Startup, LogOutcome.Started);
            using var credential = CredentialLoader.Load();
            logger.WriteEvent(LogLevel.Information, LogEvent.Startup, LogOutcome.Succeeded);
            using var shutdown = new CancellationTokenSource();
            ConsoleCancelEventHandler onCancel = (_, args) => { args.Cancel = true; shutdown.Cancel(); };
            Console.CancelKeyPress += onCancel;
            try { await ProxyRelay.RunAsync(settings, credential, loggerFactory, logger, shutdown.Token); }
            finally { Console.CancelKeyPress -= onCancel; }
            logger.WriteEvent(LogLevel.Information, LogEvent.Shutdown, LogOutcome.Succeeded);
            return 0;
        }
        catch (CredentialException exception)
        {
            logger.WriteEvent(LogLevel.Error, LogEvent.StartupFailure, LogOutcome.Failed, error: exception.Reason);
            logger.WriteEvent(LogLevel.Error, LogEvent.Shutdown, LogOutcome.Failed);
            return 1;
        }
        catch (ProxyException exception)
        {
            logger.WriteEvent(LogLevel.Error, LogEvent.McpDisconnected, LogOutcome.Failed, error: exception.Reason);
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
        catch (Exception)
        {
            logger.WriteEvent(LogLevel.Critical, LogEvent.UnhandledFailure, LogOutcome.Failed,
                error: LogError.UnexpectedFailure);
            logger.WriteEvent(LogLevel.Critical, LogEvent.Shutdown, LogOutcome.Failed);
            return 1;
        }
    }

    private static void ReportFailure(ILogger logger, Exception exception)
    {
        logger.WriteEvent(LogLevel.Critical, LogEvent.UnhandledFailure, LogOutcome.Failed,
            error: LogError.UnexpectedFailure);
        logger.WriteEvent(LogLevel.Critical, LogEvent.Shutdown, LogOutcome.Failed);
        Environment.Exit(1);
    }
}
