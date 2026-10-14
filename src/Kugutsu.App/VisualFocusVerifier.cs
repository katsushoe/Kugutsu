using Kugutsu.Contracts;

namespace Kugutsu.App;

internal sealed record VisualFocusProof(string ElementId, string SnapshotId, string RuleId, double Coverage);

/// <summary>Confirms the configured focus display without clicking or sending keys.</summary>
internal sealed class VisualFocusVerifier(VisualReferenceValidator references)
{
    internal async ValueTask<VisualFocusProof> VerifyAsync(UiOperationTarget target, CancellationToken token)
    {
        var first = await references.ValidateAsync(target, token, compareControlImage: false);
        var rule = first.Handle.Meaning.Rule;
        if (first.Handle.Meaning.Source.Shape.Kind != "input_candidate" || rule?.FocusIndicator is not { } indicator)
        { throw Unverified(); }
        var coverage = Measure(first.Current, first.Handle.Meaning.Source.Shape.Bounds, indicator, token);
        if (coverage < indicator.Coverage) { throw Unverified(); }
        // A second capture rejects a transient border; every capture also rechecks window/profile identity.
        await Task.Delay(50, token);
        var second = await references.ValidateAsync(target, token, compareControlImage: false);
        var repeated = Measure(second.Current, second.Handle.Meaning.Source.Shape.Bounds, indicator, token);
        if (repeated < indicator.Coverage) { throw Unverified(); }
        return new(target.ElementId, first.Handle.SnapshotId, rule.RuleId, Math.Min(coverage, repeated));
    }

    private static double Measure(VisualRaster raster, UiBounds bounds, VisualFocusIndicator indicator, CancellationToken token)
    {
        var left = bounds.X + indicator.Inset;
        var top = bounds.Y + indicator.Inset;
        var right = bounds.X + bounds.Width - 1 - indicator.Inset;
        var bottom = bounds.Y + bounds.Height - 1 - indicator.Inset;
        if (left < 0 || top < 0 || right >= raster.Width || bottom >= raster.Height || left >= right || top >= bottom)
        { throw Unverified(); }
        var color = Convert.ToInt32(indicator.Color[1..], 16);
        var red = color >> 16 & 255;
        var green = color >> 8 & 255;
        var blue = color & 255;
        var matching = 0;
        var total = 0;
        void Sample(int x, int y)
        {
            token.ThrowIfCancellationRequested();
            var offset = (y * raster.Width + x) * 4;
            total++;
            if (Math.Abs(raster.Pixels[offset] - blue) <= indicator.Tolerance
                && Math.Abs(raster.Pixels[offset + 1] - green) <= indicator.Tolerance
                && Math.Abs(raster.Pixels[offset + 2] - red) <= indicator.Tolerance) { matching++; }
        }
        for (var x = left; x <= right; x++) { Sample(x, top); Sample(x, bottom); }
        for (var y = top + 1; y < bottom; y++) { Sample(left, y); Sample(right, y); }
        return (double)matching / total;
    }

    private static WindowReferenceException Unverified() => new(ErrorCode.UI_TARGET_UNVERIFIED);
}
