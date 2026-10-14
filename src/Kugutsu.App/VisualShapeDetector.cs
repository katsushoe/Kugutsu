using Kugutsu.Contracts;
using Kugutsu.Hosting;

namespace Kugutsu.App;

internal sealed record VisualShape(string Kind, UiBounds Bounds, double Score, IReadOnlyList<UiEvidence> Evidence);
internal sealed record VisualShapes(IReadOnlyList<VisualShape> Shapes, bool Truncated);

/// <summary>Geometric candidates carry rule evidence; they do not prove editability or internal control type.</summary>
internal sealed class VisualShapeDetector(KugutsuOptions settings)
{
    private sealed record Edge(int Left, int Right, int Y, double Coverage);

    internal VisualShapes FindInputs(VisualRaster image, int maximum, CancellationToken token)
        => FindRectangles(image, maximum, "input_candidate", 40, 12, 128, 2.5, double.PositiveInfinity, token);

    internal VisualShapes FindButtons(VisualRaster image, int maximum, CancellationToken token)
        => FindRectangles(image, maximum, "button_candidate", 20, 12, 96, 1, 12, token);

    private VisualShapes FindRectangles(VisualRaster image, int maximum, string kind, int minimumWidth,
        int minimumHeight, int maximumHeight, double minimumAspect, double maximumAspect, CancellationToken token)
    {
        if (maximum is < 1 or > 1000 || image.Width <= 0 || image.Height <= 0
            || image.Luminance.Length != (long)image.Width * image.Height)
        { throw new WindowReferenceException(ErrorCode.INVALID_ARGUMENT); }
        var edges = new List<Edge>();
        var truncated = false;
        for (var y = 1; y < image.Height; y++)
        {
            token.ThrowIfCancellationRequested();
            var start = -1;
            var hits = 0;
            var lastHit = -1;
            for (var x = 1; x < image.Width; x++)
            {
                var hit = Math.Abs(image.At(x, y) - image.At(x, y - 1)) >= settings.VisualEdgeContrast;
                if (hit) { if (start < 0) { start = x; } hits++; lastHit = x; }
                if (start >= 0 && (x - lastHit > 2 || x == image.Width - 1))
                {
                    var end = lastHit + 1;
                    if (end - start >= minimumWidth)
                    {
                        if (edges.Count == 4096) { truncated = true; break; }
                        edges.Add(new(start, end, y, hits / (double)(end - start)));
                    }
                    start = -1; hits = 0;
                }
            }
            if (truncated) { break; }
        }
        var shapes = new List<VisualShape>();
        var evaluated = 0;
        for (var first = 0; first < edges.Count; first++)
        {
            token.ThrowIfCancellationRequested();
            var top = edges[first];
            for (var second = first + 1; second < edges.Count; second++)
            {
                var bottom = edges[second];
                var height = bottom.Y - top.Y;
                if (height > maximumHeight) { break; }
                if (height < minimumHeight || Math.Abs(top.Left - bottom.Left) > 3 || Math.Abs(top.Right - bottom.Right) > 3) { continue; }
                if (++evaluated > 65536) { truncated = true; goto Finished; }
                var left = Math.Max(top.Left, bottom.Left);
                var right = Math.Min(top.Right, bottom.Right);
                var width = right - left;
                var aspect = width / (double)height;
                if (width < minimumWidth || aspect < minimumAspect || aspect > maximumAspect) { continue; }
                var leftCoverage = VerticalCoverage(image, left, top.Y, bottom.Y);
                var rightCoverage = VerticalCoverage(image, right, top.Y, bottom.Y);
                var flatness = InteriorFlatness(image, left, right, top.Y, bottom.Y, token);
                if (leftCoverage < 0.6 || rightCoverage < 0.6 || flatness < 0.7) { continue; }
                var bounds = new UiBounds(left, top.Y, width, height);
                var score = (top.Coverage + bottom.Coverage + leftCoverage + rightCoverage + flatness) / 5;
                var shape = new VisualShape(kind, bounds, score, Array.AsReadOnly(new[] {
                    new UiEvidence("shape", RuleId: "rectangle_edges_v1", Bounds: bounds),
                    new UiEvidence("shape", RuleId: kind == "input_candidate" ? "wide_flat_interior_v1" : "compact_flat_interior_v1", Bounds: bounds) }));
                var existing = shapes.FindIndex(candidate => Overlap(candidate.Bounds, bounds) >= 0.85);
                if (existing >= 0) { if (score > shapes[existing].Score) { shapes[existing] = shape; } continue; }
                if (shapes.Count == maximum) { truncated = true; goto Finished; }
                shapes.Add(shape);
            }
        }
    Finished:
        token.ThrowIfCancellationRequested();
        return new(Array.AsReadOnly(shapes.OrderBy(shape => shape.Bounds.Y).ThenBy(shape => shape.Bounds.X).ToArray()), truncated);
    }

    private double VerticalCoverage(VisualRaster image, int column, int top, int bottom)
    {
        var count = 0;
        for (var y = top + 2; y < bottom - 2; y++)
        {
            var found = false;
            for (var x = Math.Max(1, column - 3); x <= Math.Min(image.Width - 1, column + 3); x++)
            { found |= Math.Abs(image.At(x, y) - image.At(x - 1, y)) >= settings.VisualEdgeContrast; }
            if (found) { count++; }
        }
        return count / (double)Math.Max(1, bottom - top - 4);
    }

    private double InteriorFlatness(VisualRaster image, int left, int right, int top, int bottom, CancellationToken token)
    {
        var count = 0;
        var flat = 0;
        for (var y = top + 3; y < bottom - 3; y += 2)
        {
            token.ThrowIfCancellationRequested();
            for (var x = left + 3; x < right - 3; x += 2)
            { count++; if (Math.Abs(image.At(x, y) - image.At(x - 1, y)) < settings.VisualEdgeContrast) { flat++; } }
        }
        return flat / (double)Math.Max(1, count);
    }

    internal static double Overlap(UiBounds a, UiBounds b)
    {
        var intersection = (long)Math.Max(0, Math.Min(a.X + a.Width, b.X + b.Width) - Math.Max(a.X, b.X))
            * Math.Max(0, Math.Min(a.Y + a.Height, b.Y + b.Height) - Math.Max(a.Y, b.Y));
        var union = (long)a.Width * a.Height + (long)b.Width * b.Height - intersection;
        return union > 0 ? intersection / (double)union : 0;
    }
}
