using System.ComponentModel;
using Kugutsu.Contracts;
using Kugutsu.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Kugutsu.App;

[McpServerToolType]
internal sealed class WindowTools(WindowDiscovery discovery, ILogger<WindowTools> logger)
{
    [McpServerTool(Name = "get_window", ReadOnly = true, UseStructuredContent = true,
        OutputSchemaType = typeof(ToolResponse))]
    [Description("windowIdを再照合し、現在のタイトル・プロセス情報・表示状態・画面座標の物理ピクセル矩形を取得します。未知・失効参照はWINDOW_NOT_FOUNDです。")]
    public CallToolResult GetWindow(string windowId, CancellationToken cancellationToken = default)
    {
        try
        { return McpToolResponseAdapter.ToCallResult(ToolResponse.Information(discovery.Get(windowId, cancellationToken))); }
        catch (WindowReferenceException exception)
        { return McpToolResponseAdapter.ToCallResult(ToolResponse.Rejected(new ToolError(exception.Code))); }
        catch (OperationCanceledException)
        { return McpToolResponseAdapter.ToCallResult(ToolResponse.Rejected(new ToolError(ErrorCode.CANCELLED))); }
        catch (Exception)
        {
            logger.WriteEvent(LogLevel.Error, LogEvent.OperationCompleted, LogOutcome.Failed,
                operation: LogOperation.Window, error: LogError.UnexpectedFailure);
            return McpToolResponseAdapter.ToCallResult(ToolResponse.Rejected(new ToolError(ErrorCode.INTERNAL_ERROR)));
        }
    }

    [McpServerTool(Name = "list_windows", ReadOnly = true, UseStructuredContent = true,
        OutputSchemaType = typeof(ToolResponse))]
    [Description("同じユーザー・対話セッションのトップレベルウィンドウを一覧表示します。titleContainsはタイトル部分一致、processNameは拡張子なしのプロセス名完全一致で、大文字小文字を区別しません。省略・空文字はフィルタなし。windows、truncated、skippedCountを返します。")]
    public CallToolResult ListWindows(string? titleContains = null, string? processName = null,
        CancellationToken cancellationToken = default)
    {
        try
        { return McpToolResponseAdapter.ToCallResult(ToolResponse.Information(discovery.List(titleContains, processName, cancellationToken))); }
        catch (WindowReferenceException exception)
        { return McpToolResponseAdapter.ToCallResult(ToolResponse.Rejected(new ToolError(exception.Code))); }
        catch (OperationCanceledException)
        { return McpToolResponseAdapter.ToCallResult(ToolResponse.Rejected(new ToolError(ErrorCode.CANCELLED))); }
        catch (Exception)
        {
            logger.WriteEvent(LogLevel.Error, LogEvent.OperationCompleted, LogOutcome.Failed,
                operation: LogOperation.Window, error: LogError.UnexpectedFailure);
            return McpToolResponseAdapter.ToCallResult(ToolResponse.Rejected(new ToolError(ErrorCode.INTERNAL_ERROR)));
        }
    }
}
