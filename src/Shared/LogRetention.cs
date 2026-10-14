using System.Diagnostics;
using System.Text.RegularExpressions;

namespace Kugutsu.Hosting;

/// <summary>Coordinates file rotation and retention across log writers in the same directory.</summary>
internal sealed class LogRetention
{
    private static readonly Regex ManagedName = new(
        @"^kugutsu(?:-proxy|-console-probe)?-\d{8}T\d{9}-[a-f0-9]{32}\.jsonl$",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);
    private readonly TimeSpan _retention;
    private readonly long _totalBytes;

    /// <summary>Creates an immutable policy from validated logging settings.</summary>
    public LogRetention(string directory, KugutsuOptions settings)
    {
        if (settings.LogRetentionDays is < 1 or > 365 || settings.LogFileSizeMiB is < 1 or > 100
            || settings.LogTotalSizeMiB is < 10 or > 1024 || settings.LogTotalSizeMiB < settings.LogFileSizeMiB)
        {
            throw new ArgumentOutOfRangeException(nameof(settings), "Invalid log retention settings.");
        }
        DirectoryPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        FileBytes = (long)settings.LogFileSizeMiB * 1024 * 1024;
        _totalBytes = (long)settings.LogTotalSizeMiB * 1024 * 1024;
        _retention = TimeSpan.FromDays(settings.LogRetentionDays);
    }

    /// <summary>Gets the normalized managed directory.</summary>
    public string DirectoryPath { get; }

    /// <summary>Gets the per-file UTF-8 byte limit.</summary>
    public long FileBytes { get; }

    /// <summary>Acquires the directory-wide file lease, waiting at most five seconds.</summary>
    public IDisposable Acquire()
    {
        var leasePath = Path.Combine(DirectoryPath, ".kugutsu-logs.lock");
        var started = Stopwatch.GetTimestamp();
        while (true)
        {
            try
            {
                if (File.Exists(leasePath) && (File.GetAttributes(leasePath) & FileAttributes.ReparsePoint) != 0)
                {
                    throw new IOException("The log lease must be a regular file.");
                }
                return new FileStream(leasePath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException exception) when (IsSharingViolation(exception))
            {
                if (Stopwatch.GetElapsedTime(started) >= TimeSpan.FromSeconds(5))
                {
                    throw new IOException("The log directory lease timed out.");
                }
                Thread.Sleep(25);
            }
        }
    }

    /// <summary>Deletes expired and oldest closed logs while reserving bytes for the next record.</summary>
    /// <remarks>The caller must hold the directory lease through the subsequent write.</remarks>
    public void Prune(long requiredBytes)
    {
        if (requiredBytes < 0 || requiredBytes > FileBytes)
        {
            throw new IOException("A log record exceeds the configured file limit.");
        }

        var files = GetFiles();
        var cutoff = DateTime.UtcNow - _retention;
        long total = 0;
        var remaining = new List<FileInfo>();
        foreach (var file in files)
        {
            if (file.LastWriteTimeUtc <= cutoff && DeleteClosed(file)) { continue; }
            total = checked(total + file.Length);
            remaining.Add(file);
        }

        foreach (var file in remaining)
        {
            if (total <= _totalBytes - requiredBytes) { break; }
            var length = file.Length;
            if (DeleteClosed(file)) { total -= length; }
        }
        if (total > _totalBytes - requiredBytes)
        {
            throw new IOException("Active log files prevent enforcing the total log limit.");
        }
    }

    private List<FileInfo> GetFiles()
    {
        var files = new List<FileInfo>();
        foreach (var path in Directory.EnumerateFiles(DirectoryPath, "*.jsonl", SearchOption.TopDirectoryOnly))
        {
            if (!ManagedName.IsMatch(Path.GetFileName(path))) { continue; }
            try
            {
                var file = new FileInfo(path);
                file.Refresh();
                if (!file.Exists || (file.Attributes & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0) { continue; }
                files.Add(file);
            }
            catch (FileNotFoundException) { /* A local user may remove a closed log. */ }
        }
        return files.OrderBy(file => file.LastWriteTimeUtc).ThenBy(file => file.Name, StringComparer.Ordinal).ToList();
    }

    private bool DeleteClosed(FileInfo file)
    {
        // Verify the absolute deletion target immediately before opening or deleting it.
        var fullPath = Path.GetFullPath(file.FullName);
        if (!string.Equals(Path.GetDirectoryName(fullPath), DirectoryPath, StringComparison.OrdinalIgnoreCase)
            || !ManagedName.IsMatch(Path.GetFileName(fullPath)))
        {
            throw new IOException("A log deletion target is outside the managed directory.");
        }
        try
        {
            if ((File.GetAttributes(fullPath) & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0) { return false; }
            // Live writers deny exclusive access and deletion. Never force-close their handles.
            using (var probe = new FileStream(fullPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
            File.Delete(fullPath);
            return true;
        }
        catch (FileNotFoundException) { return true; }
        catch (IOException exception) when (IsSharingViolation(exception)) { return false; }
    }

    private static bool IsSharingViolation(IOException exception) => (exception.HResult & 0xFFFF) is 32 or 33;
}
