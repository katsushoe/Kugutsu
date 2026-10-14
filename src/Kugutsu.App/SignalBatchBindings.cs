using System.Text.Json;
using Kugutsu.Contracts;

namespace Kugutsu.App;

/// <summary>Bindings belong to one queued request; the FIFO worker alone writes and reads them.</summary>
internal sealed class SignalBatchBindings
{
    private readonly Dictionary<string, string> _ids = new(StringComparer.Ordinal);
    internal static bool ValidKey(string? key) => key is { Length: > 0 and <= 64 }
        && key.All(character => char.IsAsciiLetterOrDigit(character) || character == '_');

    internal static bool ValidReference(JsonElement args) =>
        args.TryGetProperty("signalId", out var id) != args.TryGetProperty("signalKey", out var key)
        && (id.ValueKind != JsonValueKind.Undefined ? SignalRegistry.ValidId(id.GetString()) : ValidKey(key.GetString()));

    internal static string Resolve(JsonElement args, OperationExecutionContext context)
    {
        if (args.TryGetProperty("signalId", out var id)) { return id.GetString()!; }
        var key = args.GetProperty("signalKey").GetString()!;
        return context.Signals is { } bindings && bindings._ids.TryGetValue(key, out var registered)
            ? registered : throw new WindowReferenceException(ErrorCode.INVALID_ARGUMENT);
    }

    internal void Bind(string key, string id)
    {
        if (!ValidKey(key) || !SignalRegistry.ValidId(id) || !_ids.TryAdd(key, id))
        { throw new WindowReferenceException(ErrorCode.INVALID_ARGUMENT); }
    }

    internal static bool Validate(IReadOnlyList<PreparedOperation> commands, bool batch)
    {
        var declared = new HashSet<string>(StringComparer.Ordinal);
        foreach (var command in commands)
        {
            if (!command.Arguments.TryGetProperty("signalKey", out var key)) { continue; }
            var text = key.GetString();
            if (!batch || !ValidKey(text)) { return false; }
            if (command.Name == "register_signal")
            { if (!declared.Add(text!)) { return false; } }
            else if (command.Name is not ("wait_for_signal" or "unregister_signal") || !declared.Contains(text!))
            { return false; }
        }
        return true;
    }
}
