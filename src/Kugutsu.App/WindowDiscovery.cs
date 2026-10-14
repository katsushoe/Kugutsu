using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Kugutsu.Contracts;

namespace Kugutsu.App;

internal sealed record WindowBounds(
    [property: JsonPropertyName("x")] int X, [property: JsonPropertyName("y")] int Y,
    [property: JsonPropertyName("width")] int Width, [property: JsonPropertyName("height")] int Height);
internal sealed record WindowInfo(
    [property: JsonPropertyName("windowId")] string WindowId,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("processId")] uint ProcessId,
    [property: JsonPropertyName("processName")] string ProcessName,
    [property: JsonPropertyName("visible")] bool Visible,
    [property: JsonPropertyName("minimized")] bool Minimized,
    [property: JsonPropertyName("maximized")] bool Maximized,
    [property: JsonPropertyName("bounds")] WindowBounds Bounds,
    [property: JsonPropertyName("titleTruncated")] bool TitleTruncated);
internal sealed record WindowListResult(
    [property: JsonPropertyName("windows")] IReadOnlyList<WindowInfo> Windows,
    [property: JsonPropertyName("truncated")] bool Truncated,
    [property: JsonPropertyName("skippedCount")] int SkippedCount);

internal sealed class WindowDiscovery(WindowReferenceRegistry registry)
{
    private const int MaxWindows = 1000;
    private const int MaxScanned = 4096;
    private const int MaxTitle = 4096;

    internal static bool ValidFilters(string? titleContains, string? processName) =>
        (titleContains is null || titleContains.Length <= 1024 && !titleContains.Contains('\0'))
        && (processName is null || processName.Length <= 256 && !processName.Contains('\0'));

    internal OperationDefinition ListDefinition() => new("window.list",
        ArgumentSchema.Object([
            new("titleContains", ArgumentSchema.String(1024), false),
            new("processName", ArgumentSchema.String(256), false)]),
        (arguments, token) => ValueTask.FromResult<JsonElement?>(JsonSerializer.SerializeToElement(List(
            arguments.TryGetProperty("titleContains", out var title) ? title.GetString() : null,
            arguments.TryGetProperty("processName", out var process) ? process.GetString() : null, token))),
        arguments => ValidFilters(
            arguments.TryGetProperty("titleContains", out var title) ? title.GetString() : null,
            arguments.TryGetProperty("processName", out var process) ? process.GetString() : null));

    internal OperationDefinition GetDefinition() => new("window.get",
        ArgumentSchema.Object([new("windowId", ArgumentSchema.String(34, 34))]),
        (arguments, token) => ValueTask.FromResult<JsonElement?>(JsonSerializer.SerializeToElement(
            Get(arguments.GetProperty("windowId").GetString()!, token))),
        arguments => WindowReferenceRegistry.IsValidId(arguments.GetProperty("windowId").GetString()));

    internal WindowInfo Get(string windowId, CancellationToken cancellationToken)
    {
        if (!WindowReferenceRegistry.IsValidId(windowId))
        { throw new WindowReferenceException(ErrorCode.INVALID_ARGUMENT); }
        cancellationToken.ThrowIfCancellationRequested();
        var reference = registry.Resolve(windowId);
        using var dpi = new PhysicalDpiScope();
        var name = ReadProcessName(reference);
        cancellationToken.ThrowIfCancellationRequested();
        var title = ReadTitle(reference.Identity.Handle);
        return ReadState(reference, name, title, cancellationToken);
    }

    internal WindowListResult List(string? titleContains, string? processName, CancellationToken cancellationToken)
    {
        if (!ValidFilters(titleContains, processName))
        { throw new WindowReferenceException(ErrorCode.INVALID_ARGUMENT); }
        cancellationToken.ThrowIfCancellationRequested();
        registry.PruneInvalidReferences();
        var windows = new List<WindowInfo>();
        var scanned = 0;
        var skipped = 0;
        var truncated = false;
        Exception? callbackFailure = null;
        using (var dpi = new PhysicalDpiScope())
        {
            var success = EnumWindows((handle, _) =>
            {
                try
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (scanned++ >= MaxScanned || windows.Count >= MaxWindows)
                    { truncated = true; return false; }
                    try
                    {
                        var reference = registry.Register(handle);
                        var name = ReadProcessName(reference);
                        if (!string.IsNullOrEmpty(processName)
                            && !name.Equals(processName, StringComparison.OrdinalIgnoreCase)) { return true; }
                        var title = ReadTitle(handle);
                        if (!string.IsNullOrEmpty(titleContains)
                            && !title.Text.Contains(titleContains, StringComparison.OrdinalIgnoreCase)) { return true; }
                        windows.Add(ReadState(reference, name, title, cancellationToken));
                    }
                    catch (WindowReferenceException exception) when (exception.Code is ErrorCode.WINDOW_NOT_FOUND or ErrorCode.ACCESS_DENIED)
                    { skipped++; }
                    catch (Exception exception) when (exception is Win32Exception or ArgumentException or InvalidOperationException)
                    { skipped++; }
                    return true;
                }
                catch (Exception exception) { callbackFailure = exception; return false; }
            }, 0);
            cancellationToken.ThrowIfCancellationRequested();
            if (callbackFailure is not null)
            { System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(callbackFailure).Throw(); }
            if (!success && !truncated) { throw new WindowReferenceException(ErrorCode.INTERNAL_ERROR); }
            return new(windows.ToArray(), truncated, skipped);
        }
    }

    private static string ReadProcessName(WindowReference reference)
    {
        try
        {
            using var process = Process.GetProcessById(checked((int)reference.Identity.ProcessId));
            return process.ProcessName;
        }
        catch (Win32Exception exception)
        { throw new WindowReferenceException(exception.NativeErrorCode == 5 ? ErrorCode.ACCESS_DENIED : ErrorCode.WINDOW_NOT_FOUND); }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        { throw new WindowReferenceException(ErrorCode.WINDOW_NOT_FOUND); }
    }

    private static (string Text, bool Truncated) ReadTitle(nint handle)
    {
        var length = GetWindowTextLength(handle);
        if (length == 0 && Marshal.GetLastWin32Error() != 0)
        { throw new WindowReferenceException(NativeReadFailure()); }
        var buffer = new StringBuilder(MaxTitle + 1);
        var copied = GetWindowText(handle, buffer, buffer.Capacity);
        if (copied == 0 && Marshal.GetLastWin32Error() != 0)
        { throw new WindowReferenceException(NativeReadFailure()); }
        return (buffer.ToString(), length > MaxTitle || copied == MaxTitle);
    }

    private WindowInfo ReadState(WindowReference reference, string name,
        (string Text, bool Truncated) title, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var handle = reference.Identity.Handle;
        if (!GetWindowRect(handle, out var rect))
        { throw new WindowReferenceException(NativeReadFailure()); }
        var info = new WindowInfo(reference.WindowId, title.Text, reference.Identity.ProcessId,
            name, IsWindowVisible(handle), IsIconic(handle), IsZoomed(handle),
            new(rect.Left, rect.Top, checked(rect.Right - rect.Left), checked(rect.Bottom - rect.Top)), title.Truncated);
        registry.Resolve(reference.WindowId);
        cancellationToken.ThrowIfCancellationRequested();
        return info;
    }

    private static ErrorCode NativeReadFailure() => Marshal.GetLastWin32Error() == 5
        ? ErrorCode.ACCESS_DENIED : ErrorCode.WINDOW_NOT_FOUND;

    private delegate bool EnumWindowCallback(nint handle, nint parameter);
    [DllImport("user32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowCallback callback, nint parameter);
    [DllImport("user32.dll", EntryPoint = "GetWindowTextLengthW", ExactSpelling = true, SetLastError = true)]
    private static extern int GetWindowTextLength(nint handle);
    [DllImport("user32.dll", EntryPoint = "GetWindowTextW", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern int GetWindowText(nint handle, StringBuilder text, int maximum);
    [DllImport("user32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(nint handle, out NativeRect rect);
    [DllImport("user32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(nint handle);
    [DllImport("user32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(nint handle);
    [DllImport("user32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsZoomed(nint handle);
    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { public int Left, Top, Right, Bottom; }
}
