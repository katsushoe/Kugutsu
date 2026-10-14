using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text.Json;
using Kugutsu.Contracts;

namespace Kugutsu.App;

internal sealed class VisualValueReader(VisualReferenceValidator references, ILocalOcr ocr)
{
    internal async ValueTask<JsonElement?> ReadAsync(UiOperationTarget target, JsonElement arguments, CancellationToken token)
    {
        if (!arguments.TryGetProperty("method", out var method) || method.GetString() != "local_ocr")
        { throw new WindowReferenceException(ErrorCode.INVALID_ARGUMENT); }
        var initial = await references.ValidateAsync(target, token);
        if (initial.Handle.Meaning.Source.Shape.Kind != "input_candidate" || initial.Handle.Meaning.Rule?.Sensitive != false)
        { throw new WindowReferenceException(ErrorCode.UI_TARGET_UNVERIFIED); }
        var bounds = initial.Handle.Meaning.Source.Shape.Bounds;
        // Crop only the declared non-sensitive input interior; border and surrounding labels are excluded.
        var interior = new UiBounds(bounds.X + 2, bounds.Y + 2, bounds.Width - 4, bounds.Height - 4);
        var png = await Task.Run(() => Crop(initial.Current, interior, token), token);
        var text = await ocr.RecognizeAsync(png, token);
        await references.ValidateAsync(target, token);
        var words = text.Words.OrderBy(word => word.Bounds.X).ToArray();
        if (text.Truncated || words.Length == 0 || words.Any(word => word.Bounds.X < 0 || word.Bounds.Y < 0
                || word.Bounds.Width <= 0 || word.Bounds.Height <= 0
                || (long)word.Bounds.X + word.Bounds.Width > interior.Width || (long)word.Bounds.Y + word.Bounds.Height > interior.Height
                || !double.IsFinite(word.Confidence) || word.Confidence is < 0 or > 1
                || !ControlTextSignalProvider.ValidText(word.Text) || word.Text.Any(char.IsControl)))
        { throw new WindowReferenceException(ErrorCode.UI_TARGET_UNVERIFIED); }
        var overlapTop = words.Max(word => word.Bounds.Y);
        var overlapBottom = words.Min(word => word.Bounds.Y + word.Bounds.Height);
        if (overlapBottom - overlapTop < words.Min(word => word.Bounds.Height) / 2.0)
        { throw new WindowReferenceException(ErrorCode.UI_TARGET_UNVERIFIED); }
        var value = string.Join(" ", words.Select(word => word.Text));
        if (value.Length > 65536) { throw new WindowReferenceException(ErrorCode.UI_TARGET_UNVERIFIED); }
        return JsonSerializer.SerializeToElement(new { windowId = target.Window.WindowId, elementId = target.ElementId,
            driver = target.Driver, method = "local_ocr", value, source = "ocr", exact = false, estimated = true, confidence = words.Min(word => word.Confidence),
            words = words.Select(word => new { text = word.Text, confidence = word.Confidence,
                bounds = new UiBounds(word.Bounds.X + interior.X, word.Bounds.Y + interior.Y, word.Bounds.Width, word.Bounds.Height) }) });
    }

    private static byte[] Crop(VisualRaster raster, UiBounds bounds, CancellationToken token)
    {
        if (bounds.Width <= 0 || bounds.Height <= 0) { throw new WindowReferenceException(ErrorCode.UI_TARGET_UNVERIFIED); }
        try
        {
            using var bitmap = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format32bppArgb);
            var locked = bitmap.LockBits(new(0, 0, bounds.Width, bounds.Height), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
            try
            {
                for (var y = 0; y < bounds.Height; y++)
                {
                    token.ThrowIfCancellationRequested();
                    Marshal.Copy(raster.Pixels, ((bounds.Y + y) * raster.Width + bounds.X) * 4,
                        locked.Scan0 + checked(y * locked.Stride), bounds.Width * 4);
                }
            }
            finally { bitmap.UnlockBits(locked); }
            using var stream = new MemoryStream();
            bitmap.Save(stream, ImageFormat.Png);
            token.ThrowIfCancellationRequested();
            return stream.ToArray();
        }
        catch (ExternalException) { throw new WindowReferenceException(ErrorCode.CAPTURE_FAILED); }
        catch (ArgumentException) { throw new WindowReferenceException(ErrorCode.CAPTURE_FAILED); }
    }
}
