using System.Runtime.InteropServices;
using Kugutsu.Contracts;

namespace Kugutsu.App;

/// <summary>ネイティブ座標の取得を物理ピクセルで行い、元のスレッドDPI設定へ戻す。</summary>
internal sealed class PhysicalDpiScope : IDisposable
{
    private readonly nint _previous = SetThreadDpiAwarenessContext((nint)(-3)); // PER_MONITOR_AWARE
    private bool _disposed;

    internal PhysicalDpiScope()
    {
        if (_previous == 0) { throw new WindowReferenceException(ErrorCode.INTERNAL_ERROR); }
    }

    public void Dispose()
    {
        if (_disposed) { return; }
        _disposed = true;
        if (SetThreadDpiAwarenessContext(_previous) == 0)
        { throw new WindowReferenceException(ErrorCode.INTERNAL_ERROR); }
    }

    [DllImport("user32.dll", ExactSpelling = true, SetLastError = true)]
    private static extern nint SetThreadDpiAwarenessContext(nint context);
}
