using System.ComponentModel;
using System.Text.Json;
using Kugutsu.Contracts;
using Kugutsu.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Kugutsu.App;

/// <summary>所有者のロックを確認して監視管理を実行するMCPツール。</summary>
[McpServerToolType]
internal sealed class SignalTools(SignalRegistration registration, OperationRequestQueue queue,
    KugutsuOptions settings, IHttpContextAccessor accessor, ILogger<SignalTools> logger, ExecuteOperationTools executor)
{
    [McpServerTool(Name = "register_signal", UseStructuredContent = true, OutputSchemaType = typeof(ToolResponse))]
    [Description("取得中のMCPロックに条件を登録します。監視開始と初期評価を確認してsignalId・state・expiresAtを返します。未対応の条件は拒否します。")]
    public async Task<CallToolResult> RegisterSignal(string lockToken, JsonElement condition, int? retentionSeconds = null)
    {
        if (!OperationRequestQueue.IsLockTokenFormatValid(lockToken) || retentionSeconds is < 1 or > 3600
            || !SignalConditionSchema.Instance.Accepts(condition)) { return Result(ToolResponse.Rejected(new ToolError(ErrorCode.INVALID_ARGUMENT))); }
        if (accessor.HttpContext is not { } http) { return Result(ToolResponse.Rejected(new ToolError(ErrorCode.INTERNAL_ERROR))); }
        try
        {
            var owner = LogicalSessions.GetOwner(http);
            var lockId = queue.RequireSignalLock(owner.Id, lockToken);
            var data = await registration.RegisterAsync(owner.Id, lockId, condition,
                retentionSeconds ?? settings.SignalRetentionSeconds, http.RequestAborted);
            return Result(ToolResponse.Information(data));
        }
        catch (WindowReferenceException error) { return Result(ToolResponse.Rejected(new ToolError(error.Code))); }
        catch (OperationCanceledException) { return Result(ToolResponse.Rejected(new ToolError(ErrorCode.CANCELLED))); }
        catch (Exception error)
        {
            logger.WriteEvent(LogLevel.Error, LogEvent.UnhandledFailure, LogOutcome.Failed, error: AuditLog.Classify(error));
            return Result(ToolResponse.Rejected(new ToolError(ErrorCode.INTERNAL_ERROR)));
        }
    }

    private static CallToolResult Result(ToolResponse response) => McpToolResponseAdapter.ToCallResult(response);

    [McpServerTool(Name = "unregister_signal", UseStructuredContent = true, OutputSchemaType = typeof(ToolResponse))]
    [Description("所有するシグナルの監視を解除し、未成立で待機中の要求へ解除理由を通知します。未知ID・期限切れ・他所有者を区別します。")]
    public async Task<CallToolResult> UnregisterSignal(string lockToken, string signalId)
    {
        if (!OperationRequestQueue.IsLockTokenFormatValid(lockToken) || !SignalRegistry.ValidId(signalId))
        { return Result(ToolResponse.Rejected(new ToolError(ErrorCode.INVALID_ARGUMENT))); }
        if (accessor.HttpContext is not { } http) { return Result(ToolResponse.Rejected(new ToolError(ErrorCode.INTERNAL_ERROR))); }
        try
        {
            var owner = LogicalSessions.GetOwner(http);
            var lockId = queue.RequireSignalLock(owner.Id, lockToken);
            var data = await registration.UnregisterAsync(owner.Id, lockId, signalId, http.RequestAborted);
            return Result(ToolResponse.Information(data));
        }
        catch (WindowReferenceException error) { return Result(ToolResponse.Rejected(new ToolError(error.Code))); }
        catch (OperationCanceledException) { return Result(ToolResponse.Rejected(new ToolError(ErrorCode.CANCELLED))); }
        catch (Exception error)
        {
            logger.WriteEvent(LogLevel.Error, LogEvent.UnhandledFailure, LogOutcome.Failed, error: AuditLog.Classify(error));
            return Result(ToolResponse.Rejected(new ToolError(ErrorCode.INTERNAL_ERROR)));
        }
    }

    [McpServerTool(Name = "wait_for_signal", UseStructuredContent = true, OutputSchemaType = typeof(ToolResponse))]
    [Description("シグナル待機をFIFOキューへ受付登録します。timeoutMsは0～300000で、通常の待機時間切れは成功結果timeoutです。結果はget_requestで取得します。")]
    public CallToolResult WaitForSignal(int schemaVersion, string requestId, string lockToken, string signalId, int timeoutMs)
    {
        if (schemaVersion != ToolResponse.CurrentSchemaVersion || !OperationRequestQueue.IsLockTokenFormatValid(lockToken)
            || !SignalRegistry.ValidId(signalId) || timeoutMs is < 0 or > 300000)
        { return Result(ToolResponse.Rejected(new ToolError(ErrorCode.INVALID_ARGUMENT))); }
        return executor.ExecuteOperation(requestId, ResponseJson.ToElement(new
        { name = "wait_for_signal", args = new { signalId, timeoutMs } }), lockToken);
    }
}
