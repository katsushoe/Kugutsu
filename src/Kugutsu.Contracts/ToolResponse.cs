using System.Text.Json;
using System.Text.Json.Serialization;

namespace Kugutsu.Contracts;

/// <summary>Shared schema version 1 payload for information, acceptance, status and errors.</summary>
public sealed class ToolResponse
{
    public const int CurrentSchemaVersion = 1;

    [JsonConstructor]
    public ToolResponse(int schemaVersion, bool ok, Guid? requestId, RequestState? state,
        JsonElement? data, ToolError? error)
    {
        if (schemaVersion != CurrentSchemaVersion) { throw new ArgumentOutOfRangeException(nameof(schemaVersion)); }
        if (requestId == Guid.Empty) { throw new ArgumentException("Request ID must be nonempty.", nameof(requestId)); }
        if (state is not null && (!Enum.IsDefined(state.Value) || requestId is null))
        { throw new ArgumentException("Request state requires a valid request ID."); }
        if (ok == (error is not null)) { throw new ArgumentException("Success and error are inconsistent."); }
        if (state is not null)
        {
            var failed = state is RequestState.Failed or RequestState.Cancelled or RequestState.TimedOut;
            if (ok == failed) { throw new ArgumentException("Success does not match request state."); }
        }
        ResponseJson.ValidateTerminalError(state == RequestState.Cancelled, state == RequestState.TimedOut, error);
        SchemaVersion = schemaVersion;
        Ok = ok;
        RequestId = requestId;
        State = state;
        Data = ResponseJson.CopyData(data);
        Error = error;
    }

    [JsonPropertyName("schemaVersion")] public int SchemaVersion { get; }
    [JsonPropertyName("ok")] public bool Ok { get; }
    [JsonPropertyName("requestId")] public Guid? RequestId { get; }
    [JsonPropertyName("state")] public RequestState? State { get; }
    [JsonPropertyName("data")] public JsonElement? Data { get; }
    [JsonPropertyName("error")] public ToolError? Error { get; }

    /// <summary>Returns immediate information without allocating a request.</summary>
    public static ToolResponse Information<T>(T data) =>
        new(CurrentSchemaVersion, true, null, null, ResponseJson.ToElement(data), null);

    /// <summary>Reports acceptance; it does not report completion of the operation.</summary>
    public static ToolResponse Accepted(Guid requestId) =>
        new(CurrentSchemaVersion, true, requestId, RequestState.Queued, null, null);

    /// <summary>Reports a rejection, optionally retaining a valid caller-supplied request ID.</summary>
    public static ToolResponse Rejected(ToolError error, Guid? requestId = null) =>
        new(CurrentSchemaVersion, false, requestId, null, null, error);

    /// <summary>Captures indexed operation results for an accepted request.</summary>
    public static ToolResponse Request(Guid requestId, RequestState state,
        IEnumerable<OperationResult> operations, ToolError? error = null)
    {
        ArgumentNullException.ThrowIfNull(operations);
        var snapshot = operations.ToArray();
        var stopped = false;
        for (var index = 0; index < snapshot.Length; index++)
        {
            if (snapshot[index] is null || snapshot[index].Index != index)
            { throw new ArgumentException("Operation results must have contiguous zero-based indices.", nameof(operations)); }
            var operationState = snapshot[index].State;
            if (stopped && operationState != OperationState.Skipped)
            { throw new ArgumentException("Operations after a stopped operation must be skipped.", nameof(operations)); }
            stopped = operationState != OperationState.Succeeded;
            if (state == RequestState.Succeeded && operationState != OperationState.Succeeded
                || state == RequestState.Running && operationState is not (OperationState.Succeeded or OperationState.Running)
                || state is RequestState.Failed or RequestState.Cancelled or RequestState.TimedOut && operationState == OperationState.Running)
            { throw new ArgumentException("Operation results do not match request state.", nameof(operations)); }
        }
        if (state == RequestState.Queued && snapshot.Length != 0)
        { throw new ArgumentException("Queued requests have no operation results.", nameof(operations)); }
        var data = ResponseJson.ToElement(new Dictionary<string, object> { ["operations"] = snapshot });
        return new(CurrentSchemaVersion, error is null, requestId, state, data, error);
    }
}
