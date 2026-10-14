using System.Buffers.Binary;
using System.Text.Json;

namespace Kugutsu.Hosting;

/// <summary>Private helper protocol; bounded UTF-8 JSON frames never contain exception diagnostics.</summary>
internal static class AloeWire
{
    internal const int MaximumFrameBytes = 16777216;
    internal static JsonElement Read(Stream stream)
    {
        var header = new byte[4];
        stream.ReadExactly(header);
        var size = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (size is <= 0 or > MaximumFrameBytes) { throw new InvalidDataException("Invalid helper frame."); }
        var body = new byte[size];
        stream.ReadExactly(body);
        using var document = JsonDocument.Parse(body, new() { MaxDepth = 32 });
        return document.RootElement.Clone();
    }
    internal static void Write(Stream stream, object value)
    {
        var body = JsonSerializer.SerializeToUtf8Bytes(value);
        if (body.Length is <= 0 or > MaximumFrameBytes) { throw new InvalidDataException("Helper frame too large."); }
        var header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, body.Length);
        stream.Write(header);
        stream.Write(body);
        stream.Flush();
    }
    internal static async ValueTask<JsonElement> ReadAsync(Stream stream, CancellationToken token)
    {
        var header = new byte[4];
        await stream.ReadExactlyAsync(header, token);
        var size = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (size is <= 0 or > MaximumFrameBytes) { throw new InvalidDataException("Invalid helper frame."); }
        var body = new byte[size];
        await stream.ReadExactlyAsync(body, token);
        using var document = JsonDocument.Parse(body, new() { MaxDepth = 32 });
        return document.RootElement.Clone();
    }
    internal static async ValueTask WriteAsync(Stream stream, object value, CancellationToken token)
    {
        var body = JsonSerializer.SerializeToUtf8Bytes(value);
        if (body.Length is <= 0 or > MaximumFrameBytes) { throw new InvalidDataException("Helper frame too large."); }
        var header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, body.Length);
        await stream.WriteAsync(header, token);
        await stream.WriteAsync(body, token);
        await stream.FlushAsync(token);
    }
}
