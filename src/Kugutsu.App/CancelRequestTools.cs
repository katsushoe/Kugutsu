using System.ComponentModel;
using Kugutsu.Contracts;
using Kugutsu.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Kugutsu.App;

/// <summary>Cancels an owned request while it is still waiting in the operation queue.</summary>
[McpServerToolType]
internal sealed class CancelRequestTools
{
    private readonly OperationRequestQueue _queue;
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly ILogger<CancelRequestTools> _logger;

    public CancelRequestTools(OperationRequestQueue queue, IHttpContextAccessor httpContextAccessor,
        ILogger<CancelRequestTools> logger)
    {
        _queue = queue;
        _httpContextAccessor = httpContextAccessor;
        _logger = logger;
    }

    [McpServerTool(Name = "cancel_request", UseStructuredContent = true,
        OutputSchemaType = typeof(ToolResponse))]
    [Description("要求IDを指定し、呼出し元が所有する要求を取り消します。待機中はキューから除去します。実行中は協調的な取消を通知し、完了済みの場合は状態を変更せず現在の状態を返します。")]
    public CallToolResult CancelRequest(string requestId)
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
                if (!_queue.TryCancel(id, owner.Id, out var current, out var cancelled,
                    out var timedOut, out var cancellationRequested, out var newlyRequested) || current is null)
                { response = ToolResponse.Rejected(new ToolError(ErrorCode.REQUEST_NOT_FOUND), id); }
                else
                {
                    response = current;
                    if (cancelled)
                    {
                        _logger.WriteEvent(LogLevel.Information, LogEvent.RequestCompleted, LogOutcome.Cancelled,
                            requestId: id, error: LogError.Cancelled);
                    }
                    else if (timedOut)
                    {
                        _logger.WriteEvent(LogLevel.Warning, LogEvent.RequestCompleted, LogOutcome.TimedOut,
                            requestId: id, error: LogError.Timeout);
                    }
                    else if (cancellationRequested && newlyRequested)
                    {
                        _logger.WriteEvent(LogLevel.Information, LogEvent.RequestCancellationRequested,
                            LogOutcome.Started, requestId: id);
                    }
                }
            }
            catch (InvalidOperationException)
            { response = ToolResponse.Rejected(new ToolError(ErrorCode.INTERNAL_ERROR), id); }
        }
        return McpToolResponseAdapter.ToCallResult(response);
    }
}
