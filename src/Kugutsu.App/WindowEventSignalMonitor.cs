using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Threading.Channels;
using Kugutsu.Contracts;

namespace Kugutsu.App;

/// <summary>Out of context WinEvents are delivered on their installing message-loop thread.</summary>
internal sealed class WindowEventSignalMonitor : IAsyncDisposable
{
    private readonly WindowReferenceRegistry _windows;
    private readonly Channel<WindowReference> _events = Channel.CreateBounded<WindowReference>(
        new BoundedChannelOptions(256) { SingleReader = true, SingleWriter = true, FullMode = BoundedChannelFullMode.Wait });
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _ended = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly CancellationTokenSource _stop;
    private readonly Thread _thread;
    private uint _threadId;
    private uint _startTime;
    private Task _reader = Task.CompletedTask;

    private WindowEventSignalMonitor(WindowReferenceRegistry windows, CancellationToken lifetime)
    {
        _windows = windows;
        _stop = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
        _thread = new Thread(Pump) { IsBackground = true, Name = "Kugutsu.WinEventSignal" };
    }

    internal static async ValueTask<IAsyncDisposable> StartAsync(WindowReferenceRegistry windows,
        Func<WindowReference, CancellationToken, SignalObservation?> read, Action<SignalObservation> observe,
        CancellationToken startup, CancellationToken lifetime)
    {
        startup.ThrowIfCancellationRequested();
        var monitor = new WindowEventSignalMonitor(windows, lifetime);
        monitor._thread.Start();
        try
        {
            await monitor._ready.Task.WaitAsync(startup);
            observe(new(false, null));
            monitor._reader = monitor.ReadAsync(read, observe);
            return monitor;
        }
        catch { await monitor.DisposeAsync(); throw; }
    }

    private void Pump()
    {
        nint hook = 0;
        WinEventCallback callback = OnEvent;
        var rooted = GCHandle.Alloc(callback);
        try
        {
            _threadId = GetCurrentThreadId();
            PeekMessage(out _, 0, 0, 0, 0); // Ensure PostThreadMessage can address this thread.
            _startTime = unchecked((uint)Environment.TickCount);
            hook = SetWinEventHook(0x8000, 0x8002, 0, callback, 0, 0, 0);
            if (hook == 0) { throw new WindowReferenceException(ErrorCode.SIGNAL_CONDITION_UNSUPPORTED); }
            _ready.TrySetResult();
            using var cancel = _stop.Token.Register(() => PostThreadMessage(_threadId, 0x0012, 0, 0));
            while (!_stop.IsCancellationRequested)
            {
                var result = GetMessage(out var message, 0, 0, 0);
                if (result == 0) { break; }
                if (result < 0) { throw new WindowReferenceException(ErrorCode.SIGNAL_MONITOR_FAILED); }
                TranslateMessage(ref message);
                DispatchMessage(ref message);
            }
        }
        catch (Exception error)
        {
            var failure = error is WindowReferenceException expected ? expected
                : new WindowReferenceException(ErrorCode.SIGNAL_MONITOR_FAILED);
            _ready.TrySetException(failure);
            _events.Writer.TryComplete(failure);
        }
        finally
        {
            if (hook != 0 && !UnhookWinEvent(hook))
            { _events.Writer.TryComplete(new WindowReferenceException(ErrorCode.SIGNAL_MONITOR_FAILED)); }
            rooted.Free();
            _events.Writer.TryComplete();
            _ended.TrySetResult();
        }
    }

    private void OnEvent(nint hook, uint eventType, nint window, int objectId, int childId, uint thread, uint time)
    {
        if (_stop.IsCancellationRequested || eventType is not (0x8000 or 0x8002)
            || objectId != 0 || childId != 0 || window == 0 || unchecked((int)(time - _startTime)) < 0
            || GetAncestor(window, 2) != window) { return; }
        try
        {
            var reference = _windows.Register(window);
            if (!_events.Writer.TryWrite(reference))
            { _events.Writer.TryComplete(new WindowReferenceException(ErrorCode.SIGNAL_MONITOR_FAILED)); }
        }
        catch (WindowReferenceException error) when (error.Code is ErrorCode.WINDOW_NOT_FOUND or ErrorCode.ACCESS_DENIED) { }
        catch (Exception) { _events.Writer.TryComplete(new WindowReferenceException(ErrorCode.SIGNAL_MONITOR_FAILED)); }
    }

    private async Task ReadAsync(Func<WindowReference, CancellationToken, SignalObservation?> read,
        Action<SignalObservation> observe)
    {
        try
        {
            await foreach (var reference in _events.Reader.ReadAllAsync(_stop.Token))
            {
                SignalObservation? observation;
                try { observation = read(reference, _stop.Token); }
                catch (WindowReferenceException error) when (error.Code is ErrorCode.WINDOW_NOT_FOUND or ErrorCode.ACCESS_DENIED) { continue; }
                catch (Exception error) when (error is Win32Exception or ArgumentException or InvalidOperationException) { continue; }
                if (observation is null) { continue; }
                observe(observation);
                if (observation.Matched || observation.Error is not null) { return; }
            }
            if (!_stop.IsCancellationRequested) { observe(new(false, null, ErrorCode.SIGNAL_MONITOR_FAILED)); }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        catch (Exception) { observe(new(false, null, ErrorCode.SIGNAL_MONITOR_FAILED)); }
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        await _ended.Task;
        await _reader;
        _stop.Dispose();
    }

    private delegate void WinEventCallback(nint hook, uint eventType, nint window, int objectId, int childId, uint thread, uint time);
    [StructLayout(LayoutKind.Sequential)]
    private struct Message
    {
        public nint Window;
        public uint Id;
        public nuint WParam;
        public nint LParam;
        public uint Time;
        public int X, Y;
        public uint Private;
    }
    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern nint SetWinEventHook(uint minimum, uint maximum, nint module, WinEventCallback callback, uint process, uint thread, uint flags);
    [DllImport("user32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWinEvent(nint hook);
    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern nint GetAncestor(nint window, uint flags);
    [DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll", EntryPoint = "PeekMessageW", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PeekMessage(out Message message, nint window, uint minimum, uint maximum, uint flags);
    [DllImport("user32.dll", EntryPoint = "GetMessageW", ExactSpelling = true)]
    private static extern int GetMessage(out Message message, nint window, uint minimum, uint maximum);
    [DllImport("user32.dll", EntryPoint = "PostThreadMessageW", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostThreadMessage(uint thread, uint message, nuint wParam, nint lParam);
    [DllImport("user32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TranslateMessage(ref Message message);
    [DllImport("user32.dll", EntryPoint = "DispatchMessageW", ExactSpelling = true)]
    private static extern nint DispatchMessage(ref Message message);
}
