using System.Runtime.InteropServices;
using System.Text.Json;
using System.Diagnostics;
using Kugutsu.Contracts;

namespace Kugutsu.App;

/// <summary>対象アプリの応答を同期的に待たず、状態変更と結果確認を行う。</summary>
internal sealed class WindowStateOperations(WindowReferenceRegistry registry, WindowDiscovery discovery)
{
    internal OperationDefinition ActivateDefinition() => new("window.activate",
        ArgumentSchema.Object([new("windowId", ArgumentSchema.String(34, 34))]),
        async (arguments, token) => JsonSerializer.SerializeToElement(
            await ActivateAsync(arguments.GetProperty("windowId").GetString()!, token)),
        arguments => WindowReferenceRegistry.IsValidId(arguments.GetProperty("windowId").GetString()));

    internal async ValueTask<WindowInfo> ActivateAsync(string windowId, CancellationToken cancellationToken)
    {
        var current = discovery.Get(windowId, cancellationToken);
        if (current.Minimized || !current.Visible)
        { await RestoreAsync(windowId, cancellationToken); }
        cancellationToken.ThrowIfCancellationRequested();
        var reference = registry.Resolve(windowId);
        if (!HasInputFocus(reference) && !SetForegroundWindow(reference.Identity.Handle))
        {
            registry.Resolve(windowId);
            throw new WindowReferenceException(ErrorCode.FOCUS_FAILED);
        }
        var started = Stopwatch.GetTimestamp();
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            reference = registry.Resolve(windowId);
            if (HasInputFocus(reference))
            {
                current = discovery.Get(windowId, cancellationToken);
                VerifyInputTarget(windowId, cancellationToken);
                if (current.Visible && !current.Minimized) { return current; }
            }
            if (Stopwatch.GetElapsedTime(started) >= TimeSpan.FromSeconds(1))
            { throw new WindowReferenceException(ErrorCode.FOCUS_FAILED); }
            await Task.Delay(50, cancellationToken);
        }
    }

    /// <summary>後続の入力送信直前にも、前面ウィンドウとフォーカスを確認する。</summary>
    internal WindowReference VerifyInputTarget(string windowId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var reference = registry.Resolve(windowId);
        if (!HasInputFocus(reference))
        {
            registry.Resolve(windowId);
            throw new WindowReferenceException(ErrorCode.FOCUS_FAILED);
        }
        registry.Resolve(windowId);
        cancellationToken.ThrowIfCancellationRequested();
        return reference;
    }

    private static bool HasInputFocus(WindowReference reference)
    {
        var handle = reference.Identity.Handle;
        if (GetForegroundWindow() != handle || !IsWindowEnabled(handle)) { return false; }
        var info = new GuiThreadInfo { Size = (uint)Marshal.SizeOf<GuiThreadInfo>() };
        if (!GetGUIThreadInfo(reference.Identity.ThreadId, ref info))
        {
            if (Marshal.GetLastWin32Error() == 5)
            { throw new WindowReferenceException(ErrorCode.ACCESS_DENIED); }
            return false;
        }
        // メニュー・移動／サイズ変更中には通常の入力先として扱わない。
        if ((info.Flags & 0x1e) != 0 || info.Active != handle || info.Focus == 0
            || !IsWindowEnabled(info.Focus)
            || (info.Focus != handle && !IsChild(handle, info.Focus))) { return false; }
        if (GetWindowThreadProcessId(info.Focus, out var processId) == 0
            || processId != reference.Identity.ProcessId) { return false; }
        return GetForegroundWindow() == handle;
    }

    internal OperationDefinition RestoreDefinition() => new("window.restore",
        ArgumentSchema.Object([new("windowId", ArgumentSchema.String(34, 34))]),
        async (arguments, token) => JsonSerializer.SerializeToElement(
            await RestoreAsync(arguments.GetProperty("windowId").GetString()!, token)),
        arguments => WindowReferenceRegistry.IsValidId(arguments.GetProperty("windowId").GetString()));

    internal async ValueTask<WindowInfo> RestoreAsync(string windowId, CancellationToken cancellationToken)
        => await ChangeStateAsync(windowId, 9, IsRestored, cancellationToken); // SW_RESTORE

    internal OperationDefinition MinimizeDefinition() => new("window.minimize",
        ArgumentSchema.Object([new("windowId", ArgumentSchema.String(34, 34))]),
        async (arguments, token) => JsonSerializer.SerializeToElement(
            await MinimizeAsync(arguments.GetProperty("windowId").GetString()!, token)),
        arguments => WindowReferenceRegistry.IsValidId(arguments.GetProperty("windowId").GetString()));

    internal async ValueTask<WindowInfo> MinimizeAsync(string windowId, CancellationToken cancellationToken)
        => await ChangeStateAsync(windowId, 6, info => info.Minimized, cancellationToken); // SW_MINIMIZE

    internal OperationDefinition MaximizeDefinition() => new("window.maximize",
        ArgumentSchema.Object([new("windowId", ArgumentSchema.String(34, 34))]),
        async (arguments, token) => JsonSerializer.SerializeToElement(
            await MaximizeAsync(arguments.GetProperty("windowId").GetString()!, token)),
        arguments => WindowReferenceRegistry.IsValidId(arguments.GetProperty("windowId").GetString()));

    internal async ValueTask<WindowInfo> MaximizeAsync(string windowId, CancellationToken cancellationToken)
        => await ChangeStateAsync(windowId, 3,
            info => info.Visible && !info.Minimized && info.Maximized, cancellationToken); // SW_MAXIMIZE

    internal OperationDefinition CloseDefinition() => new("window.close",
        ArgumentSchema.Object([new("windowId", ArgumentSchema.String(34, 34))]),
        async (arguments, token) => await CloseAsync(arguments.GetProperty("windowId").GetString()!, token),
        arguments => WindowReferenceRegistry.IsValidId(arguments.GetProperty("windowId").GetString()));

    internal async ValueTask<JsonElement> CloseAsync(string windowId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var reference = registry.Resolve(windowId);
        if (!PostMessage(reference.Identity.Handle, 0x0010, 0, 0)) // WM_CLOSE
        {
            var error = Marshal.GetLastWin32Error();
            if (error == 5) { throw new WindowReferenceException(ErrorCode.ACCESS_DENIED); }
            registry.Resolve(windowId);
            throw new WindowReferenceException(ErrorCode.WINDOW_STATE_CHANGE_FAILED);
        }
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (registry.HasWindowEnded(reference))
            {
                cancellationToken.ThrowIfCancellationRequested();
                return JsonSerializer.SerializeToElement(new { windowId, closed = true });
            }
            await Task.Delay(50, cancellationToken);
        }
    }

    private async ValueTask<WindowInfo> ChangeStateAsync(string windowId, int command,
        Func<WindowInfo, bool> matches, CancellationToken cancellationToken)
    {
        if (!WindowReferenceRegistry.IsValidId(windowId))
        { throw new WindowReferenceException(ErrorCode.INVALID_ARGUMENT); }
        var current = discovery.Get(windowId, cancellationToken);
        if (matches(current)) { return current; }

        cancellationToken.ThrowIfCancellationRequested();
        var reference = registry.Resolve(windowId);
        // 戻り値は非同期要求の開始だけを示すため、状態を別途読み返す。
        if (!ShowWindowAsync(reference.Identity.Handle, command))
        {
            registry.Resolve(windowId);
            throw new WindowReferenceException(ErrorCode.WINDOW_STATE_CHANGE_FAILED);
        }
        while (true)
        {
            current = discovery.Get(windowId, cancellationToken);
            if (matches(current)) { return current; }
            // 上限はキューの操作・要求期限で制御する。待機中も協調取消する。
            await Task.Delay(50, cancellationToken);
        }
    }

    private static bool IsRestored(WindowInfo info) => info.Visible && !info.Minimized && !info.Maximized;

    [StructLayout(LayoutKind.Sequential)]
    private struct GuiThreadInfo
    {
        public uint Size, Flags;
        public nint Active, Focus, Capture, MenuOwner, MoveSize, Caret;
        public int CaretLeft, CaretTop, CaretRight, CaretBottom;
    }

    [DllImport("user32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(nint handle);
    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern nint GetForegroundWindow();
    [DllImport("user32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetGUIThreadInfo(uint threadId, ref GuiThreadInfo info);
    [DllImport("user32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsChild(nint parent, nint child);
    [DllImport("user32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowEnabled(nint handle);
    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern uint GetWindowThreadProcessId(nint handle, out uint processId);

    [DllImport("user32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindowAsync(nint handle, int command);

    [DllImport("user32.dll", EntryPoint = "PostMessageW", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessage(nint handle, uint message, nuint wParam, nint lParam);
}
