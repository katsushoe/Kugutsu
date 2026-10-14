using System.Text.Json;
using System.Text.Json.Serialization;

namespace Kugutsu.Contracts;

/// <summary>MCP tools/call result with matching structured and compatibility text content.</summary>
public sealed class ToolCallResult
{
    public ToolCallResult(ToolResponse response)
    {
        ArgumentNullException.ThrowIfNull(response);
        StructuredContent = ResponseJson.ToElement(response);
        Content = Array.AsReadOnly(new[] { new TextContent(StructuredContent.GetRawText()) });
        IsError = !response.Ok;
    }

    [JsonPropertyName("structuredContent")] public JsonElement StructuredContent { get; }
    [JsonPropertyName("content")] public IReadOnlyList<TextContent> Content { get; }
    [JsonPropertyName("isError")] public bool IsError { get; }
}

/// <summary>The serialized structured response for clients reading text content.</summary>
public sealed class TextContent
{
    internal TextContent(string text) { Text = text; }

    [JsonPropertyName("type")] public string Type => "text";
    [JsonPropertyName("text")] public string Text { get; }
}
