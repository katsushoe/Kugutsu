using System.Diagnostics;
using Kugutsu.Contracts;
using Kugutsu.Hosting;

namespace Kugutsu.App;

internal sealed record VisualFrame(WindowReference Window, WindowInfo Context, UiVisualStamp Stamp, byte[] Png, VisualRaster Raster);
internal sealed record VisualElementHandle(string SnapshotId, VisualMeaning Meaning);

/// <summary>Element references retain frame IDs, not image buffers; expired frames can release memory.</summary>
internal sealed class VisualFrameRegistry : IDisposable
{
    private readonly object _gate = new();
    private readonly Dictionary<string, VisualFrame> _frames = new(StringComparer.Ordinal);
    private readonly TimeSpan _lifetime;
    private readonly System.Threading.Timer _expiry;
    private bool _disposed;

    public VisualFrameRegistry(KugutsuOptions settings)
    {
        _lifetime = TimeSpan.FromSeconds(settings.VisualSnapshotLifetimeSeconds);
        _expiry = new System.Threading.Timer(_ => { lock (_gate) { if (!_disposed) { Prune(); } } }, null, 1000, 1000);
    }

    internal void Add(string id, VisualFrame frame, CancellationToken token)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            token.ThrowIfCancellationRequested();
            Prune();
            if (Stopwatch.GetElapsedTime(frame.Stamp.CapturedAt) >= _lifetime)
            { throw new WindowReferenceException(ErrorCode.UI_ELEMENT_STALE); }
            if (_frames.Count >= 16 || _frames.Values.Sum(Bytes) + Bytes(frame) > 134217728)
            { throw new WindowReferenceException(ErrorCode.UI_ELEMENT_LIMIT_EXCEEDED); }
            _frames.Add(id, frame);
        }
    }

    internal VisualFrame Get(string id, CancellationToken token)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            token.ThrowIfCancellationRequested();
            Prune();
            return _frames.TryGetValue(id, out var frame) ? frame : throw new WindowReferenceException(ErrorCode.UI_ELEMENT_STALE);
        }
    }
    internal void Remove(string id) { lock (_gate) { _frames.Remove(id); } }
    private static long Bytes(VisualFrame frame) => (long)frame.Png.Length + frame.Raster.Luminance.Length + frame.Raster.Pixels.Length;
    private void Prune()
    {
        foreach (var pair in _frames.Where(pair => Stopwatch.GetElapsedTime(pair.Value.Stamp.CapturedAt) >= _lifetime).ToArray())
        { _frames.Remove(pair.Key); }
    }
    public void Dispose() { lock (_gate) { _disposed = true; _expiry.Dispose(); _frames.Clear(); } }
}
