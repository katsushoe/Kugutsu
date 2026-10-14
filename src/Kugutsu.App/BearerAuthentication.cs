using System.Security.Cryptography;
using Microsoft.AspNetCore.Http;
using Kugutsu.Hosting;

namespace Kugutsu.App;

/// <summary>Authenticates each HTTP request using the startup credential.</summary>
internal static class BearerAuthentication
{
    // Credential Manager permits 2560 bytes of UTF-16LE: at most 1280 ASCII characters.
    private const int MaxTokenLength = 1280;

    /// <summary>Accepts a single Bearer header; never reads query or body credentials.</summary>
    public static bool IsAuthorized(HttpRequest request, BearerCredential credential)
    {
        var headers = request.Headers.Authorization;
        if (headers.Count != 1 || headers[0] is not { } header) { return false; }
        var value = header.AsSpan();
        const string scheme = "Bearer";
        if (value.Length <= scheme.Length
            || !value[..scheme.Length].Equals(scheme, StringComparison.OrdinalIgnoreCase)
            || value[scheme.Length] != ' ') { return false; }

        var start = scheme.Length;
        while (start < value.Length && value[start] == ' ') { start++; }
        var token = value[start..];
        if (token.Length is 0 or > MaxTokenLength) { return false; }

        Span<byte> bytes = stackalloc byte[MaxTokenLength];
        try
        {
            for (var index = 0; index < token.Length; index++)
            {
                // Reject Unicode rather than replacing it during ASCII conversion.
                if (token[index] > 127) { return false; }
                bytes[index] = (byte)token[index];
            }
            // Exact comparison also rejects whitespace, combined headers and invalid token grammar.
            return credential.Matches(bytes[..token.Length]);
        }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }
}
