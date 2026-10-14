using System.ComponentModel;
using Kugutsu.Contracts;
using Kugutsu.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Kugutsu.App;

/// <summary>Acquires exclusive access to the operation buffer for one logical client.</summary>
[McpServerToolType]
internal sealed class LockMcpTools
{
    private readonly OperationRequestQueue _queue;
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly KugutsuOptions _settings;
    private readonly ILogger<LockMcpTools> _logger;

    public LockMcpTools(OperationRequestQueue queue, IHttpContextAccessor httpContextAccessor,
        KugutsuOptions settings, ILogger<LockMcpTools> logger)
    {
        _queue = queue;
        _httpContextAccessor = httpContextAccessor;
        _settings = settings;
        _logger = logger;
    }

    [McpServerTool(Name = "lock_mcp", UseStructuredContent = true,
        OutputSchemaType = typeof(ToolResponse))]
    [Description("操作バッファを排他取得します。取得要求はFIFOで待機し、待機中は新しい操作要求を受け付けません。成功時にlockTokenとexpiresAtを返します。")]
    public async Task<CallToolResult> LockMcp(int? acquireTimeoutMs = null, int? leaseSeconds = null)
    {
        if (acquireTimeoutMs is < 0 or > 300000 || leaseSeconds is < 10 or > 3600)
        { return McpToolResponseAdapter.ToCallResult(ToolResponse.Rejected(new ToolError(ErrorCode.INVALID_ARGUMENT))); }
        if (_httpContextAccessor.HttpContext is not { } context)
        { return McpToolResponseAdapter.ToCallResult(ToolResponse.Rejected(new ToolError(ErrorCode.INTERNAL_ERROR))); }

        try
        {
            var owner = LogicalSessions.GetOwner(context);
            var timeout = TimeSpan.FromMilliseconds(acquireTimeoutMs ?? _settings.McpLockAcquireTimeoutSeconds * 1000);
            var lease = TimeSpan.FromSeconds(leaseSeconds ?? _settings.McpLockLeaseSeconds);
            var grant = await _queue.AcquireLockAsync(owner.Id, timeout, lease, context.RequestAborted)
                .ConfigureAwait(false);
            if (grant is null)
            { return McpToolResponseAdapter.ToCallResult(ToolResponse.Rejected(new ToolError(ErrorCode.MCP_LOCK_TIMEOUT))); }

            _logger.WriteEvent(LogLevel.Information, LogEvent.LockAcquired, LogOutcome.Succeeded);
            return McpToolResponseAdapter.ToCallResult(ToolResponse.Information(new
            { lockToken = grant.LockToken, expiresAt = grant.ExpiresAt }));
        }
        catch (OperationCanceledException)
        { return McpToolResponseAdapter.ToCallResult(ToolResponse.Rejected(new ToolError(ErrorCode.CANCELLED))); }
        catch (InvalidOperationException)
        { return McpToolResponseAdapter.ToCallResult(ToolResponse.Rejected(new ToolError(ErrorCode.INTERNAL_ERROR))); }
    }

    [McpServerTool(Name = "is_mcp_locked", ReadOnly = true, UseStructuredContent = true,
        OutputSchemaType = typeof(ToolResponse))]
    [Description("即時照会でMCPロック状態を返します。取得予約は行いません。ロックトークンは返しません。")]
    public CallToolResult IsMcpLocked()
    {
        if (_httpContextAccessor.HttpContext is not { } context)
        { return McpToolResponseAdapter.ToCallResult(ToolResponse.Rejected(new ToolError(ErrorCode.INTERNAL_ERROR))); }
        try
        {
            var owner = LogicalSessions.GetOwner(context);
            var status = _queue.GetLockStatus(owner.Id);
            return McpToolResponseAdapter.ToCallResult(ToolResponse.Information(new
            {
                locked = status.Locked,
                ownedByCaller = status.OwnedByCaller,
                expiresAt = status.ExpiresAt,
                remainingMs = status.RemainingMs
            }));
        }
        catch (InvalidOperationException)
        { return McpToolResponseAdapter.ToCallResult(ToolResponse.Rejected(new ToolError(ErrorCode.INTERNAL_ERROR))); }
    }

    [McpServerTool(Name = "renew_mcp_lock", UseStructuredContent = true,
        OutputSchemaType = typeof(ToolResponse))]
    [Description("呼出し元が所有するMCPロックの期限を更新します。lockTokenが必要です。省略したleaseSecondsには設定値を使います。")]
    public CallToolResult RenewMcpLock(string lockToken, int? leaseSeconds = null)
    {
        if (leaseSeconds is < 10 or > 3600 || string.IsNullOrEmpty(lockToken))
        { return McpToolResponseAdapter.ToCallResult(ToolResponse.Rejected(new ToolError(ErrorCode.INVALID_ARGUMENT))); }
        if (_httpContextAccessor.HttpContext is not { } context)
        { return McpToolResponseAdapter.ToCallResult(ToolResponse.Rejected(new ToolError(ErrorCode.INTERNAL_ERROR))); }
        try
        {
            var owner = LogicalSessions.GetOwner(context);
            var lease = TimeSpan.FromSeconds(leaseSeconds ?? _settings.McpLockLeaseSeconds);
            var result = _queue.RenewLock(owner.Id, lockToken, lease, out var expiresAt);
            if (result == QueueAdmission.Locked)
            { return McpToolResponseAdapter.ToCallResult(ToolResponse.Rejected(new ToolError(ErrorCode.MCP_LOCKED))); }
            if (result == QueueAdmission.LockTokenInvalid)
            { return McpToolResponseAdapter.ToCallResult(ToolResponse.Rejected(new ToolError(ErrorCode.MCP_LOCK_TOKEN_INVALID))); }
            if (result == QueueAdmission.LockExpired)
            { return McpToolResponseAdapter.ToCallResult(ToolResponse.Rejected(new ToolError(ErrorCode.MCP_LOCK_EXPIRED))); }

            if (result == QueueAdmission.Stopped)
            { return McpToolResponseAdapter.ToCallResult(ToolResponse.Rejected(new ToolError(ErrorCode.CANCELLED))); }
            _logger.WriteEvent(LogLevel.Information, LogEvent.LockRenewed, LogOutcome.Succeeded);
            return McpToolResponseAdapter.ToCallResult(ToolResponse.Information(new { expiresAt }));
        }
        catch (InvalidOperationException)
        { return McpToolResponseAdapter.ToCallResult(ToolResponse.Rejected(new ToolError(ErrorCode.INTERNAL_ERROR))); }
    }

    [McpServerTool(Name = "unlock_mcp", UseStructuredContent = true,
        OutputSchemaType = typeof(ToolResponse))]
    [Description("呼出し元が所有するMCPロックを解放します。未完了要求が残る場合はMCP_LOCK_BUSYです。同じ所有者とlockTokenでの再送は保持期間中、同じ成功応答を返します。")]
    public CallToolResult UnlockMcp(string lockToken)
    {
        if (string.IsNullOrEmpty(lockToken))
        { return McpToolResponseAdapter.ToCallResult(ToolResponse.Rejected(new ToolError(ErrorCode.INVALID_ARGUMENT))); }
        if (_httpContextAccessor.HttpContext is not { } context)
        { return McpToolResponseAdapter.ToCallResult(ToolResponse.Rejected(new ToolError(ErrorCode.INTERNAL_ERROR))); }
        try
        {
            var owner = LogicalSessions.GetOwner(context);
            var result = _queue.UnlockLock(owner.Id, lockToken, out var alreadyReleased);
            if (result == McpLockReleaseResult.Busy)
            { return McpToolResponseAdapter.ToCallResult(ToolResponse.Rejected(new ToolError(ErrorCode.MCP_LOCK_BUSY))); }
            if (result == McpLockReleaseResult.Locked)
            { return McpToolResponseAdapter.ToCallResult(ToolResponse.Rejected(new ToolError(ErrorCode.MCP_LOCKED))); }
            if (result == McpLockReleaseResult.TokenInvalid)
            { return McpToolResponseAdapter.ToCallResult(ToolResponse.Rejected(new ToolError(ErrorCode.MCP_LOCK_TOKEN_INVALID))); }
            if (result == McpLockReleaseResult.Expired)
            { return McpToolResponseAdapter.ToCallResult(ToolResponse.Rejected(new ToolError(ErrorCode.MCP_LOCK_EXPIRED))); }

            if (!alreadyReleased)
            { _logger.WriteEvent(LogLevel.Information, LogEvent.LockReleased, LogOutcome.Succeeded); }
            return McpToolResponseAdapter.ToCallResult(ToolResponse.Information(new { unlocked = true }));
        }
        catch (InvalidOperationException)
        { return McpToolResponseAdapter.ToCallResult(ToolResponse.Rejected(new ToolError(ErrorCode.INTERNAL_ERROR))); }
    }
}
