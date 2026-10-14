using System.ComponentModel;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text.Json;
using Kugutsu.Contracts;

namespace Kugutsu.App;

internal sealed class ScreenCapture(WindowDiscovery discovery, WindowCoordinates coordinates)
{
    internal OperationDefinition Definition() => new("screen.capture",
        ArgumentSchema.Object([new("windowId", ArgumentSchema.String(34, 34))]),
        (args, token) => ValueTask.FromResult<JsonElement?>(Capture(args.GetProperty("windowId").GetString()!, token)),
        args => WindowReferenceRegistry.IsValidId(args.GetProperty("windowId").GetString()));

    internal OperationDefinition DesktopDefinition() => new("screen.capture_desktop",
        ArgumentSchema.Object([]),
        (_, token) => ValueTask.FromResult<JsonElement?>(CaptureDesktop(token)));

    private sealed record MonitorInfo(WindowBounds Bounds, bool Primary);

    private JsonElement CaptureDesktop(CancellationToken token)
    {
        var monitors = ReadMonitors(token);
        var left = monitors.Min(item => item.Bounds.X);
        var top = monitors.Min(item => item.Bounds.Y);
        var right = monitors.Max(item => (long)item.Bounds.X + item.Bounds.Width);
        var bottom = monitors.Max(item => (long)item.Bounds.Y + item.Bounds.Height);
        var width = right - left;
        var height = bottom - top;
        if (width <= 0 || height <= 0 || width > int.MaxValue || height > int.MaxValue
            || width * height > 32L * 1024 * 1024)
        { throw new WindowReferenceException(ErrorCode.CAPTURE_TOO_LARGE); }
        var bounds = new WindowBounds(left, top, (int)width, (int)height);
        using var output = new LimitedPngStream(16 * 1024 * 1024);
        try
        {
            using var bitmap = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format32bppRgb);
            using (var graphics = Graphics.FromImage(bitmap))
            using (var dpi = new PhysicalDpiScope())
            {
                graphics.Clear(Color.Black);
                foreach (var monitor in monitors)
                {
                    token.ThrowIfCancellationRequested();
                    var rect = monitor.Bounds;
                    graphics.CopyFromScreen(rect.X, rect.Y, checked(rect.X - left), checked(rect.Y - top),
                        new Size(rect.Width, rect.Height), CopyPixelOperation.SourceCopy | CopyPixelOperation.CaptureBlt);
                }
            }
            VerifyMonitors(monitors, token);
            bitmap.Save(output, ImageFormat.Png);
            VerifyMonitors(monitors, token);
            token.ThrowIfCancellationRequested();
            return JsonSerializer.SerializeToElement(new { width = bounds.Width, height = bounds.Height,
                bounds, source = "visible_desktop", byteLength = output.Length,
                monitors = monitors.Select((item, index) => new { index, bounds = item.Bounds, primary = item.Primary }),
                image = new { mimeType = "image/png", data = Convert.ToBase64String(output.ToArray()) } });
        }
        catch (Exception) when (output.Exceeded)
        { throw new WindowReferenceException(ErrorCode.CAPTURE_TOO_LARGE); }
        catch (Win32Exception exception)
        { throw new WindowReferenceException(exception.NativeErrorCode == 5 ? ErrorCode.ACCESS_DENIED : ErrorCode.CAPTURE_FAILED); }
        catch (ExternalException) { throw new WindowReferenceException(ErrorCode.CAPTURE_FAILED); }
    }

    private static void VerifyMonitors(MonitorInfo[] expected, CancellationToken token)
    {
        if (!ReadMonitors(token).SequenceEqual(expected))
        { throw new WindowReferenceException(ErrorCode.DESKTOP_CONFIGURATION_CHANGED); }
    }

    private static MonitorInfo[] ReadMonitors(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        using var dpi = new PhysicalDpiScope();
        var monitors = new List<MonitorInfo>();
        Exception? failure = null;
        var success = EnumDisplayMonitors(0, 0, (nint handle, nint dc, ref NativeRect rect, nint parameter) =>
        {
            try
            {
                token.ThrowIfCancellationRequested();
                if (monitors.Count >= 64) { throw new WindowReferenceException(ErrorCode.CAPTURE_TOO_LARGE); }
                var info = new NativeMonitorInfo { Size = (uint)Marshal.SizeOf<NativeMonitorInfo>() };
                if (!GetMonitorInfo(handle, ref info))
                { throw new WindowReferenceException(Marshal.GetLastWin32Error() == 5 ? ErrorCode.ACCESS_DENIED : ErrorCode.CAPTURE_FAILED); }
                var bounds = new WindowBounds(info.Monitor.Left, info.Monitor.Top,
                    checked(info.Monitor.Right - info.Monitor.Left), checked(info.Monitor.Bottom - info.Monitor.Top));
                if (bounds.Width <= 0 || bounds.Height <= 0)
                { throw new WindowReferenceException(ErrorCode.CAPTURE_TARGET_UNAVAILABLE); }
                monitors.Add(new(bounds, (info.Flags & 1) != 0));
                return true;
            }
            catch (Exception exception) { failure = exception; return false; }
        }, 0);
        token.ThrowIfCancellationRequested();
        if (failure is not null) { System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw(); }
        if (!success) { throw new WindowReferenceException(ErrorCode.CAPTURE_FAILED); }
        if (monitors.Count == 0) { throw new WindowReferenceException(ErrorCode.CAPTURE_TARGET_UNAVAILABLE); }
        return monitors.OrderBy(item => item.Bounds.X).ThenBy(item => item.Bounds.Y)
            .ThenBy(item => item.Bounds.Width).ThenBy(item => item.Bounds.Height).ThenBy(item => item.Primary).ToArray();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)]
    private struct NativeMonitorInfo { public uint Size; public NativeRect Monitor, Work; public uint Flags; }
    private delegate bool MonitorCallback(nint monitor, nint dc, ref NativeRect rect, nint parameter);
    [DllImport("user32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumDisplayMonitors(nint dc, nint clip, MonitorCallback callback, nint parameter);
    [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(nint monitor, ref NativeMonitorInfo info);

    internal JsonElement Capture(string windowId, CancellationToken token)
    {
        var state = discovery.Get(windowId, token);
        if (!state.Visible || state.Minimized)
        { throw new WindowReferenceException(ErrorCode.CAPTURE_TARGET_UNAVAILABLE); }
        var geometry = coordinates.GetGeometry(windowId, token);
        var bounds = geometry.ClientScreenBounds;
        if (bounds.Width <= 0 || bounds.Height <= 0)
        { throw new WindowReferenceException(ErrorCode.CAPTURE_TARGET_UNAVAILABLE); }
        if ((long)bounds.Width * bounds.Height > 16 * 1024 * 1024)
        { throw new WindowReferenceException(ErrorCode.CAPTURE_TOO_LARGE); }
        using var output = new LimitedPngStream(8 * 1024 * 1024);
        try
        {
            using var bitmap = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format32bppRgb);
            using (var graphics = Graphics.FromImage(bitmap))
            using (var dpi = new PhysicalDpiScope())
            using (var uncovered = new Region(new Rectangle(bounds.X, bounds.Y, bounds.Width, bounds.Height)))
            {
                foreach (var screen in Screen.AllScreens) { uncovered.Exclude(screen.Bounds); }
                if (!uncovered.IsEmpty(graphics))
                { throw new WindowReferenceException(ErrorCode.CAPTURE_TARGET_UNAVAILABLE); }
                token.ThrowIfCancellationRequested();
                graphics.CopyFromScreen(bounds.X, bounds.Y, 0, 0, new Size(bounds.Width, bounds.Height),
                    CopyPixelOperation.SourceCopy | CopyPixelOperation.CaptureBlt);
            }
            coordinates.VerifyGeometry(geometry, token);
            bitmap.Save(output, ImageFormat.Png);
            token.ThrowIfCancellationRequested();
            coordinates.VerifyGeometry(geometry, token);
            state = discovery.Get(windowId, token);
            if (!state.Visible || state.Minimized)
            { throw new WindowReferenceException(ErrorCode.CAPTURE_TARGET_UNAVAILABLE); }
            return JsonSerializer.SerializeToElement(new { windowId, width = bounds.Width, height = bounds.Height,
                bounds, source = "visible_client", byteLength = output.Length,
                image = new { mimeType = "image/png", data = Convert.ToBase64String(output.ToArray()) } });
        }
        catch (Exception) when (output.Exceeded)
        { throw new WindowReferenceException(ErrorCode.CAPTURE_TOO_LARGE); }
        catch (Win32Exception exception)
        { throw new WindowReferenceException(exception.NativeErrorCode == 5 ? ErrorCode.ACCESS_DENIED : ErrorCode.CAPTURE_FAILED); }
        catch (ExternalException) { throw new WindowReferenceException(ErrorCode.CAPTURE_FAILED); }
    }

    private sealed class LimitedPngStream(long maximumBytes) : MemoryStream
    {
        internal bool Exceeded { get; private set; }
        private void Check(long end)
        {
            if (end <= maximumBytes) { return; }
            Exceeded = true;
            throw new WindowReferenceException(ErrorCode.CAPTURE_TOO_LARGE);
        }
        public override void Write(byte[] buffer, int offset, int count)
        { Check(Position + count); base.Write(buffer, offset, count); }
        public override void Write(ReadOnlySpan<byte> buffer)
        { Check(Position + buffer.Length); base.Write(buffer); }
        public override void WriteByte(byte value) { Check(Position + 1); base.WriteByte(value); }
        public override void SetLength(long value) { Check(value); base.SetLength(value); }
    }
}
