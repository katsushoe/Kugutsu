using System.Text.Json.Serialization;

namespace Kugutsu.Contracts;

/// <summary>States of an accepted request. These names are part of schema version 1.</summary>
[JsonConverter(typeof(RequestStateConverter))]
public enum RequestState
{
    [JsonStringEnumMemberName("queued")] Queued,
    [JsonStringEnumMemberName("running")] Running,
    [JsonStringEnumMemberName("succeeded")] Succeeded,
    [JsonStringEnumMemberName("failed")] Failed,
    [JsonStringEnumMemberName("cancelled")] Cancelled,
    [JsonStringEnumMemberName("timed_out")] TimedOut
}

/// <summary>States of one operation, including operations skipped after a failure.</summary>
[JsonConverter(typeof(OperationStateConverter))]
public enum OperationState
{
    [JsonStringEnumMemberName("running")] Running,
    [JsonStringEnumMemberName("succeeded")] Succeeded,
    [JsonStringEnumMemberName("failed")] Failed,
    [JsonStringEnumMemberName("cancelled")] Cancelled,
    [JsonStringEnumMemberName("timed_out")] TimedOut,
    [JsonStringEnumMemberName("skipped")] Skipped
}

/// <summary>Writes named request states and rejects numeric enum input.</summary>
public sealed class RequestStateConverter() : JsonStringEnumConverter<RequestState>(null, false);

/// <summary>Writes named operation states and rejects numeric enum input.</summary>
public sealed class OperationStateConverter() : JsonStringEnumConverter<OperationState>(null, false);
