using System.Collections.Frozen;
using System.Text.Json;

namespace Kugutsu.App;

/// <summary>Immutable argument rules shared by registration and request validation.</summary>
internal abstract class ArgumentSchema
{
    internal bool IsObject => this is ObjectSchema;
    public abstract bool Accepts(JsonElement value);

    public static ArgumentSchema Object(IEnumerable<ArgumentProperty> properties) => new ObjectSchema(properties);
    public static ArgumentSchema String(int maximumLength, int minimumLength = 0,
        IEnumerable<string>? choices = null) => new StringSchema(minimumLength, maximumLength, choices);
    public static ArgumentSchema Integer(long minimum, long maximum) => new IntegerSchema(minimum, maximum);
    public static ArgumentSchema Number(double minimum, double maximum) => new NumberSchema(minimum, maximum);
    public static ArgumentSchema Boolean { get; } = new BooleanSchema();
    public static ArgumentSchema Array(ArgumentSchema item, int maximumCount, int minimumCount = 0)
        => new ArraySchema(item, minimumCount, maximumCount);

    private sealed class ObjectSchema : ArgumentSchema
    {
        private readonly FrozenDictionary<string, ArgumentProperty> _properties;

        public ObjectSchema(IEnumerable<ArgumentProperty> properties)
        {
            ArgumentNullException.ThrowIfNull(properties);
            var map = new Dictionary<string, ArgumentProperty>(StringComparer.Ordinal);
            foreach (var property in properties)
            {
                ArgumentNullException.ThrowIfNull(property);
                if (string.IsNullOrWhiteSpace(property.Name) || property.Schema is null
                    || !map.TryAdd(property.Name, property))
                { throw new ArgumentException("Invalid argument property registration.", nameof(properties)); }
            }
            _properties = map.ToFrozenDictionary(StringComparer.Ordinal);
        }

        public override bool Accepts(JsonElement value)
        {
            if (value.ValueKind != JsonValueKind.Object) { return false; }
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject())
            {
                if (!seen.Add(property.Name) || !_properties.TryGetValue(property.Name, out var definition)
                    || !definition.Schema.Accepts(property.Value)) { return false; }
            }
            return _properties.Values.All(property => !property.Required || seen.Contains(property.Name));
        }
    }

    private sealed class StringSchema : ArgumentSchema
    {
        private readonly int _minimum;
        private readonly int _maximum;
        private readonly FrozenSet<string>? _choices;

        public StringSchema(int minimum, int maximum, IEnumerable<string>? choices)
        {
            ValidateCount(minimum, maximum);
            _minimum = minimum;
            _maximum = maximum;
            _choices = choices?.ToFrozenSet(StringComparer.Ordinal);
            if (_choices is { Count: 0 } || _choices?.Any(choice => choice is null
                || choice.Length < minimum || choice.Length > maximum) == true)
            { throw new ArgumentException("Invalid string choices.", nameof(choices)); }
        }

        public override bool Accepts(JsonElement value)
        {
            if (value.ValueKind != JsonValueKind.String) { return false; }
            var text = value.GetString()!;
            return text.Length >= _minimum && text.Length <= _maximum
                && (_choices is null || _choices.Contains(text));
        }
    }

    private sealed class IntegerSchema : ArgumentSchema
    {
        private readonly long _minimum;
        private readonly long _maximum;
        public IntegerSchema(long minimum, long maximum)
        {
            if (minimum > maximum) { throw new ArgumentException("Invalid integer range."); }
            _minimum = minimum;
            _maximum = maximum;
        }
        public override bool Accepts(JsonElement value) => value.ValueKind == JsonValueKind.Number
            && value.TryGetInt64(out var number) && number >= _minimum && number <= _maximum;
    }

    private sealed class NumberSchema : ArgumentSchema
    {
        private readonly double _minimum;
        private readonly double _maximum;
        public NumberSchema(double minimum, double maximum)
        {
            if (!double.IsFinite(minimum) || !double.IsFinite(maximum) || minimum > maximum)
            { throw new ArgumentException("Invalid number range."); }
            _minimum = minimum;
            _maximum = maximum;
        }
        public override bool Accepts(JsonElement value) => value.ValueKind == JsonValueKind.Number
            && value.TryGetDouble(out var number) && double.IsFinite(number)
            && number >= _minimum && number <= _maximum;
    }

    private sealed class BooleanSchema : ArgumentSchema
    {
        public override bool Accepts(JsonElement value) => value.ValueKind is JsonValueKind.True or JsonValueKind.False;
    }

    private sealed class ArraySchema : ArgumentSchema
    {
        private readonly ArgumentSchema _item;
        private readonly int _minimum;
        private readonly int _maximum;
        public ArraySchema(ArgumentSchema item, int minimum, int maximum)
        {
            ArgumentNullException.ThrowIfNull(item);
            ValidateCount(minimum, maximum);
            _item = item;
            _minimum = minimum;
            _maximum = maximum;
        }
        public override bool Accepts(JsonElement value) => value.ValueKind == JsonValueKind.Array
            && value.GetArrayLength() >= _minimum && value.GetArrayLength() <= _maximum
            && value.EnumerateArray().All(_item.Accepts);
    }

    private static void ValidateCount(int minimum, int maximum)
    {
        if (minimum < 0 || maximum < minimum) { throw new ArgumentException("Invalid count range."); }
    }
}

/// <summary>A named argument, its immutable rules and required status.</summary>
internal sealed record ArgumentProperty(string Name, ArgumentSchema Schema, bool Required = true);
