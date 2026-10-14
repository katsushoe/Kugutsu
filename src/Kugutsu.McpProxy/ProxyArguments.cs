namespace Kugutsu.McpProxy;

/// <summary>Parses only the documented proxy endpoint override.</summary>
internal static class ProxyArguments
{
    /// <summary>Returns null when absent; duplicate, unknown or incomplete arguments fail closed.</summary>
    public static string? Parse(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);
        if (args.Length == 0) { return null; }
        string value;
        if (args.Length == 2 && string.Equals(args[0], "--url", StringComparison.Ordinal))
        { value = args[1]; }
        else if (args.Length == 1 && args[0] is { } argument
            && argument.StartsWith("--url=", StringComparison.Ordinal))
        { value = argument["--url=".Length..]; }
        else { throw Invalid(); }

        if (string.IsNullOrEmpty(value) || value.Any(char.IsWhiteSpace) || value.Contains('\0'))
        { throw Invalid(); }
        // The shared validator checks the effective URL after binding all other settings.
        return value;
    }

    private static InvalidDataException Invalid() => new("Invalid proxy command line arguments.");
}
