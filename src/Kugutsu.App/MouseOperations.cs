using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Kugutsu.Contracts;

namespace Kugutsu.App;

internal sealed class MouseOperations(WindowCoordinates coordinates, WindowStateOperations windows)
{
    internal OperationDefinition ClickDefinition() => CreateClickDefinition("mouse.click", 1);

    internal OperationDefinition DoubleClickDefinition() => CreateClickDefinition("mouse.double_click", 2);

    internal OperationDefinition ScrollDefinition() => new("mouse.scroll",
        ArgumentSchema.Object([
            new("windowId", ArgumentSchema.String(34, 34)),
            new("x", ArgumentSchema.Integer(0, int.MaxValue)),
            new("y", ArgumentSchema.Integer(0, int.MaxValue)),
            new("delta", ArgumentSchema.Integer(-12000, 12000)),
            new("axis", ArgumentSchema.String(10, 8, ["vertical", "horizontal"]), false)]),
        async (args, token) => await ScrollAsync(args.GetProperty("windowId").GetString()!,
            new(args.GetProperty("x").GetInt32(), args.GetProperty("y").GetInt32()),
            args.GetProperty("delta").GetInt32(),
            args.TryGetProperty("axis", out var axis) ? axis.GetString()! : "vertical", token),
        args => WindowReferenceRegistry.IsValidId(args.GetProperty("windowId").GetString())
            && args.GetProperty("delta").GetInt32() is var delta && delta != 0 && delta % 120 == 0);

    private async ValueTask<JsonElement> ScrollAsync(string windowId, PixelPoint point, int delta, string axis,
        CancellationToken token)
    {
        var conversion = await PreparePointerAsync(windowId, point, token);
        var flags = axis == "vertical" ? 0x0800u : 0x1000u; // WHEEL / HWHEEL
        token.ThrowIfCancellationRequested();
        if (SendInput(1, [MouseInput(flags, data: unchecked((uint)delta))], Marshal.SizeOf<Input>()) != 1)
        { throw new WindowReferenceException(ErrorCode.INPUT_SEND_FAILED); }
        return JsonSerializer.SerializeToElement(new { windowId, x = point.X, y = point.Y, delta, axis,
            inputSent = true, screenPoint = new { x = conversion.Point.X, y = conversion.Point.Y } });
    }

    private OperationDefinition CreateClickDefinition(string name, int clickCount) => new(name,
        ArgumentSchema.Object([
            new("windowId", ArgumentSchema.String(34, 34)),
            new("x", ArgumentSchema.Integer(0, int.MaxValue)),
            new("y", ArgumentSchema.Integer(0, int.MaxValue)),
            new("button", ArgumentSchema.String(6, 4, ["left", "right", "middle"]), false)]),
        async (args, token) => await ClickAsync(args.GetProperty("windowId").GetString()!,
            new(args.GetProperty("x").GetInt32(), args.GetProperty("y").GetInt32()),
            args.TryGetProperty("button", out var button) ? button.GetString()! : "left", clickCount, token),
        args => WindowReferenceRegistry.IsValidId(args.GetProperty("windowId").GetString()));

    internal async ValueTask<JsonElement> ClickAsync(string windowId, PixelPoint point, string button, int clickCount,
        CancellationToken token, Func<CancellationToken, ValueTask>? verifyTarget = null)
    {
        var conversion = await PreparePointerAsync(windowId, point, token);
        if (verifyTarget is not null) { await verifyTarget(token); }
        VerifyTarget(windowId, conversion, token);
        if (!CursorMatches(conversion.Point)) { throw new WindowReferenceException(ErrorCode.INPUT_SEND_FAILED); }
        var (down, up) = button switch
        {
            "left" => (0x0002u, 0x0004u),
            "right" => (0x0008u, 0x0010u),
            "middle" => (0x0020u, 0x0040u),
            _ => throw new WindowReferenceException(ErrorCode.INVALID_ARGUMENT)
        };
        token.ThrowIfCancellationRequested();
        Input[] inputs = clickCount == 2
            ? [MouseInput(down), MouseInput(up), MouseInput(down), MouseInput(up)]
            : [MouseInput(down), MouseInput(up)];
        var sent = SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Input>());
        if (sent != inputs.Length)
        {
            // 押下だけ受理された場合、取消状態でも解放を1回試みる。クリックは再送しない。
            if (sent % 2 == 1 && SendInput(1, [MouseInput(up)], Marshal.SizeOf<Input>()) != 1)
            { throw new WindowReferenceException(ErrorCode.INPUT_RELEASE_FAILED); }
            throw new WindowReferenceException(ErrorCode.INPUT_SEND_FAILED);
        }
        if (clickCount == 2)
        {
            return JsonSerializer.SerializeToElement(new { windowId, x = point.X, y = point.Y, button,
                clickCount, inputSent = true,
                screenPoint = new { x = conversion.Point.X, y = conversion.Point.Y } });
        }
        return JsonSerializer.SerializeToElement(new { windowId, x = point.X, y = point.Y, button,
            inputSent = true, screenPoint = new { x = conversion.Point.X, y = conversion.Point.Y } });
    }

    private async ValueTask<CoordinateConversion> PreparePointerAsync(string windowId, PixelPoint point,
        CancellationToken token)
    {
        await windows.ActivateAsync(windowId, token);
        var conversion = coordinates.ClientToScreen(windowId, point, token);
        var desktop = conversion.Geometry.VirtualDesktopBounds;
        var move = MouseInput(0xc001, Normalize(conversion.Point.X, desktop.X, desktop.Width),
            Normalize(conversion.Point.Y, desktop.Y, desktop.Height)); // MOVE | ABSOLUTE | VIRTUALDESK
        VerifyTarget(windowId, conversion, token);
        if (SendInput(1, [move], Marshal.SizeOf<Input>()) != 1)
        { throw new WindowReferenceException(ErrorCode.INPUT_SEND_FAILED); }
        var started = Stopwatch.GetTimestamp();
        while (!CursorMatches(conversion.Point))
        {
            token.ThrowIfCancellationRequested();
            if (Stopwatch.GetElapsedTime(started) >= TimeSpan.FromSeconds(1))
            { throw new WindowReferenceException(ErrorCode.INPUT_SEND_FAILED); }
            await Task.Delay(10, token);
        }
        VerifyTarget(windowId, conversion, token);
        if (!CursorMatches(conversion.Point))
        { throw new WindowReferenceException(ErrorCode.INPUT_SEND_FAILED); }
        return conversion;
    }

    private void VerifyTarget(string windowId, CoordinateConversion conversion, CancellationToken token)
    {
        coordinates.VerifyGeometry(conversion.Geometry, token);
        var reference = windows.VerifyInputTarget(windowId, token);
        using var dpi = new PhysicalDpiScope();
        var hit = WindowFromPoint(new() { X = conversion.Point.X, Y = conversion.Point.Y });
        if (hit == 0 || (hit != reference.Identity.Handle && GetAncestor(hit, 2) != reference.Identity.Handle))
        { throw new WindowReferenceException(ErrorCode.FOCUS_FAILED); }
        var gui = new GuiThreadInfo { Size = (uint)Marshal.SizeOf<GuiThreadInfo>() };
        if (!GetGUIThreadInfo(0, ref gui) || gui.Capture != 0)
        { throw new WindowReferenceException(ErrorCode.INPUT_STATE_UNSAFE); }
        // 利用者の押下と合成入力を混ぜない。押下状態を変更して解除しない。
        foreach (var key in new[] { 1, 2, 4, 5, 6, 16, 17, 18, 91, 92 })
        {
            if ((GetAsyncKeyState(key) & 0x8000) != 0)
            { throw new WindowReferenceException(ErrorCode.INPUT_STATE_UNSAFE); }
        }
        windows.VerifyInputTarget(windowId, token);
    }

    private static int Normalize(int value, int origin, int size)
    {
        var relative = (long)value - origin;
        var normalized = (2 * relative + 1) * 65536 / (2L * size);
        if (normalized is < 0 or > 65535 || normalized * size / 65536 != relative)
        { throw new WindowReferenceException(ErrorCode.INVALID_ARGUMENT); }
        return (int)normalized;
    }

    private static bool CursorMatches(PixelPoint point)
    {
        using var dpi = new PhysicalDpiScope();
        if (!GetCursorPos(out var cursor))
        { throw new WindowReferenceException(ErrorCode.INPUT_SEND_FAILED); }
        return cursor.X == point.X && cursor.Y == point.Y;
    }

    private static Input MouseInput(uint flags, int x = 0, int y = 0, uint data = 0) =>
        new() { Type = 0, Mouse = new() { X = x, Y = y, Flags = flags, Data = data } }; // INPUT_MOUSE

    [StructLayout(LayoutKind.Sequential)]
    private struct Input { public uint Type; public NativeMouse Mouse; }
    [StructLayout(LayoutKind.Sequential)]
    private struct NativeMouse { public int X, Y; public uint Data, Flags, Time; public nuint Extra; }
    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)]
    private struct GuiThreadInfo
    {
        public uint Size, Flags;
        public nint Active, Focus, Capture, MenuOwner, MoveSize, Caret;
        public int Left, Top, Right, Bottom;
    }
    [DllImport("user32.dll", ExactSpelling = true, SetLastError = true)]
    private static extern uint SendInput(uint count, Input[] inputs, int size);
    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern nint WindowFromPoint(NativePoint point);
    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern nint GetAncestor(nint handle, uint flags);
    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out NativePoint point);
    [DllImport("user32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetGUIThreadInfo(uint threadId, ref GuiThreadInfo info);
}
