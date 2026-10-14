using System.ComponentModel;
using System.Buffers;
using System.Security.Cryptography;
using System.Text.Json;
using Kugutsu.Contracts;
using Kugutsu.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Kugutsu.App;

/// <summary>MCP endpoint for validating and accepting one operation request.</summary>
[McpServerToolType]
internal sealed class ExecuteOperationTools
{
    private readonly OperationDispatcher _dispatcher;
    private readonly OperationRequestQueue _queue;
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly ILogger<ExecuteOperationTools> _logger;
    private readonly KugutsuOptions _settings;
    private readonly AloeBatchRunner _aloe;
    private readonly AloeDependencies _aloeDependencies;

    public ExecuteOperationTools(OperationDispatcher dispatcher, OperationRequestQueue queue,
        IHttpContextAccessor httpContextAccessor, ILogger<ExecuteOperationTools> logger,
        KugutsuOptions settings, AloeBatchRunner aloe, AloeDependencies aloeDependencies)
    {
        _dispatcher = dispatcher;
        _queue = queue;
        _httpContextAccessor = httpContextAccessor;
        _logger = logger;
        _settings = settings;
        _aloe = aloe;
        _aloeDependencies = aloeDependencies;
    }

    /// <summary>Validates one operation and reports queued acceptance.</summary>
    [McpServerTool(Name = "execute_operation", UseStructuredContent = true,
        OutputSchemaType = typeof(ToolResponse))]
    [Description("要求IDと単一の操作{name,args}を検証し、FIFOキューへ受付登録します。MCPロック中は所有セッションがlockTokenを指定してください。返却状態queuedは操作完了を意味しません。完了確認にはget_requestを使用します。")]
    public CallToolResult ExecuteOperation(string requestId, JsonElement operation, string? lockToken = null)
    {
        var response = ValidateAndEnqueue(requestId, operation, lockToken);
        return McpToolResponseAdapter.ToCallResult(response);
    }

    /// <summary>Validates and accepts a JSON batch as one request.</summary>
    [McpServerTool(Name = "submit_batch", UseStructuredContent = true,
        OutputSchemaType = typeof(ToolResponse))]
    [Description("format=jsonではoperations配列、format=aloeではsource文字列をFIFOへ1要求として登録します。operationsとsourceの同時指定は拒否します。MCPロック中とシグナル操作には所有者のlockTokenを指定します。queuedは完了を意味せず、結果はget_requestで取得します。Aloeは固定コンパイラーと制限付き補助プロセスで実行します。")]
    public CallToolResult SubmitBatch(int schemaVersion, string requestId, string format, JsonElement? operations = null,
        string? lockToken = null, string? source = null)
    {
        var response = ValidateAndEnqueueBatch(schemaVersion, requestId, format, operations, lockToken, source);
        return McpToolResponseAdapter.ToCallResult(response);
    }

    private ToolResponse ValidateAndEnqueueBatch(int schemaVersion, string requestIdText,
        string format, JsonElement? operationsInput, string? lockToken, string? source)
    {
        if (schemaVersion != ToolResponse.CurrentSchemaVersion
            || !Guid.TryParseExact(requestIdText, "D", out var requestId) || requestId == Guid.Empty)
        { return ToolResponse.Rejected(new ToolError(ErrorCode.INVALID_ARGUMENT)); }
        if (format == "aloe") { return ValidateAndEnqueueAloe(requestId, operationsInput, source, lockToken); }
        if (source is not null || operationsInput is null)
        { return ToolResponse.Rejected(new ToolError(ErrorCode.INVALID_ARGUMENT), requestId); }
        var operations = operationsInput.Value;
        if (format != "json" || operations.ValueKind != JsonValueKind.Array
            || operations.GetArrayLength() is 0
            || operations.GetArrayLength() > _settings.MaxBatchOperations)
        { return ToolResponse.Rejected(new ToolError(ErrorCode.INVALID_ARGUMENT), requestId); }
        var context = _httpContextAccessor.HttpContext;
        if (context is null) { return ToolResponse.Rejected(new ToolError(ErrorCode.INTERNAL_ERROR), requestId); }
        LogicalOwner owner;
        try { owner = LogicalSessions.GetOwner(context); }
        catch (InvalidOperationException)
        { return ToolResponse.Rejected(new ToolError(ErrorCode.INTERNAL_ERROR), requestId); }

        var fingerprint = RequestFingerprint.Compute(operations);
        var existing = _queue.CheckExisting(requestId, owner.Id, fingerprint, out var prior);
        if (existing == QueueAdmission.Duplicate && prior is not null) { return prior.GetResponse(); }
        if (existing == QueueAdmission.Conflict)
        { return ToolResponse.Rejected(new ToolError(ErrorCode.REQUEST_ID_CONFLICT), requestId); }

        var prepared = new List<PreparedOperation>(operations.GetArrayLength());
        foreach (var operation in operations.EnumerateArray())
        {
            if (!_dispatcher.TryPrepare(operation, out var command, out var error))
            { return ToolResponse.Rejected(error, requestId); }
            prepared.Add(command!);
        }

        if (!SignalBatchBindings.Validate(prepared, true))
        { return ToolResponse.Rejected(new ToolError(ErrorCode.INVALID_ARGUMENT), requestId); }
        OperationExecutionContext executionContext;
        try
        {
            var hasSignals = prepared.Any(command => command.Name is "register_signal" or "wait_for_signal" or "unregister_signal");
            if (hasSignals && lockToken is null) { return ToolResponse.Rejected(new ToolError(ErrorCode.MCP_LOCK_TOKEN_INVALID), requestId); }
            executionContext = new(owner.Id, hasSignals ? _queue.RequireSignalLock(owner.Id, lockToken!) : null,
                hasSignals ? new SignalBatchBindings() : null);
        }
        catch (WindowReferenceException error) { return ToolResponse.Rejected(new ToolError(error.Code), requestId); }
        var queuedOperations = prepared.Select(command => new QueuedOperation(command.Name,
            (queuedOwnerId, cancellationToken) => queuedOwnerId == owner.Id
                ? _dispatcher.ExecuteAsync(command, cancellationToken, executionContext)
                : ValueTask.FromException<JsonElement?>(new InvalidOperationException("Queued owner changed."))))
            .ToArray();
        var admission = _queue.TryEnqueueBatch(requestId, owner.Id, fingerprint, queuedOperations, out _, lockToken);
        if (admission == QueueAdmission.LockPending)
        { return ToolResponse.Rejected(new ToolError(ErrorCode.MCP_LOCK_PENDING), requestId); }
        if (admission == QueueAdmission.Locked)
        { return ToolResponse.Rejected(new ToolError(ErrorCode.MCP_LOCKED), requestId); }
        if (admission == QueueAdmission.LockTokenInvalid)
        { return ToolResponse.Rejected(new ToolError(ErrorCode.MCP_LOCK_TOKEN_INVALID), requestId); }
        if (admission == QueueAdmission.LockExpired)
        { return ToolResponse.Rejected(new ToolError(ErrorCode.MCP_LOCK_EXPIRED), requestId); }
        if (admission == QueueAdmission.Full)
        { return ToolResponse.Rejected(new ToolError(ErrorCode.QUEUE_FULL), requestId); }
        if (admission == QueueAdmission.Stopped)
        { return ToolResponse.Rejected(new ToolError(ErrorCode.CANCELLED), requestId); }
        if (admission == QueueAdmission.Duplicate && _queue.TryGet(requestId, owner.Id, out var duplicate))
        { return duplicate!; }
        if (admission == QueueAdmission.Conflict)
        { return ToolResponse.Rejected(new ToolError(ErrorCode.REQUEST_ID_CONFLICT), requestId); }

        _logger.WriteEvent(LogLevel.Information, LogEvent.RequestAccepted, LogOutcome.Succeeded, requestId: requestId);
        return ToolResponse.Accepted(requestId);
    }

    private ToolResponse ValidateAndEnqueueAloe(Guid requestId, JsonElement? operations, string? source, string? lockToken)
    {
        if (operations is not null || source is null || !KeyboardOperations.ValidText(source))
        { return ToolResponse.Rejected(new ToolError(ErrorCode.INVALID_ARGUMENT), requestId); }
        if (_httpContextAccessor.HttpContext is not { } http)
        { return ToolResponse.Rejected(new ToolError(ErrorCode.INTERNAL_ERROR), requestId); }
        try
        {
            var owner = LogicalSessions.GetOwner(http);
            var fingerprint = RequestFingerprint.Compute(JsonSerializer.SerializeToElement(new { format = "aloe", source }));
            var existing = _queue.CheckExisting(requestId, owner.Id, fingerprint, out var prior);
            if (existing == QueueAdmission.Duplicate && prior is not null) { return prior.GetResponse(); }
            if (existing == QueueAdmission.Conflict) { return ToolResponse.Rejected(new ToolError(ErrorCode.REQUEST_ID_CONFLICT), requestId); }
            var context = new OperationExecutionContext(owner.Id, lockToken is null ? null : _queue.RequireSignalLock(owner.Id, lockToken));
            using var assets = _aloeDependencies.OpenVerified(http.RequestAborted);
            var command = _aloe.Prepare(source, context);
            var admission = _queue.TryEnqueueBatch(requestId, owner.Id, fingerprint, [command], out _, lockToken);
            if (admission == QueueAdmission.Duplicate && _queue.TryGet(requestId, owner.Id, out var duplicate)) { return duplicate!; }
            if (admission != QueueAdmission.Accepted)
            {
                var code = admission switch
                {
                    QueueAdmission.LockPending => ErrorCode.MCP_LOCK_PENDING, QueueAdmission.Locked => ErrorCode.MCP_LOCKED,
                    QueueAdmission.LockTokenInvalid => ErrorCode.MCP_LOCK_TOKEN_INVALID, QueueAdmission.LockExpired => ErrorCode.MCP_LOCK_EXPIRED,
                    QueueAdmission.Full => ErrorCode.QUEUE_FULL, QueueAdmission.Stopped => ErrorCode.CANCELLED,
                    QueueAdmission.Conflict => ErrorCode.REQUEST_ID_CONFLICT, _ => ErrorCode.INTERNAL_ERROR
                };
                return ToolResponse.Rejected(new ToolError(code), requestId);
            }
            _logger.WriteEvent(LogLevel.Information, LogEvent.RequestAccepted, LogOutcome.Succeeded, requestId: requestId);
            return ToolResponse.Accepted(requestId);
        }
        catch (AloeDependencyException) { return ToolResponse.Rejected(new ToolError(ErrorCode.ALOE_UNAVAILABLE), requestId); }
        catch (WindowReferenceException failure) { return ToolResponse.Rejected(new ToolError(failure.Code), requestId); }
        catch (OperationCanceledException) { return ToolResponse.Rejected(new ToolError(ErrorCode.CANCELLED), requestId); }
        catch (Exception failure)
        {
            _logger.WriteEvent(LogLevel.Error, LogEvent.UnhandledFailure, LogOutcome.Failed, error: AuditLog.Classify(failure));
            return ToolResponse.Rejected(new ToolError(ErrorCode.INTERNAL_ERROR), requestId);
        }
    }

    private ToolResponse ValidateAndEnqueue(string requestIdText, JsonElement operation, string? lockToken)
    {
        if (!Guid.TryParseExact(requestIdText, "D", out var requestId) || requestId == Guid.Empty)
        { return ToolResponse.Rejected(new ToolError(ErrorCode.INVALID_ARGUMENT)); }
        if (operation.ValueKind == JsonValueKind.Undefined)
        { return ToolResponse.Rejected(new ToolError(ErrorCode.INVALID_ARGUMENT), requestId); }
        var context = _httpContextAccessor.HttpContext;
        if (context is null) { return ToolResponse.Rejected(new ToolError(ErrorCode.INTERNAL_ERROR), requestId); }
        LogicalOwner owner;
        try { owner = LogicalSessions.GetOwner(context); }
        catch (InvalidOperationException)
        { return ToolResponse.Rejected(new ToolError(ErrorCode.INTERNAL_ERROR), requestId); }

        var fingerprint = RequestFingerprint.Compute(operation);
        var existing = _queue.CheckExisting(requestId, owner.Id, fingerprint, out var prior);
        if (existing == QueueAdmission.Duplicate && prior is not null) { return prior.GetResponse(); }
        if (existing == QueueAdmission.Conflict)
        { return ToolResponse.Rejected(new ToolError(ErrorCode.REQUEST_ID_CONFLICT), requestId); }
        if (!_dispatcher.TryPrepare(operation, out var prepared, out var error))
        { return ToolResponse.Rejected(error, requestId); }
        if (!SignalBatchBindings.Validate([prepared], false))
        { return ToolResponse.Rejected(new ToolError(ErrorCode.INVALID_ARGUMENT), requestId); }
        OperationExecutionContext executionContext;
        try
        {
            var hasSignals = prepared.Name is "register_signal" or "wait_for_signal" or "unregister_signal";
            if (hasSignals && lockToken is null) { return ToolResponse.Rejected(new ToolError(ErrorCode.MCP_LOCK_TOKEN_INVALID), requestId); }
            executionContext = new(owner.Id, hasSignals ? _queue.RequireSignalLock(owner.Id, lockToken!) : null);
        }
        catch (WindowReferenceException failure) { return ToolResponse.Rejected(new ToolError(failure.Code), requestId); }

        var admission = _queue.TryEnqueue(requestId, owner.Id, prepared.Name,
            fingerprint,
            (queuedOwnerId, cancellationToken) => queuedOwnerId == owner.Id
                ? _dispatcher.ExecuteAsync(prepared, cancellationToken, executionContext)
                : ValueTask.FromException<System.Text.Json.JsonElement?>(new InvalidOperationException("Queued owner changed.")),
            out _, lockToken);
        if (admission == QueueAdmission.LockPending)
        { return ToolResponse.Rejected(new ToolError(ErrorCode.MCP_LOCK_PENDING), requestId); }
        if (admission == QueueAdmission.Locked)
        { return ToolResponse.Rejected(new ToolError(ErrorCode.MCP_LOCKED), requestId); }
        if (admission == QueueAdmission.LockTokenInvalid)
        { return ToolResponse.Rejected(new ToolError(ErrorCode.MCP_LOCK_TOKEN_INVALID), requestId); }
        if (admission == QueueAdmission.LockExpired)
        { return ToolResponse.Rejected(new ToolError(ErrorCode.MCP_LOCK_EXPIRED), requestId); }
        if (admission == QueueAdmission.Full)
        { return ToolResponse.Rejected(new ToolError(ErrorCode.QUEUE_FULL), requestId); }
        if (admission == QueueAdmission.Stopped)
        { return ToolResponse.Rejected(new ToolError(ErrorCode.CANCELLED), requestId); }
        if (admission == QueueAdmission.Duplicate && _queue.TryGet(requestId, owner.Id, out var duplicate))
        { return duplicate!; }
        if (admission == QueueAdmission.Conflict)
        { return ToolResponse.Rejected(new ToolError(ErrorCode.REQUEST_ID_CONFLICT), requestId); }

        _logger.WriteEvent(LogLevel.Information, LogEvent.RequestAccepted, LogOutcome.Succeeded, requestId: requestId);
        return ToolResponse.Accepted(requestId);
    }
}

/// <summary>Hashes operation JSON with object property order normalized.</summary>
internal static class RequestFingerprint
{
    internal static string Compute(JsonElement value)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer)) { WriteCanonical(value, writer); }
        return Convert.ToHexString(SHA256.HashData(buffer.WrittenSpan));
    }

    private static void WriteCanonical(JsonElement value, Utf8JsonWriter writer)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in value.EnumerateObject().OrderBy(item => item.Name, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(property.Name);
                    WriteCanonical(property.Value, writer);
                }
                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in value.EnumerateArray()) { WriteCanonical(item, writer); }
                writer.WriteEndArray();
                break;
            default:
                value.WriteTo(writer);
                break;
        }
    }
}
