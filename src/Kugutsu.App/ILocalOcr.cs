namespace Kugutsu.App;

using Kugutsu.Contracts;

internal sealed record OcrWord(string Text, UiBounds Bounds, double Confidence);
internal sealed record OcrResult(IReadOnlyList<OcrWord> Words, bool Truncated,
    string Engine, string ModelVersion, string Language, int ImageWidth, int ImageHeight);
internal interface ILocalOcr
{
    bool IsAvailable { get; }
    ValueTask<OcrResult> RecognizeAsync(ReadOnlyMemory<byte> png, CancellationToken token);
}
