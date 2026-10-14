using System.ComponentModel;
using Kugutsu.Contracts;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Kugutsu.App;

/// <summary>Reads one request result without disclosing another logical owner's work.</summary>
[McpServerToolType]
internal sealed class GetRequestTools
{
    private readonly OperationRequestQueue _queue;
    private readonly IHttpContextAccessor _httpContextAccessor;

    public GetRequestTools(OperationRequestQueue queue, IHttpContextAccessor httpContextAccessor)
    {
        _queue = queue;
        _httpContextAccessor = httpContextAccessor;
    }

    [McpServerTool(Name = "get_request", ReadOnly = true, UseStructuredContent = true,
        OutputSchemaType = typeof(ToolResponse))]
    [Description("要求IDを指定し、呼出し元の論理セッションが所有する要求状態と操作結果を取得します。他の所有者の要求は存在しない場合と同じ応答です。")]
    public CallToolResult GetRequest(string requestId)
    {
        ToolResponse response;
        if (!Guid.TryParseExact(requestId, "D", out var id) || id == Guid.Empty)
        { response = ToolResponse.Rejected(new ToolError(ErrorCode.INVALID_ARGUMENT)); }
        else if (_httpContextAccessor.HttpContext is not { } context)
        { response = ToolResponse.Rejected(new ToolError(ErrorCode.INTERNAL_ERROR), id); }
        else
        {
            try
            {
                var owner = LogicalSessions.GetOwner(context);
                response = _queue.TryGet(id, owner.Id, out var snapshot) && snapshot is not null
                    ? snapshot
                    : ToolResponse.Rejected(new ToolError(ErrorCode.REQUEST_NOT_FOUND), id);
            }
            catch (InvalidOperationException)
            { response = ToolResponse.Rejected(new ToolError(ErrorCode.INTERNAL_ERROR), id); }
        }
        return McpToolResponseAdapter.ToCallResult(response);
    }
}

internal static class McpToolResponseAdapter
{
    public static CallToolResult ToCallResult(ToolResponse response)
    {
        var structured = JsonSerializer.SerializeToNode(response)!;
        var images = new List<ContentBlock>();
        if (structured["data"] is JsonObject requestData && requestData["operations"] is JsonArray operations)
        {
            foreach (var operation in operations)
            {
                if (operation?["name"]?.GetValue<string>() is not ("screen.capture" or "screen.capture_desktop" or "ui.snapshot")
                    || operation["data"]?["image"] is not JsonObject image) { continue; }
                if (image["data"] is not JsonValue data) { continue; }
                images.Add(new ImageContentBlock { MimeType = "image/png", Data = Convert.FromBase64String(data.GetValue<string>()) });
                image.Remove("data");
                image["contentIndex"] = images.Count;
            }
        }
        var json = JsonSerializer.SerializeToElement(structured);
        return new CallToolResult
        {
            StructuredContent = json,
            Content = [new TextContentBlock { Text = json.GetRawText() }, .. images],
            IsError = !response.Ok
        };
    }
}
