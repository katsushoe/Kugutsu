using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using Kugutsu.Contracts;

namespace Kugutsu.App;

internal sealed class VisualRaster(int width, int height, byte[] luminance, byte[] pixels)
{
    internal int Width { get; } = width;
    internal int Height { get; } = height;
    internal byte[] Luminance { get; } = luminance;
    internal byte[] Pixels { get; } = pixels;
    internal int At(int x, int y) => Luminance[y * Width + x];

    internal static VisualRaster Decode(ReadOnlyMemory<byte> png, CancellationToken token)
    {
        var dimensions = TesseractLocalOcr.Dimensions(png.Span);
        token.ThrowIfCancellationRequested();
        try
        {
            using var stream = new MemoryStream(png.ToArray(), false);
            using var source = new Bitmap(stream);
            if (source.Width != dimensions.Width || source.Height != dimensions.Height)
            { throw new WindowReferenceException(ErrorCode.INVALID_ARGUMENT); }
            using var bitmap = new Bitmap(source.Width, source.Height, PixelFormat.Format32bppArgb);
            using (var graphics = Graphics.FromImage(bitmap))
            { graphics.Clear(Color.Black); graphics.DrawImageUnscaled(source, 0, 0); }
            var gray = new byte[checked(bitmap.Width * bitmap.Height)];
            var color = new byte[checked(gray.Length * 4)];
            var locked = bitmap.LockBits(new(0, 0, bitmap.Width, bitmap.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            try
            {
                for (var y = 0; y < bitmap.Height; y++)
                {
                    token.ThrowIfCancellationRequested();
                    var offset = y * bitmap.Width * 4;
                    Marshal.Copy(locked.Scan0 + checked(y * locked.Stride), color, offset, bitmap.Width * 4);
                    for (var x = 0; x < bitmap.Width; x++)
                    { gray[y * bitmap.Width + x] = (byte)((77 * color[offset + x * 4 + 2] + 150 * color[offset + x * 4 + 1] + 29 * color[offset + x * 4]) >> 8); }
                }
            }
            finally { bitmap.UnlockBits(locked); }
            return new(bitmap.Width, bitmap.Height, gray, color);
        }
        catch (ArgumentException) { throw new WindowReferenceException(ErrorCode.INVALID_ARGUMENT); }
        catch (ExternalException) { throw new WindowReferenceException(ErrorCode.CAPTURE_FAILED); }
    }
}
