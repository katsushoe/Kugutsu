using System.Runtime.InteropServices;
using Kugutsu.Contracts;

namespace Kugutsu.App;

internal readonly record struct PixelPoint(int X, int Y);
internal sealed record ClientGeometry(string WindowId, WindowBounds ClientScreenBounds,
    WindowBounds VirtualDesktopBounds);
internal sealed record CoordinateConversion(PixelPoint Point, ClientGeometry Geometry);

/// <summary>クライアント左上基準の物理ピクセルと仮想画面座標を相互変換する。</summary>
internal sealed class WindowCoordinates(WindowReferenceRegistry registry)
{
    internal ClientGeometry GetGeometry(string windowId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var reference = registry.Resolve(windowId);
        using var dpi = new PhysicalDpiScope();
        var client = ReadClientBounds(reference.Identity.Handle);
        var desktop = new WindowBounds(GetSystemMetrics(76), GetSystemMetrics(77),
            GetSystemMetrics(78), GetSystemMetrics(79)); // SM_*VIRTUALSCREEN
        if (desktop.Width <= 0 || desktop.Height <= 0)
        { throw new WindowReferenceException(ErrorCode.INTERNAL_ERROR); }
        if (ReadClientBounds(reference.Identity.Handle) != client)
        { throw new WindowReferenceException(ErrorCode.WINDOW_GEOMETRY_CHANGED); }
        registry.Resolve(windowId);
        cancellationToken.ThrowIfCancellationRequested();
        return new(windowId, client, desktop);
    }

    internal CoordinateConversion ClientToScreen(string windowId, PixelPoint point,
        CancellationToken cancellationToken)
    {
        var geometry = GetGeometry(windowId, cancellationToken);
        var client = geometry.ClientScreenBounds;
        if (point.X < 0 || point.Y < 0 || point.X >= client.Width || point.Y >= client.Height)
        { throw new WindowReferenceException(ErrorCode.INVALID_ARGUMENT); }
        PixelPoint screen;
        try { screen = new(checked(client.X + point.X), checked(client.Y + point.Y)); }
        catch (OverflowException) { throw new WindowReferenceException(ErrorCode.INVALID_ARGUMENT); }
        ValidateScreenPoint(screen, geometry.VirtualDesktopBounds);
        VerifyGeometry(geometry, cancellationToken);
        return new(screen, geometry);
    }

    internal CoordinateConversion ScreenToClient(string windowId, PixelPoint point,
        CancellationToken cancellationToken)
    {
        var geometry = GetGeometry(windowId, cancellationToken);
        ValidateScreenPoint(point, geometry.VirtualDesktopBounds);
        if (!Contains(geometry.ClientScreenBounds, point))
        { throw new WindowReferenceException(ErrorCode.INVALID_ARGUMENT); }
        var client = geometry.ClientScreenBounds;
        var relative = new PixelPoint(checked(point.X - client.X), checked(point.Y - client.Y));
        VerifyGeometry(geometry, cancellationToken);
        return new(relative, geometry);
    }

    internal void VerifyGeometry(ClientGeometry geometry, CancellationToken cancellationToken)
    {
        if (GetGeometry(geometry.WindowId, cancellationToken) != geometry)
        { throw new WindowReferenceException(ErrorCode.WINDOW_GEOMETRY_CHANGED); }
    }

    private static bool Contains(WindowBounds bounds, PixelPoint point) =>
        (long)point.X - bounds.X >= 0 && (long)point.X - bounds.X < bounds.Width
        && (long)point.Y - bounds.Y >= 0 && (long)point.Y - bounds.Y < bounds.Height;

    private static void ValidateScreenPoint(PixelPoint point, WindowBounds desktop)
    {
        using var dpi = new PhysicalDpiScope();
        if (!Contains(desktop, point) || MonitorFromPoint(new NativePoint { X = point.X, Y = point.Y }, 0) == 0)
        { throw new WindowReferenceException(ErrorCode.INVALID_ARGUMENT); }
    }

    private static WindowBounds ReadClientBounds(nint handle)
    {
        if (!GetClientRect(handle, out var rect)) { throw new WindowReferenceException(NativeFailure()); }
        // RECTを2点として渡すと、RTLウィンドウでもleft/rightが正規化される。
        if (MapWindowPoints(handle, 0, ref rect, 2) == 0 && Marshal.GetLastWin32Error() != 0)
        { throw new WindowReferenceException(NativeFailure()); }
        try
        {
            var width = checked(rect.Right - rect.Left);
            var height = checked(rect.Bottom - rect.Top);
            if (width < 0 || height < 0) { throw new WindowReferenceException(ErrorCode.INVALID_ARGUMENT); }
            return new(rect.Left, rect.Top, width, height);
        }
        catch (OverflowException) { throw new WindowReferenceException(ErrorCode.INVALID_ARGUMENT); }
    }

    private static ErrorCode NativeFailure() => Marshal.GetLastWin32Error() == 5
        ? ErrorCode.ACCESS_DENIED : ErrorCode.WINDOW_NOT_FOUND;

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint { public int X, Y; }
    [DllImport("user32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetClientRect(nint handle, out NativeRect rect);
    [DllImport("user32.dll", ExactSpelling = true, SetLastError = true)]
    private static extern int MapWindowPoints(nint from, nint to, ref NativeRect points, uint count);
    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern nint MonitorFromPoint(NativePoint point, uint flags);
}
