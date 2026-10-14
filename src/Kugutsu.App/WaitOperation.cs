using System.Diagnostics;
using System.Text.Json;

namespace Kugutsu.App;

/// <summary>FIFOワーカーを保持しながら、協調取消可能な固定時間待機を行う。</summary>
internal static class WaitOperation
{
    internal static OperationDefinition Definition() => new("wait",
        ArgumentSchema.Object([new("durationMs", ArgumentSchema.Integer(0, 30000))]),
        async (args, token) =>
        {
            token.ThrowIfCancellationRequested();
            var durationMs = args.GetProperty("durationMs").GetInt32();
            var started = Stopwatch.GetTimestamp();
            var duration = TimeSpan.FromMilliseconds(durationMs);
            var elapsed = Stopwatch.GetElapsedTime(started);
            while (elapsed < duration)
            {
                await Task.Delay(Math.Max(1, (int)Math.Ceiling((duration - elapsed).TotalMilliseconds)), token);
                elapsed = Stopwatch.GetElapsedTime(started);
            }
            token.ThrowIfCancellationRequested();
            return JsonSerializer.SerializeToElement(new
            {
                durationMs, elapsedMs = (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds
            });
        });
}
