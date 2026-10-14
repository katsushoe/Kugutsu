using System.Runtime.InteropServices;
using Kugutsu.Contracts;

namespace Kugutsu.App;

internal sealed record ValidatedVisualTarget(UiElementReference Reference, VisualElementHandle Handle, VisualFrame Frame, VisualRaster Current);

/// <summary>Window/profile identity and exact local pixels are checked without relocating an image target.</summary>
internal sealed class VisualReferenceValidator(Func<UiElementRegistry> elements, VisualFrameRegistry frames,
    WindowReferenceRegistry windows, WindowCoordinates coordinates, WindowDiscovery discovery,
    WindowStateOperations state, ScreenCapture capture, VisualProfileCatalog profiles)
{
    internal async ValueTask<ValidatedVisualTarget> ValidateAsync(UiOperationTarget target, CancellationToken token,
        bool compareControlImage = true)
    {
        var reference = elements().Resolve(target.ElementId, target.Window.WindowId, target.Driver, token);
        if (reference.Target != target || reference.NativeTarget is not VisualElementHandle handle
            || handle.SnapshotId != target.SnapshotId) { throw Stale(); }
        var frame = frames.Get(handle.SnapshotId, token);
        VerifyContext(reference, handle, frame, token);
        var image = capture.Capture(target.Window.WindowId, token);
        var png = Convert.FromBase64String(image.GetProperty("image").GetProperty("data").GetString()!);
        var current = await Task.Run(() => VisualRaster.Decode(png, token), token);
        if (current.Width != frame.Raster.Width || current.Height != frame.Raster.Height) { throw Stale(); }
        var ownBounds = handle.Meaning.Source.Shape.Bounds;
        if (compareControlImage && !EqualRegion(frame.Raster, current, ownBounds, token)) { throw Stale(); }
        foreach (var bounds in handle.Meaning.Evidence.Where(item => item.Bounds is not null).Select(item => item.Bounds!).Distinct())
        {
            if (Intersects(bounds, ownBounds)) { continue; }
            if (!EqualRegion(frame.Raster, current, bounds, token)) { throw Stale(); }
        }
        VerifyContext(reference, handle, frame, token);
        return new(reference, handle, frame, current);
    }

    internal void VerifyContext(UiElementReference reference, VisualElementHandle handle, VisualFrame frame, CancellationToken token)
    {
        var target = reference.Target;
        token.ThrowIfCancellationRequested();
        if (frames.Get(handle.SnapshotId, token) != frame || frame.Window != target.Window
            || windows.Resolve(target.Window.WindowId) != target.Window) { throw Stale(); }
        var stamp = elements().GetVisualStamp(target.Window.WindowId, token);
        var context = discovery.Get(target.Window.WindowId, token);
        if (stamp.Geometry != frame.Stamp.Geometry || stamp.Dpi != frame.Stamp.Dpi
            || context.Title != frame.Context.Title || context.TitleTruncated != frame.Context.TitleTruncated) { throw Stale(); }
        if (handle.Meaning.Profile is { } expected)
        {
            var current = profiles.Load(token).SingleOrDefault(profile => profile.ProfileId == expected.ProfileId);
            if (current is null || current.Driver != target.Driver || current.ProfileVersion != expected.ProfileVersion
                || current.ContentHash != expected.ContentHash) { throw Stale(); }
        }
        state.VerifyInputTarget(target.Window.WindowId, token);
        VerifyHit(target, handle.Meaning.Source.Shape.Bounds, token);
        token.ThrowIfCancellationRequested();
        if (elements().Resolve(target.ElementId, target.Window.WindowId, target.Driver, token) != reference) { throw Stale(); }
    }

    private void VerifyHit(UiOperationTarget target, UiBounds bounds, CancellationToken token)
    {
        var inset = Math.Min(2, Math.Min(bounds.Width, bounds.Height) / 2);
        var points = new[] { new PixelPoint(bounds.X + bounds.Width / 2, bounds.Y + bounds.Height / 2),
            new(bounds.X + inset, bounds.Y + inset), new(bounds.X + bounds.Width - 1 - inset, bounds.Y + inset),
            new(bounds.X + inset, bounds.Y + bounds.Height - 1 - inset),
            new(bounds.X + bounds.Width - 1 - inset, bounds.Y + bounds.Height - 1 - inset) };
        foreach (var point in points)
        {
            var screen = coordinates.ClientToScreen(target.Window.WindowId, point, token).Point;
            using var dpi = new PhysicalDpiScope();
            var hit = WindowFromPoint(new() { X = screen.X, Y = screen.Y });
            if (hit == 0 || hit != target.Window.Identity.Handle && GetAncestor(hit, 2) != target.Window.Identity.Handle)
            { throw new WindowReferenceException(ErrorCode.UI_TARGET_UNVERIFIED); }
        }
    }

    private static bool EqualRegion(VisualRaster baseline, VisualRaster current, UiBounds bounds, CancellationToken token)
    {
        if (bounds.X < 0 || bounds.Y < 0 || bounds.Width <= 0 || bounds.Height <= 0
            || (long)bounds.X + bounds.Width > current.Width || (long)bounds.Y + bounds.Height > current.Height) { return false; }
        for (var y = bounds.Y; y < bounds.Y + bounds.Height; y++)
        {
            token.ThrowIfCancellationRequested();
            var start = (y * current.Width + bounds.X) * 4;
            if (!baseline.Pixels.AsSpan(start, bounds.Width * 4).SequenceEqual(current.Pixels.AsSpan(start, bounds.Width * 4))) { return false; }
        }
        return true;
    }
    private static bool Intersects(UiBounds a, UiBounds b) => a.X < b.X + b.Width && b.X < a.X + a.Width
        && a.Y < b.Y + b.Height && b.Y < a.Y + a.Height;
    private static WindowReferenceException Stale() => new(ErrorCode.UI_ELEMENT_STALE);
    [StructLayout(LayoutKind.Sequential)] private struct Point { public int X, Y; }
    [DllImport("user32.dll", ExactSpelling = true)] private static extern nint WindowFromPoint(Point point);
    [DllImport("user32.dll", ExactSpelling = true)] private static extern nint GetAncestor(nint window, uint flags);
}
