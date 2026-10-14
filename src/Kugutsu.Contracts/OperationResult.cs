using System.Text.Json;
using System.Text.Json.Serialization;

namespace Kugutsu.Contracts;

/// <summary>An immutable snapshot of one operation's observable result.</summary>
public sealed class OperationResult
{
    [JsonConstructor]
    public OperationResult(int index, string name, OperationState state,
        DateTimeOffset? startedAt, DateTimeOffset? finishedAt, JsonElement? data, ToolError? error)
    {
        if (index < 0) { throw new ArgumentOutOfRangeException(nameof(index)); }
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (!Enum.IsDefined(state)) { throw new ArgumentOutOfRangeException(nameof(state)); }
        var failure = state is OperationState.Failed or OperationState.Cancelled or OperationState.TimedOut;
        if (failure != (error is not null)) { throw new ArgumentException("Error does not match operation state."); }
        if (state == OperationState.Skipped && (startedAt is not null || finishedAt is not null || data is not null))
        { throw new ArgumentException("Skipped operations have no timestamps or data."); }
        if (state == OperationState.Running && (startedAt is null || finishedAt is not null))
        { throw new ArgumentException("Running operations require only a start time."); }
        if (state != OperationState.Running && state != OperationState.Skipped && finishedAt is null)
        { throw new ArgumentException("Finished operations require a finish time."); }
        if (state == OperationState.Succeeded && startedAt is null)
        { throw new ArgumentException("Succeeded operations require a start time."); }
        if (startedAt is not null && finishedAt < startedAt)
        { throw new ArgumentException("Finish time precedes start time."); }
        ResponseJson.ValidateTerminalError(state == OperationState.Cancelled, state == OperationState.TimedOut, error);
        Index = index;
        Name = name;
        State = state;
        StartedAt = startedAt?.ToUniversalTime();
        FinishedAt = finishedAt?.ToUniversalTime();
        Data = ResponseJson.CopyData(data);
        Error = error;
    }

    [JsonPropertyName("index")] public int Index { get; }
    [JsonPropertyName("name")] public string Name { get; }
    [JsonPropertyName("state")] public OperationState State { get; }
    [JsonPropertyName("startedAt")] public DateTimeOffset? StartedAt { get; }
    [JsonPropertyName("finishedAt")] public DateTimeOffset? FinishedAt { get; }
    [JsonPropertyName("data")] public JsonElement? Data { get; }
    [JsonPropertyName("error")] public ToolError? Error { get; }
}
