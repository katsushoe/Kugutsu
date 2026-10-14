using System.Text.Json;
using Kugutsu.Contracts;
using Kugutsu.Hosting;

namespace Kugutsu.App;

/// <summary>Each private host call uses the existing operation schema and its parent-owned identity.</summary>
internal sealed class AloeOperationBridge(OperationDispatcher dispatcher, KugutsuOptions settings, OperationRequestQueue queue)
{
    internal async ValueTask<JsonElement?> ExecuteAsync(string name, string arguments, OperationExecutionContext context,
        CancellationToken token)
    {
        if (context.OwnerId == Guid.Empty || arguments.Length > 65536) { throw new WindowReferenceException(ErrorCode.INVALID_ARGUMENT); }
        token.ThrowIfCancellationRequested();
        if (context.LockId is { } lockId) { queue.VerifySignalLock(context.OwnerId, lockId); }
        PreparedOperation? operation;
        var signalHelper = name switch
        {
            "$aloe.register_signal" => "register_signal",
            "$aloe.wait_for_signal" => "wait_for_signal",
            "$aloe.unregister_signal" => "unregister_signal",
            _ => null
        };
        try
        {
            using var document = JsonDocument.Parse(arguments, new() { MaxDepth = 32 });
            var operationName = name;
            JsonElement operationArguments = document.RootElement;
            if (signalHelper is not null)
            {
                operationName = signalHelper;
                operationArguments = signalHelper switch
                {
                    "register_signal" => RegisterSignalArguments(document.RootElement),
                    "wait_for_signal" => WaitForSignalArguments(document.RootElement),
                    _ => UnregisterSignalArguments(document.RootElement)
                };
            }
            var command = JsonSerializer.SerializeToElement(new { name = operationName, args = operationArguments });
            if (!dispatcher.TryPrepare(command, out operation, out _) || !SignalBatchBindings.Validate([operation], false))
            { throw new WindowReferenceException(ErrorCode.INVALID_ARGUMENT); }
        }
        catch (JsonException) { throw new WindowReferenceException(ErrorCode.INVALID_ARGUMENT); }
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(settings.OperationTimeoutSeconds));
        try
        {
            var result = await dispatcher.ExecuteAsync(operation, deadline.Token, context);
            deadline.Token.ThrowIfCancellationRequested();
            if (context.LockId is { } currentLock) { queue.VerifySignalLock(context.OwnerId, currentLock); }
            if (signalHelper is null || result is not { } signalResult) { return result; }
            return JsonSerializer.SerializeToElement(signalHelper switch
            {
                "register_signal" => signalResult.GetProperty("signalId").GetString(),
                "wait_for_signal" => signalResult.GetProperty("status").GetString(),
                _ => "unregistered"
            });
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        { throw new WindowReferenceException(ErrorCode.TIMEOUT); }
    }

    private static JsonElement RegisterSignalArguments(JsonElement helperArguments)
    {
        var conditionText = helperArguments.GetProperty("conditionJson").GetString();
        var retentionText = helperArguments.GetProperty("retentionSeconds").GetString();
        if (conditionText is null || !int.TryParse(retentionText, System.Globalization.NumberStyles.None,
            System.Globalization.CultureInfo.InvariantCulture, out var retention) || retention is < 1 or > 3600)
        { throw new WindowReferenceException(ErrorCode.INVALID_ARGUMENT); }
        try
        {
            using var condition = JsonDocument.Parse(conditionText, new() { MaxDepth = 32 });
            if (condition.RootElement.ValueKind != JsonValueKind.Object) { throw new WindowReferenceException(ErrorCode.INVALID_ARGUMENT); }
            return JsonSerializer.SerializeToElement(new { condition = condition.RootElement, retentionSeconds = retention });
        }
        catch (JsonException) { throw new WindowReferenceException(ErrorCode.INVALID_ARGUMENT); }
    }

    private static JsonElement WaitForSignalArguments(JsonElement helperArguments)
    {
        var id = helperArguments.GetProperty("signalId").GetString();
        var timeoutText = helperArguments.GetProperty("timeoutMs").GetString();
        if (!SignalRegistry.ValidId(id) || !int.TryParse(timeoutText, System.Globalization.NumberStyles.None,
            System.Globalization.CultureInfo.InvariantCulture, out var timeout) || timeout is < 0 or > 300000)
        { throw new WindowReferenceException(ErrorCode.INVALID_ARGUMENT); }
        return JsonSerializer.SerializeToElement(new { signalId = id, timeoutMs = timeout });
    }

    private static JsonElement UnregisterSignalArguments(JsonElement helperArguments)
    {
        var id = helperArguments.GetProperty("signalId").GetString();
        if (!SignalRegistry.ValidId(id)) { throw new WindowReferenceException(ErrorCode.INVALID_ARGUMENT); }
        return JsonSerializer.SerializeToElement(new { signalId = id });
    }
}
