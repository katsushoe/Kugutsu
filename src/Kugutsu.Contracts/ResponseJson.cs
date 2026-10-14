using System.Text.Json;
using System.Text.Json.Serialization;

namespace Kugutsu.Contracts;

/// <summary>Canonical response serialization independent of host serializer defaults.</summary>
public static class ResponseJson
{
    private static readonly JsonSerializerOptions Options = CreateOptions();

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.Never
        };
        options.MakeReadOnly(populateMissingResolver: true);
        return options;
    }

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);

    public static JsonElement ToElement<T>(T value) => JsonSerializer.SerializeToElement(value, Options);

    internal static JsonElement? CopyData(JsonElement? data)
    {
        if (data?.ValueKind == JsonValueKind.Undefined)
        { throw new ArgumentException("Undefined JSON data cannot be serialized.", nameof(data)); }
        return data?.Clone();
    }

    internal static void ValidateTerminalError(bool cancelled, bool timedOut, ToolError? error)
    {
        if (cancelled && error?.Code != ErrorCode.CANCELLED
            || timedOut && error?.Code is not (ErrorCode.TIMEOUT or ErrorCode.REQUEST_TIMEOUT))
        { throw new ArgumentException("Terminal state requires its corresponding error code."); }
    }
}
