using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Kugutsu.Hosting;

/// <summary>Identifies the executable writing the log.</summary>
internal enum LogComponent { App, Proxy, ConsoleProbe }

/// <summary>Fixed events permitted in the audit log.</summary>
internal enum LogEvent
{
    Startup, Shutdown, StartupFailure, UnhandledFailure, LogWriteFailure,
    McpConnected, McpDisconnected, McpReconnecting, McpMessageFailed, McpProtocolError, RequestAccepted, RequestStarted, RequestCompleted,
    OperationCompleted, LockAcquired, LockRenewed, LockReleased, LockExpired,
    SignalRegistered, SignalChanged, SignalRemoved, RequestCancellationRequested,
    EmergencyStopActivated, OperationAdmissionResumed,
    ForceUnlockRequested, ForceUnlockCompleted,
}

/// <summary>Operation categories without input values or target names.</summary>
internal enum LogOperation { Window, Keyboard, Mouse, Snapshot, Control, Application, Terminal, PowerShell, Browser, Dom, Signal, Batch }

/// <summary>Fixed execution outcomes.</summary>
internal enum LogOutcome { Started, Succeeded, Failed, Cancelled, TimedOut }

/// <summary>Diagnostic codes independent of exception messages.</summary>
internal enum LogError { ConfigurationInvalid, IoFailure, PermissionDenied, UnexpectedFailure, OperationFailed, Cancelled, Timeout, RequestTimeout, LogWriteFailed, CredentialMissing, CredentialInvalid, CredentialUnavailable, HttpPortInUse, HttpHostStartFailed, McpAuthenticationFailed, McpTransportFailed, McpProtocolFailed, McpSessionFailed, InstanceAlreadyRunning, HttpHostStopFailed }

/// <summary>Only explicitly permitted metadata may be passed to the provider.</summary>
internal sealed record SafeLogEntry(LogEvent Event, LogOutcome Outcome, Guid? RequestId,
    LogOperation? Operation, int? OperationIndex, long? ElapsedMilliseconds, LogError? Error,
    string SourceFile, int SourceLine);

/// <summary>Writes fixed events without accepting arbitrary messages or exception text.</summary>
internal static class AuditLog
{
    /// <summary>Writes metadata for a lifecycle, request or operation event.</summary>
    public static void WriteEvent(this ILogger logger, LogLevel level, LogEvent logEvent, LogOutcome outcome,
        Guid? requestId = null, LogOperation? operation = null, int? operationIndex = null,
        long? elapsedMilliseconds = null, LogError? error = null,
        [CallerFilePath] string sourceFile = "", [CallerLineNumber] int sourceLine = 0)
    {
        ArgumentNullException.ThrowIfNull(logger);
        if (!Enum.IsDefined(level) || level == LogLevel.None || !Enum.IsDefined(logEvent)
            || !Enum.IsDefined(outcome) || (operation.HasValue && !Enum.IsDefined(operation.Value))
            || (error.HasValue && !Enum.IsDefined(error.Value))
            || operationIndex < 0 || elapsedMilliseconds < 0 || sourceLine < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(logEvent), "Invalid audit metadata.");
        }

        var entry = new SafeLogEntry(logEvent, outcome, requestId, operation, operationIndex,
            elapsedMilliseconds, error, Path.GetFileName(sourceFile), sourceLine);
        logger.Log(level, new EventId((int)logEvent), entry, null, static (state, _) => state.Event.ToString());
    }

    /// <summary>Classifies failures without retaining their messages or inner exceptions.</summary>
    public static LogError Classify(Exception exception) => exception switch
    {
        UnauthorizedAccessException or System.Security.SecurityException => LogError.PermissionDenied,
        InvalidDataException or FormatException or InvalidOperationException => LogError.ConfigurationInvalid,
        IOException => LogError.IoFailure,
        _ => LogError.UnexpectedFailure,
    };
}

/// <summary>Serializes permitted audit metadata into UTF-8 JSON Lines files.</summary>
internal sealed class StructuredLogProvider : ILoggerProvider
{
    private readonly object _gate = new();
    private readonly LogComponent _component;
    private readonly Action<string> _fallback;
    private readonly Action _onWriteFailure;
    private StreamWriter? _writer;
    private LogRetention? _retention;
    private System.Threading.Timer? _cleanupTimer;
    private DateOnly _fileDay;
    private volatile LogLevel _minimum = LogLevel.Information;
    private bool _disposed;

    /// <summary>Creates a provider with a diagnostic sink available before settings are loaded.</summary>
    public StructuredLogProvider(LogComponent component, Action<string> fallback, Action onWriteFailure)
    {
        _component = component;
        _fallback = fallback;
        _onWriteFailure = onWriteFailure;
    }

    /// <summary>Creates a distinct log file so configuration failures can also be recorded.</summary>
    public void OpenFile(InstallationPaths paths)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_writer is not null) { throw new InvalidOperationException("Log file is already open."); }
            Directory.CreateDirectory(paths.LogsDirectory);
            _retention = new LogRetention(paths.LogsDirectory, new KugutsuOptions());
            using var lease = _retention.Acquire();
            _retention.Prune(0);
            CreateFile();
            _cleanupTimer = new System.Threading.Timer(static state => ((StructuredLogProvider)state!).Sweep(), this,
                TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
        }
    }

    /// <summary>Applies validated level and retention settings to subsequent events.</summary>
    public void ApplySettings(KugutsuOptions settings)
    {
        if (settings.LogLevel is < LogLevel.Trace or > LogLevel.Error)
        {
            throw new ArgumentOutOfRangeException(nameof(settings));
        }
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_retention is null || _writer is null) { throw new InvalidOperationException("Log file is not open."); }
            var policy = new LogRetention(_retention.DirectoryPath, settings);
            using var lease = policy.Acquire();
            _retention = policy;
            if (_writer.BaseStream.Length > policy.FileBytes) { Rotate(); }
            policy.Prune(0);
            _minimum = settings.LogLevel;
        }
    }

    /// <summary>Creates a logger that ignores arbitrary categories, scopes and message states.</summary>
    public ILogger CreateLogger(string categoryName) => new AuditLogger(this);

    /// <summary>Closes the owned file without disposing the diagnostic sink.</summary>
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) { return; }
            _disposed = true;
            _cleanupTimer?.Dispose();
            try { _writer?.Dispose(); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                WriteFailure();
            }
            finally { _writer = null; }
        }
    }

    private bool IsEnabled(LogLevel level) => level >= _minimum && level <= LogLevel.Critical;

    private string Serialize(LogLevel level, SafeLogEntry entry) => JsonSerializer.Serialize(new
    {
        timestamp = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
        level = level.ToString(),
        component = _component.ToString(),
        @event = entry.Event.ToString(),
        outcome = entry.Outcome.ToString(),
        request_id = entry.RequestId,
        operation_kind = entry.Operation?.ToString(),
        operation_index = entry.OperationIndex,
        elapsed_ms = entry.ElapsedMilliseconds,
        error_code = entry.Error?.ToString(),
        source_file = entry.SourceFile,
        source_line = entry.SourceLine,
    });

    private void Emit(LogLevel level, SafeLogEntry entry)
    {
        lock (_gate)
        {
            if (_disposed || !IsEnabled(level)) { return; }
            var line = Serialize(level, entry);
            if (_writer is null) { EmitFallback(line); return; }
            try
            {
                var policy = _retention!;
                using var lease = policy.Acquire();
                var bytes = Encoding.UTF8.GetByteCount(line) + 1L;
                if (_writer.BaseStream.Length > policy.FileBytes - bytes
                    || _fileDay != DateOnly.FromDateTime(DateTime.UtcNow)) { Rotate(); }
                policy.Prune(bytes);
                _writer!.WriteLine(line);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                WriteFailure();
            }
        }
    }

    private void CreateFile()
    {
        var now = DateTimeOffset.UtcNow;
        var prefix = _component switch { LogComponent.App => "kugutsu", LogComponent.Proxy => "kugutsu-proxy", _ => "kugutsu-console-probe" };
        var stamp = now.ToString("yyyyMMddTHHmmssfff", CultureInfo.InvariantCulture);
        var file = Path.Combine(_retention!.DirectoryPath, $"{prefix}-{stamp}-{Guid.NewGuid():N}.jsonl");
        var stream = new FileStream(file, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        try { _writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" }; }
        catch { stream.Dispose(); throw; }
        _fileDay = DateOnly.FromDateTime(now.UtcDateTime);
    }

    private void Rotate()
    {
        _writer!.Dispose();
        _writer = null;
        CreateFile();
    }

    private void Sweep()
    {
        lock (_gate)
        {
            if (_disposed || _retention is null || _writer is null) { return; }
            try
            {
                using var lease = _retention.Acquire();
                _retention.Prune(0);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                WriteFailure();
            }
        }
    }

    private void EmitFallback(string line)
    {
        try { _fallback(line); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ObjectDisposedException)
        {
            _onWriteFailure();
        }
    }

    private void WriteFailure([CallerLineNumber] int sourceLine = 0)
    {
        var failure = new SafeLogEntry(LogEvent.LogWriteFailure, LogOutcome.Failed,
            null, null, null, null, LogError.LogWriteFailed, "StructuredLog.cs", sourceLine);
        try { EmitFallback(Serialize(LogLevel.Critical, failure)); }
        finally { _onWriteFailure(); }
    }

    private sealed class AuditLogger(StructuredLogProvider provider) : ILogger
    {
        public bool IsEnabled(LogLevel logLevel) => provider.IsEnabled(logLevel);
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter)
        {
            // Do not call formatter, traverse scopes or serialize exception/state objects.
            if (state is SafeLogEntry entry) { provider.Emit(logLevel, entry); }
        }
    }
}
