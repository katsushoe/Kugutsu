using Kugutsu.Contracts;

namespace Kugutsu.App;

internal sealed record DomFrameReference(string Element, string DocumentRoot);
internal sealed record DomElementReference(string DomElementId, BrowserKind Kind, string BrowserId, string TabId,
    IReadOnlyList<DomFrameReference> Frames, string DocumentRoot, string NativeElement);

/// <summary>文書とフレームの不透明な要素参照を保持し、検索条件で再対応しない。</summary>
internal sealed class DomElementRegistry
{
    private readonly object _gate = new();
    private readonly Dictionary<string, DomElementReference> _entries = new(StringComparer.Ordinal);
    private readonly HashSet<string> _stale = new(StringComparer.Ordinal);
    private readonly Queue<string> _staleOrder = new();

    internal static bool ValidId(string? id) => id is { Length: 34 } && id.StartsWith("d_", StringComparison.Ordinal)
        && Guid.TryParseExact(id.AsSpan(2), "N", out _);

    internal IReadOnlyList<DomElementReference> Register(BrowserKind kind, string browserId, string tabId,
        IReadOnlyList<DomFrameReference> frames, string root, IReadOnlyList<string> elements, CancellationToken token)
    {
        lock (_gate)
        {
            token.ThrowIfCancellationRequested();
            if (_entries.Count + elements.Count > 10000) { throw new WindowReferenceException(ErrorCode.DOM_ELEMENT_LIMIT_EXCEEDED); }
            var frozenFrames = Array.AsReadOnly(frames.ToArray());
            var references = elements.Select(element => new DomElementReference("d_" + Guid.NewGuid().ToString("N"),
                kind, browserId, tabId, frozenFrames, root, element)).ToArray();
            foreach (var reference in references) { _entries.Add(reference.DomElementId, reference); }
            return Array.AsReadOnly(references);
        }
    }

    internal DomElementReference Resolve(string id, BrowserKind kind, string browserId, string tabId, CancellationToken token)
    {
        lock (_gate)
        {
            token.ThrowIfCancellationRequested();
            if (_stale.Contains(id)) { throw new WindowReferenceException(ErrorCode.DOM_ELEMENT_STALE); }
            if (!_entries.TryGetValue(id, out var entry) || entry.BrowserId != browserId || entry.TabId != tabId)
            { throw new WindowReferenceException(ErrorCode.DOM_ELEMENT_NOT_FOUND); }
            if (entry.Kind != kind) { throw new WindowReferenceException(ErrorCode.BROWSER_TYPE_MISMATCH); }
            return entry;
        }
    }

    internal void Invalidate(string id)
    {
        lock (_gate)
        {
            if (!_entries.Remove(id) || !_stale.Add(id)) { return; }
            _staleOrder.Enqueue(id);
            while (_staleOrder.Count > 10000) { _stale.Remove(_staleOrder.Dequeue()); }
        }
    }

    internal void InvalidateBrowser(string browserId)
    {
        lock (_gate)
        {
            foreach (var id in _entries.Values.Where(item => item.BrowserId == browserId).Select(item => item.DomElementId).ToArray())
            { Invalidate(id); }
        }
    }

    internal void InvalidateTab(string browserId, string tabId)
    {
        lock (_gate)
        {
            foreach (var id in _entries.Values.Where(item => item.BrowserId == browserId && item.TabId == tabId)
                .Select(item => item.DomElementId).ToArray()) { Invalidate(id); }
        }
    }
}
