using Microsoft.Extensions.Logging;

namespace Kugutsu.Hosting;

/// <summary>Configuration values read from the Kugutsu section.</summary>
internal sealed class KugutsuOptions
{
    /// <summary>Gets or sets the server endpoint from external configuration.</summary>
    public string? ServerUrl { get; set; }

    /// <summary>Gets or sets: Maximum queued requests.</summary>
    public int QueueCapacity { get; set; } = 100;

    /// <summary>Gets or sets: Maximum operations per batch.</summary>
    public int MaxBatchOperations { get; set; } = 1000;

    /// <summary>Gets or sets the maximum executed Aloe instructions per request.</summary>
    public int MaxAloeInstructions { get; set; } = 1000000;

    /// <summary>Gets or sets the private committed memory ceiling for the Aloe helper.</summary>
    public int AloeMemoryLimitMiB { get; set; } = 256;

    /// <summary>Gets or sets: Maximum UTF-8 request size.</summary>
    public int MaxRequestBytes { get; set; } = 1024 * 1024;

    /// <summary>Gets or sets the maximum logical clients retained during one server lifetime.</summary>
    public int MaxClientSessions { get; set; } = 1000;

    /// <summary>Gets or sets: Per-operation timeout.</summary>
    public int OperationTimeoutSeconds { get; set; } = 30;

    /// <summary>Gets or sets: Whole request timeout.</summary>
    public int RequestTimeoutSeconds { get; set; } = 300;

    /// <summary>Gets or sets: Queue timeout.</summary>
    public int QueueTimeoutSeconds { get; set; } = 300;

    /// <summary>Gets or sets: Default lock lease.</summary>
    public int McpLockLeaseSeconds { get; set; } = 120;

    /// <summary>Gets or sets: Default lock acquisition timeout.</summary>
    public int McpLockAcquireTimeoutSeconds { get; set; } = 30;

    /// <summary>Gets or sets: Maximum registered signals.</summary>
    public int MaxSignals { get; set; } = 100;

    /// <summary>Gets or sets: Signal lifetime.</summary>
    public int SignalRetentionSeconds { get; set; } = 300;

    /// <summary>Gets or sets: State polling interval.</summary>
    public int SignalPollIntervalMilliseconds { get; set; } = 200;

    /// <summary>Gets or sets the fixed local OCR language combination.</summary>
    public string OcrLanguage { get; set; } = "jpn+eng";

    /// <summary>Gets or sets the maximum number of OCR words retained in memory.</summary>
    public int OcrMaxWords { get; set; } = 1000;

    /// <summary>Gets or sets the minimum luminance contrast for visual shape boundaries.</summary>
    public int VisualEdgeContrast { get; set; } = 24;

    /// <summary>Gets or sets: Enabled UI drivers.</summary>
    public string[] EnabledUiDrivers { get; set; } = ["uia", "imgui_visual", "generic_visual"];

    /// <summary>Gets or sets: Installation-relative profile directory.</summary>
    public string? UiProfileDirectory { get; set; } = null;

    /// <summary>Gets or sets: Visual reference lifetime.</summary>
    public int VisualSnapshotLifetimeSeconds { get; set; } = 10;

    /// <summary>Gets or sets: Maximum discovery candidates.</summary>
    public int MaxUiCandidates { get; set; } = 100;

    /// <summary>Gets or sets: Completed result retention.</summary>
    public int ResultRetentionMinutes { get; set; } = 60;

    /// <summary>Gets or sets: Requested logging level.</summary>
    public LogLevel LogLevel { get; set; } = LogLevel.Information;

    /// <summary>Gets or sets: Log file retention.</summary>
    public int LogRetentionDays { get; set; } = 14;

    /// <summary>Gets or sets: Log file size limit.</summary>
    public int LogFileSizeMiB { get; set; } = 10;

    /// <summary>Gets or sets: Total log size limit.</summary>
    public int LogTotalSizeMiB { get; set; } = 200;

    /// <summary>Gets or sets: Registered application launch targets.</summary>
    public LaunchTargetOptions[] AllowedLaunchTargets { get; set; } = [];

    /// <summary>Gets or sets: Configured terminal launch target identifier.</summary>
    public string? TerminalLaunchTargetId { get; set; } = "terminal";

    /// <summary>Gets or sets: Configured PowerShell launch target identifier.</summary>
    public string? PowerShellLaunchTargetId { get; set; } = "powershell";

    /// <summary>Gets or sets the explicit console host target used for a dedicated PowerShell console.</summary>
    public string? ConsoleHostLaunchTargetId { get; set; } = "consolehost";

    /// <summary>Gets or sets: Browser types and launch target identifiers.</summary>
    public Dictionary<string, string> BrowserLaunchTargets { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Gets or sets: Browser types and driver executable paths.</summary>
    public Dictionary<string, string> BrowserDriverPaths { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>Describes a configured executable and its fixed arguments.</summary>
internal sealed class LaunchTargetOptions
{
    /// <summary>Gets or sets the launch target identifier.</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>Gets or sets the executable path from configuration.</summary>
    public string ExecutablePath { get; set; } = string.Empty;

    /// <summary>Gets or sets the fixed argument array.</summary>
    public string[] Arguments { get; set; } = [];
}

