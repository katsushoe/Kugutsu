using System.Diagnostics;
using System.Runtime.InteropServices;
using Kugutsu.Contracts;
using Kugutsu.Hosting;

namespace Kugutsu.App;

internal sealed record UiVisualStamp(ClientGeometry Geometry, uint Dpi, long CapturedAt);
internal sealed record UiElementReference(UiOperationTarget Target, object NativeTarget, UiVisualStamp? VisualStamp);

/// <summary>不透明なドライバ参照を保持し、別対象への再対応を行わない。</summary>
internal sealed class UiElementRegistry(WindowReferenceRegistry windows, WindowCoordinates coordinates,
    UiDriverRegistry drivers, KugutsuOptions settings) : IDisposable
{
    private sealed record Entry(UiElementReference Reference);
    private readonly object _gate = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly TimeSpan _visualLifetime = TimeSpan.FromSeconds(settings.VisualSnapshotLifetimeSeconds);
    private bool _disposed;

    internal static bool IsValidId(string? id) => id is { Length: 34 }
        && id.StartsWith("e_", StringComparison.Ordinal) && Guid.TryParseExact(id.AsSpan(2), "N", out _);

    internal UiVisualStamp GetVisualStamp(string windowId, CancellationToken token)
    {
        var capturedAt = Stopwatch.GetTimestamp();
        var geometry = coordinates.GetGeometry(windowId, token);
        var reference = windows.Resolve(windowId);
        var dpi = GetDpiForWindow(reference.Identity.Handle);
        if (dpi == 0) { throw new WindowReferenceException(ErrorCode.UI_TARGET_UNVERIFIED); }
        coordinates.VerifyGeometry(geometry, token);
        return new(geometry, dpi, capturedAt);
    }

    internal UiElementReference Register(WindowReference window, string driver, object nativeTarget,
        string? snapshotId, string? profileId, string? profileVersion, UiVisualStamp? visualStamp,
        CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(nativeTarget);
        token.ThrowIfCancellationRequested();
        var implementation = drivers.Resolve(driver);
        var visual = driver != "uia";
        if ((profileId is null) != (profileVersion is null)
            || profileId is not null && (string.IsNullOrWhiteSpace(profileId) || string.IsNullOrWhiteSpace(profileVersion))
            || visual && (string.IsNullOrWhiteSpace(snapshotId) || visualStamp is null)
            || !visual && visualStamp is not null)
        { throw new WindowReferenceException(ErrorCode.INVALID_ARGUMENT); }
        if (windows.Resolve(window.WindowId) != window)
        { throw new WindowReferenceException(ErrorCode.UI_ELEMENT_STALE); }
        VerifyProfile(implementation, profileId, profileVersion);
        if (visualStamp is not null && (visualStamp.Geometry.WindowId != window.WindowId
            || visualStamp.CapturedAt > Stopwatch.GetTimestamp()
            || Stopwatch.GetElapsedTime(visualStamp.CapturedAt) >= _visualLifetime
            || !IsVisualCurrent(window.WindowId, visualStamp, token)))
        { throw new WindowReferenceException(ErrorCode.UI_ELEMENT_STALE); }
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            PruneExpired();
            if (_entries.Count >= 10000)
            {
                foreach (var pair in _entries.ToArray())
                {
                    token.ThrowIfCancellationRequested();
                    try { windows.Resolve(pair.Value.Reference.Target.Window.WindowId); }
                    catch (WindowReferenceException exception) when (exception.Code is ErrorCode.WINDOW_NOT_FOUND or ErrorCode.ACCESS_DENIED)
                    { _entries.Remove(pair.Key); }
                }
            }
            if (_entries.Count >= 10000)
            { throw new WindowReferenceException(ErrorCode.UI_ELEMENT_LIMIT_EXCEEDED); }
            token.ThrowIfCancellationRequested();
            var id = "e_" + Guid.NewGuid().ToString("N");
            var reference = new UiElementReference(new(window, driver, id, snapshotId, profileId, profileVersion),
                nativeTarget, visualStamp);
            _entries.Add(id, new(reference));
            return reference;
        }
    }

    internal UiElementReference Resolve(string elementId, string windowId, CancellationToken token)
    {
        if (!IsValidId(elementId) || !WindowReferenceRegistry.IsValidId(windowId))
        { throw new WindowReferenceException(ErrorCode.INVALID_ARGUMENT); }
        token.ThrowIfCancellationRequested();
        string driver;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_entries.TryGetValue(elementId, out var entry))
            { throw new WindowReferenceException(ErrorCode.UI_ELEMENT_NOT_FOUND); }
            driver = entry.Reference.Target.Driver;
        }
        return Resolve(elementId, windowId, driver, token);
    }

    internal UiElementReference Resolve(string elementId, string windowId, string driver, CancellationToken token)
    {
        if (!IsValidId(elementId) || !WindowReferenceRegistry.IsValidId(windowId))
        { throw new WindowReferenceException(ErrorCode.INVALID_ARGUMENT); }
        token.ThrowIfCancellationRequested();
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_entries.TryGetValue(elementId, out var entry))
            { throw new WindowReferenceException(ErrorCode.UI_ELEMENT_NOT_FOUND); }
            var reference = entry.Reference;
            if (reference.Target.Window.WindowId != windowId || reference.Target.Driver != driver)
            { throw new WindowReferenceException(ErrorCode.UI_ELEMENT_STALE); }
            try
            {
                if (IsExpired(entry) || windows.Resolve(windowId) != reference.Target.Window)
                { throw new WindowReferenceException(ErrorCode.UI_ELEMENT_STALE); }
                var implementation = drivers.Resolve(driver);
                VerifyProfile(implementation, reference.Target.ProfileId, reference.Target.ProfileVersion);
                if (reference.VisualStamp is not null && !IsVisualCurrent(windowId, reference.VisualStamp, token))
                { throw new WindowReferenceException(ErrorCode.UI_ELEMENT_STALE); }
                token.ThrowIfCancellationRequested();
                return reference;
            }
            catch (WindowReferenceException exception)
            {
                _entries.Remove(elementId);
                if (exception.Code is ErrorCode.WINDOW_NOT_FOUND or ErrorCode.WINDOW_GEOMETRY_CHANGED)
                { throw new WindowReferenceException(ErrorCode.UI_ELEMENT_STALE); }
                throw;
            }
        }
    }

    internal void Invalidate(string elementId)
    { lock (_gate) { ObjectDisposedException.ThrowIf(_disposed, this); _entries.Remove(elementId); } }

    internal void InvalidateSnapshot(string snapshotId)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            foreach (var pair in _entries.Where(pair => pair.Value.Reference.Target.SnapshotId == snapshotId).ToArray())
            { _entries.Remove(pair.Key); }
        }
    }

    private static void VerifyProfile(IUiDriver implementation, string? profileId, string? profileVersion)
    {
        if (profileId is not null && !implementation.Info.Profiles.Any(item => item.ProfileId == profileId
            && item.ProfileVersion == profileVersion))
        { throw new WindowReferenceException(ErrorCode.UI_ELEMENT_STALE); }
    }

    private bool IsVisualCurrent(string windowId, UiVisualStamp expected, CancellationToken token)
    {
        var actual = GetVisualStamp(windowId, token);
        return actual.Geometry == expected.Geometry && actual.Dpi == expected.Dpi;
    }

    private bool IsExpired(Entry entry) => entry.Reference.VisualStamp is { } stamp
        && Stopwatch.GetElapsedTime(stamp.CapturedAt) >= _visualLifetime;
    private void PruneExpired()
    {
        foreach (var pair in _entries.Where(pair => IsExpired(pair.Value)).ToArray()) { _entries.Remove(pair.Key); }
    }

    public void Dispose()
    { lock (_gate) { _disposed = true; _entries.Clear(); } }

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern uint GetDpiForWindow(nint handle);
}
