using System.Text.Json;
using Kugutsu.Contracts;
using Kugutsu.Hosting;

namespace Kugutsu.App;

/// <summary>A compiled helper is executed once and stopped before this method completes.</summary>
internal sealed class AloeRuntimeAdapter(KugutsuOptions settings, AloeOperationBridge bridge)
{
    internal async ValueTask<JsonElement?> RunAsync(AloeCompiledSession session, OperationExecutionContext context, CancellationToken token)
    {
        session.BeginExecution();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(settings.RequestTimeoutSeconds));
        try
        {
            await AloeWire.WriteAsync(session.Input, new { command = "run", maximumInstructions = settings.MaxAloeInstructions }, deadline.Token);
            var calls = 0;
            long instructions = 0;
            var pointer = -1;
            while (true)
            {
                var response = await AloeWire.ReadAsync(session.Output, deadline.Token);
                var status = response.GetProperty("status").GetString();
                if (status is "completed" or "error")
                {
                    if (response.TryGetProperty("instructions", out var executed)) { instructions = executed.GetInt64(); }
                    if (response.TryGetProperty("instructionPointer", out var position)) { pointer = position.GetInt32(); }
                    if (status == "error")
                    {
                        var code = response.GetProperty("code").GetString() == "ALOE_LIMIT_EXCEEDED"
                            ? ErrorCode.ALOE_LIMIT_EXCEEDED : ErrorCode.ALOE_RUNTIME_ERROR;
                        throw new WindowReferenceException(code, JsonSerializer.SerializeToElement(new { stage = "runtime", instructionPointer = pointer,
                            executedInstructions = instructions, hostCallCount = calls }));
                    }
                    break;
                }
                if (status != "operation" || response.GetProperty("sequence").GetInt32() != checked(calls + 1))
                { throw new WindowReferenceException(ErrorCode.ALOE_RUNTIME_ERROR); }
                if (calls >= settings.MaxBatchOperations)
                { throw new WindowReferenceException(ErrorCode.ALOE_LIMIT_EXCEEDED,
                    JsonSerializer.SerializeToElement(new { stage = "host_call", hostCallIndex = calls, hostCallCount = calls })); }
                var result = await bridge.ExecuteAsync(response.GetProperty("name").GetString()!, response.GetProperty("args").GetString()!,
                    context, deadline.Token);
                calls++;
                var data = result?.GetRawText() ?? "null";
                if (System.Text.Encoding.UTF8.GetByteCount(data) > AloeWire.MaximumFrameBytes - 4096)
                { throw new WindowReferenceException(ErrorCode.ALOE_LIMIT_EXCEEDED,
                    JsonSerializer.SerializeToElement(new { stage = "host_result", hostCallIndex = calls - 1, hostCallCount = calls })); }
                await AloeWire.WriteAsync(session.Input, new { status = "operation_result", sequence = calls, data }, deadline.Token);
            }
            token.ThrowIfCancellationRequested();
            return JsonSerializer.SerializeToElement(new { executed = true, compiledInstructionCount = session.InstructionCount,
                executedInstructions = instructions, instructionPointer = pointer,
                functionCount = session.FunctionCount, hostCallCount = calls, applicationResultVerified = false });
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        { throw new WindowReferenceException(ErrorCode.REQUEST_TIMEOUT); }
        catch (Exception error) when (error is IOException or JsonException)
        { throw new WindowReferenceException(ErrorCode.ALOE_RUNTIME_ERROR); }
        finally { await session.DisposeAsync(); }
    }
}
