using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Kugutsu.Contracts;
using Kugutsu.Hosting;
using Microsoft.Extensions.Logging;
using Tesseract;

namespace Kugutsu.App;

/// <summary>Fixed, local models and native binaries; images and recognition output stay in memory.</summary>
internal sealed class TesseractLocalOcr(InstallationPaths paths, KugutsuOptions settings, ILogger<TesseractLocalOcr> logger) : ILocalOcr
{
    private static readonly SemaphoreSlim NativeGate = new(1, 1);
    private static readonly object LoadingGate = new();
    private readonly Lazy<bool> _available = new(() => Probe(paths, logger));
    public bool IsAvailable => _available.Value;
    private static nint _leptonica, _tesseract;
    private static readonly (string Name, string Hash)[] Models = [
        ("eng.traineddata", "7D4322BD2A7749724879683FC3912CB542F19906C83BCC1A52132556427170B2"),
        ("jpn.traineddata", "1F5DE9236D2E85F5FDF4B3C500F2D4926F8D9449F28F5394472D9E8D83B91B4D")];
    private static readonly (string Name, string Hash)[] Libraries = [
        ("leptonica-1.82.0.dll", "1700330110ADA8E4F07FB063915E60E2B585AD87D9B1948093945E4645B66D08"),
        ("tesseract50.dll", "7F4873CDB78B9CD18C069EAE434D38DD14E987531866463357CF51C016241820")];

    public async ValueTask<OcrResult> RecognizeAsync(ReadOnlyMemory<byte> png, CancellationToken token)
    {
        var dimensions = Dimensions(png.Span);
        await NativeGate.WaitAsync(token);
        try
        {
            return await Task.Run(() => Recognize(png, dimensions.Width, dimensions.Height, token), token);
        }
        catch (OperationCanceledException) { throw; }
        catch (WindowReferenceException) { throw; }
        catch (Exception) { logger.LogDebug("[OCR] Recognition unavailable."); throw new WindowReferenceException(ErrorCode.UI_DRIVER_UNAVAILABLE); }
        finally { NativeGate.Release(); }
    }

    private OcrResult Recognize(ReadOnlyMemory<byte> png, int width, int height, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (Environment.Is64BitProcess || !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("TESSDATA_PREFIX")))
        { throw Unavailable(); }
        var modelPath = Path.Combine(paths.DataDirectory, "ocr", "tessdata_fast-4.1.0");
        var nativePath = Path.Combine(paths.BinDirectory, "x86");
        var leases = new List<FileStream>();
        try
        {
            foreach (var model in Models) { leases.Add(OpenVerified(Path.Combine(modelPath, model.Name), model.Hash, token)); }
            foreach (var library in Libraries) { leases.Add(OpenVerified(Path.Combine(nativePath, library.Name), library.Hash, token)); }
            // Keep trusted modules resident because the wrapper caches native delegates for process lifetime.
            LoadNative(paths);
            using var engine = new TesseractEngine(modelPath, settings.OcrLanguage, EngineMode.LstmOnly,
                Array.Empty<string>(), new Dictionary<string, object> { ["debug_file"] = "NUL" }, false);
            if (!engine.Version.StartsWith("5.2.0", StringComparison.Ordinal)) { throw Unavailable(); }
            token.ThrowIfCancellationRequested();
            using var image = Pix.LoadFromMemory(png.ToArray());
            if (image.Width != width || image.Height != height) { throw new WindowReferenceException(ErrorCode.INVALID_ARGUMENT); }
            using var page = engine.Process(image, PageSegMode.SparseText);
            using var iterator = page.GetIterator();
            var words = new List<OcrWord>();
            var truncated = false;
            iterator.Begin();
            do
            {
                token.ThrowIfCancellationRequested();
                var text = iterator.GetText(PageIteratorLevel.Word);
                if (string.IsNullOrWhiteSpace(text)) { continue; }
                if (words.Count >= settings.OcrMaxWords) { truncated = true; break; }
                if (text.Length > 4096 || !ControlTextSignalProvider.ValidText(text)
                    || !iterator.TryGetBoundingBox(PageIteratorLevel.Word, out var box)
                    || box.X1 < 0 || box.Y1 < 0 || box.X2 > width || box.Y2 > height || box.X2 <= box.X1 || box.Y2 <= box.Y1)
                { throw Unavailable(); }
                var confidence = iterator.GetConfidence(PageIteratorLevel.Word);
                if (!float.IsFinite(confidence) || confidence is < 0 or > 100) { throw Unavailable(); }
                words.Add(new(text, new(box.X1, box.Y1, box.X2 - box.X1, box.Y2 - box.Y1), confidence / 100d));
            } while (iterator.Next(PageIteratorLevel.Word));
            token.ThrowIfCancellationRequested();
            return new(words.AsReadOnly(), truncated, "tesseract-5.2.0", "tessdata_fast-4.1.0",
                settings.OcrLanguage, width, height);
        }
        finally { foreach (var lease in leases) { lease.Dispose(); } }
    }

    private static FileStream OpenVerified(string path, string hash, CancellationToken token)
    {
        string? ancestor = path;
        while (ancestor is not null)
        {
            token.ThrowIfCancellationRequested();
            if ((File.GetAttributes(ancestor) & FileAttributes.ReparsePoint) != 0) { throw Unavailable(); }
            ancestor = Path.GetDirectoryName(ancestor);
        }
        var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        try
        {
            if (stream.Length is <= 0 or > 16777216 || Convert.ToHexString(SHA256.HashData(stream)) != hash) { throw Unavailable(); }
            token.ThrowIfCancellationRequested();
            return stream;
        }
        catch { stream.Dispose(); throw; }
    }

    private static nint LoadOnce(nint loaded, string path)
    {
        if (loaded != 0) { return loaded; }
        var module = LoadLibraryEx(path, 0, 0x00000100 | 0x00000800); // DLL directory and System32 dependencies only.
        return module != 0 ? module : throw Unavailable();
    }

    private static void LoadNative(InstallationPaths paths)
    {
        lock (LoadingGate)
        {
            var directory = Path.Combine(paths.BinDirectory, "x86");
            _leptonica = LoadOnce(_leptonica, Path.Combine(directory, Libraries[0].Name));
            _tesseract = LoadOnce(_tesseract, Path.Combine(directory, Libraries[1].Name));
            TesseractEnviornment.CustomSearchPath = paths.BinDirectory;
        }
    }

    private static bool Probe(InstallationPaths paths, ILogger logger)
    {
        var leases = new List<FileStream>();
        try
        {
            if (Environment.Is64BitProcess || !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("TESSDATA_PREFIX"))) { throw Unavailable(); }
            foreach (var model in Models) { leases.Add(OpenVerified(Path.Combine(paths.DataDirectory, "ocr", "tessdata_fast-4.1.0", model.Name), model.Hash, CancellationToken.None)); }
            foreach (var library in Libraries) { leases.Add(OpenVerified(Path.Combine(paths.BinDirectory, "x86", library.Name), library.Hash, CancellationToken.None)); }
            LoadNative(paths);
            var version = Marshal.GetDelegateForFunctionPointer<NativeVersion>(NativeLibrary.GetExport(_tesseract, "TessVersion"))();
            return version != 0 && Marshal.PtrToStringAnsi(version, 5) == "5.2.0";
        }
        catch (Exception) { logger.LogDebug("[OCR] Fixed dependencies unavailable."); return false; }
        finally { foreach (var lease in leases) { lease.Dispose(); } }
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate nint NativeVersion();

    internal static (int Width, int Height) Dimensions(ReadOnlySpan<byte> png)
    {
        if (png.Length is < 33 or > 8388608 || !png[..8].SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 })
            || BinaryPrimitives.ReadUInt32BigEndian(png[8..12]) != 13 || !png[12..16].SequenceEqual("IHDR"u8))
        { throw new WindowReferenceException(ErrorCode.INVALID_ARGUMENT); }
        var width = BinaryPrimitives.ReadUInt32BigEndian(png[16..20]);
        var height = BinaryPrimitives.ReadUInt32BigEndian(png[20..24]);
        if (width is 0 or > 4096 || height is 0 or > 4096 || (ulong)width * height > 8388608)
        { throw new WindowReferenceException(ErrorCode.INVALID_ARGUMENT); }
        return ((int)width, (int)height);
    }

    private static WindowReferenceException Unavailable() => new(ErrorCode.UI_DRIVER_UNAVAILABLE);
    [DllImport("kernel32.dll", EntryPoint = "LoadLibraryExW", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern nint LoadLibraryEx(string path, nint file, uint flags);
}
