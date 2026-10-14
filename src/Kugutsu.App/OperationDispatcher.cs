using System.Collections.Frozen;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using Kugutsu.Contracts;

namespace Kugutsu.App;

/// <summary>One implemented operation, its argument rules and execution handler.</summary>
internal sealed record OperationDefinition(string Name, ArgumentSchema Arguments,
    Func<JsonElement, CancellationToken, ValueTask<JsonElement?>> Execute,
    Func<JsonElement, bool>? ValidateArguments = null,
    Func<JsonElement, OperationExecutionContext, CancellationToken, ValueTask<JsonElement?>>? ExecuteOwned = null);

internal sealed record OperationExecutionContext(Guid OwnerId, Guid? LockId, SignalBatchBindings? Signals = null);

/// <summary>Validates operations before queue acceptance and dispatches immutable argument snapshots.</summary>
internal sealed class OperationDispatcher
{
    private readonly FrozenDictionary<string, OperationDefinition> _operations;
    private readonly object _identity = new();
    private readonly BrowserOperationGate? _browserGate;

    public OperationDispatcher(IEnumerable<OperationDefinition> definitions, BrowserOperationGate? browserGate = null)
    {
        _browserGate = browserGate;
        ArgumentNullException.ThrowIfNull(definitions);
        var map = new Dictionary<string, OperationDefinition>(StringComparer.Ordinal);
        foreach (var definition in definitions)
        {
            ArgumentNullException.ThrowIfNull(definition);
            if (!ValidName(definition.Name) || definition.Arguments is null || !definition.Arguments.IsObject
                || definition.Execute is null
                || !map.TryAdd(definition.Name, definition))
            { throw new ArgumentException("Invalid operation registration.", nameof(definitions)); }
        }
        _operations = map.ToFrozenDictionary(StringComparer.Ordinal);
    }

    public bool TryPrepare(JsonElement operation, [NotNullWhen(true)] out PreparedOperation? prepared,
        [NotNullWhen(false)] out ToolError? error)
    {
        prepared = null;
        error = new ToolError(ErrorCode.INVALID_ARGUMENT);
        if (operation.ValueKind != JsonValueKind.Object) { return false; }
        string? name = null;
        JsonElement arguments = default;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in operation.EnumerateObject())
        {
            if (!seen.Add(property.Name)) { return false; }
            switch (property.Name)
            {
                case "name" when property.Value.ValueKind == JsonValueKind.String:
                    name = property.Value.GetString();
                    break;
                case "args" when property.Value.ValueKind == JsonValueKind.Object:
                    arguments = property.Value;
                    break;
                default: return false;
            }
        }
        if (!ValidName(name) || arguments.ValueKind != JsonValueKind.Object
            || !_operations.TryGetValue(name!, out var definition)
            || !definition.Arguments.Accepts(arguments)
            || definition.ValidateArguments?.Invoke(arguments) == false) { return false; }
        prepared = new PreparedOperation(_identity, definition, arguments);
        error = null;
        return true;
    }

    public async ValueTask<JsonElement?> ExecuteAsync(PreparedOperation operation, CancellationToken cancellationToken,
        OperationExecutionContext? context = null)
    {
        ArgumentNullException.ThrowIfNull(operation);
        if (!ReferenceEquals(operation.RegistryIdentity, _identity))
        { throw new ArgumentException("Operation belongs to a different registry.", nameof(operation)); }
        cancellationToken.ThrowIfCancellationRequested();
        using var browserLease = _browserGate is not null
            && (operation.Name.StartsWith("browser.", StringComparison.Ordinal) || operation.Name.StartsWith("dom.", StringComparison.Ordinal))
            ? await _browserGate.EnterAsync(cancellationToken) : null;
        if (operation.Definition.ExecuteOwned is not null && context is null)
        { throw new WindowReferenceException(ErrorCode.MCP_LOCK_TOKEN_INVALID); }
        var result = operation.Definition.ExecuteOwned is { } owned
            ? await owned(operation.Arguments, context!, cancellationToken)
            : await operation.Definition.Execute(operation.Arguments, cancellationToken);
        if (result?.ValueKind == JsonValueKind.Undefined)
        { throw new InvalidOperationException("Operation returned undefined JSON data."); }
        return result?.Clone();
    }

    private static bool ValidName(string? name) => name is { Length: > 0 and <= 128 }
        && name.All(character => character is >= 'a' and <= 'z' or >= '0' and <= '9' or '_' or '.')
        && name[0] is >= 'a' and <= 'z';
}

/// <summary>Arguments independent of the caller's JsonDocument, tied to their validated registry.</summary>
internal sealed class PreparedOperation
{
    internal PreparedOperation(object registryIdentity, OperationDefinition definition, JsonElement arguments)
    {
        RegistryIdentity = registryIdentity;
        Definition = definition;
        Arguments = arguments.Clone();
    }
    internal object RegistryIdentity { get; }
    internal OperationDefinition Definition { get; }
    public string Name => Definition.Name;
    public JsonElement Arguments { get; }
    public override string ToString() => Name;
}
