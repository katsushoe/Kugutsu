using Kugutsu.Contracts;

namespace Kugutsu.App;

internal sealed record VisualLabel(string Text, UiBounds Bounds, double OcrConfidence, string Relation, double RuleScore);
internal sealed record LabeledVisualShape(VisualShape Shape, IReadOnlyList<VisualLabel> Labels);
internal sealed record VisualAssociations(IReadOnlyList<LabeledVisualShape> Shapes, bool Truncated);
internal sealed record VisualTextLabel(string Text, UiBounds Bounds, double Confidence);
internal sealed record VisualTextLabels(IReadOnlyList<VisualTextLabel> Labels, bool Truncated);

/// <summary>Spatial associations preserve multiple alternatives, without treating input contents as labels.</summary>
internal sealed class VisualLabelAssociator
{
    internal VisualTextLabels ReadLabels(OcrResult ocr, CancellationToken token)
    {
        if (ocr.Words.Count > 10000)
        { throw new WindowReferenceException(ErrorCode.INVALID_ARGUMENT); }
        var lines = new List<VisualTextLabel>();
        var words = new List<VisualTextLabel>();
        var truncated = ocr.Truncated;
        foreach (var word in ocr.Words.OrderBy(word => word.Bounds.Y).ThenBy(word => word.Bounds.X))
        {
            token.ThrowIfCancellationRequested();
            if (word.Bounds.Width <= 0 || word.Bounds.Height <= 0 || word.Bounds.X < 0 || word.Bounds.Y < 0
                || (long)word.Bounds.X + word.Bounds.Width > ocr.ImageWidth || (long)word.Bounds.Y + word.Bounds.Height > ocr.ImageHeight
                || !double.IsFinite(word.Confidence) || word.Confidence is < 0 or > 1
                || word.Text.Length > 4096 || !ControlTextSignalProvider.ValidText(word.Text))
            { throw new WindowReferenceException(ErrorCode.INVALID_ARGUMENT); }
            var text = word.Text.Trim();
            if (text.Length == 0) { continue; }
            words.Add(new(text, word.Bounds, word.Confidence));
            var grouped = false;
            for (var index = lines.Count - 1; index >= Math.Max(0, lines.Count - 64); index--)
            {
                var line = lines[index];
                var gap = word.Bounds.X - (line.Bounds.X + line.Bounds.Width);
                if (gap < 0 || gap > Math.Max(24, Math.Min(line.Bounds.Height, word.Bounds.Height) * 2)
                    || VerticalOverlap(line.Bounds, word.Bounds) < 0.5 || line.Text.Length + text.Length + 1 > 4096) { continue; }
                lines[index] = new(line.Text + " " + text, Union(line.Bounds, word.Bounds), Math.Min(line.Confidence, word.Confidence));
                grouped = true;
                break;
            }
            if (grouped) { continue; }
            if (lines.Count == 1000) { truncated = true; break; }
            lines.Add(new(text, word.Bounds, word.Confidence));
        }
        // Preserve single-word alternatives when a grouped line crosses a control boundary.
        foreach (var word in words)
        {
            token.ThrowIfCancellationRequested();
            if (lines.Contains(word)) { continue; }
            if (lines.Count == 2000) { truncated = true; break; }
            lines.Add(word);
        }
        return new(lines.AsReadOnly(), truncated);
    }

    internal VisualAssociations Associate(VisualShapes shapes, OcrResult ocr, CancellationToken token)
    {
        if (shapes.Shapes.Count > 1000) { throw new WindowReferenceException(ErrorCode.INVALID_ARGUMENT); }
        var text = ReadLabels(ocr, token);
        var lines = text.Labels;
        var truncated = shapes.Truncated || text.Truncated;
        var results = new List<LabeledVisualShape>();
        foreach (var shape in shapes.Shapes)
        {
            token.ThrowIfCancellationRequested();
            if (shape.Bounds.Width <= 0 || shape.Bounds.Height <= 0 || shape.Bounds.X < 0 || shape.Bounds.Y < 0
                || (long)shape.Bounds.X + shape.Bounds.Width > ocr.ImageWidth || (long)shape.Bounds.Y + shape.Bounds.Height > ocr.ImageHeight)
            { throw new WindowReferenceException(ErrorCode.INVALID_ARGUMENT); }
            var labels = new List<VisualLabel>();
            foreach (var line in lines)
            {
                string? relation = null;
                double score = 0;
                var bounds = shape.Bounds;
                if (shape.Kind == "button_candidate" && Contains(bounds, line.Bounds))
                { relation = "inside"; score = 1; }
                else if (shape.Kind == "input_candidate")
                {
                    var gap = bounds.X - (line.Bounds.X + line.Bounds.Width);
                    if (gap >= 0 && gap <= Math.Max(24, bounds.Height * 4) && VerticalOverlap(bounds, line.Bounds) >= 0.5)
                    { relation = "left"; score = 1 - gap / (double)Math.Max(25, bounds.Height * 4 + 1); }
                    else
                    {
                        gap = line.Bounds.X - (bounds.X + bounds.Width);
                        if (gap >= 0 && gap <= Math.Max(24, bounds.Height * 4) && VerticalOverlap(bounds, line.Bounds) >= 0.5)
                        { relation = "right"; score = 1 - gap / (double)Math.Max(25, bounds.Height * 4 + 1); }
                    }
                    if (relation is null)
                    {
                        gap = bounds.Y - (line.Bounds.Y + line.Bounds.Height);
                        var overlap = Math.Max(0, Math.Min(bounds.X + bounds.Width, line.Bounds.X + line.Bounds.Width) - Math.Max(bounds.X, line.Bounds.X));
                        if (gap >= 0 && gap <= bounds.Height * 2 && overlap >= Math.Min(bounds.Width, line.Bounds.Width) * 0.5)
                        { relation = "above"; score = 1 - gap / (double)(bounds.Height * 2 + 1); }
                    }
                }
                if (relation is null) { continue; }
                if (labels.Count == 16) { truncated = true; break; }
                labels.Add(new(line.Text, line.Bounds, line.Confidence, relation, score));
            }
            results.Add(new(shape, labels.AsReadOnly()));
        }
        return new(results.AsReadOnly(), truncated);
    }

    private static double VerticalOverlap(UiBounds a, UiBounds b) =>
        Math.Max(0, Math.Min(a.Y + a.Height, b.Y + b.Height) - Math.Max(a.Y, b.Y)) / (double)Math.Max(1, Math.Min(a.Height, b.Height));
    private static bool Contains(UiBounds outer, UiBounds inner) => inner.X >= outer.X && inner.Y >= outer.Y
        && (long)inner.X + inner.Width <= (long)outer.X + outer.Width && (long)inner.Y + inner.Height <= (long)outer.Y + outer.Height;
    private static UiBounds Union(UiBounds a, UiBounds b)
    {
        var left = Math.Min(a.X, b.X); var top = Math.Min(a.Y, b.Y);
        return new(left, top, Math.Max(a.X + a.Width, b.X + b.Width) - left, Math.Max(a.Y + a.Height, b.Y + b.Height) - top);
    }
}
