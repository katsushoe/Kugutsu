namespace Kugutsu.Hosting;

/// <summary>Names for Kugutsu's logical session protocol, separate from MCP HTTP sessions.</summary>
internal static class SessionProtocol
{
    public const string IdHeader = "X-Kugutsu-Session-Id";
    public const string CredentialHeader = "X-Kugutsu-Session-Credential";
    public const string ResumeHeader = "X-Kugutsu-Session-Resume";
    public const int CredentialLength = 43;

    /// <summary>Derives the private issuance route from the configured MCP URL.</summary>
    public static Uri IssuanceEndpoint(Uri endpoint) =>
        new UriBuilder(endpoint) { Path = endpoint.AbsolutePath.TrimEnd('/') + "/sessions" }.Uri;

    /// <summary>Derives the private resumption route without using a caller-provided identity path.</summary>
    public static Uri ResumeEndpoint(Uri endpoint) =>
        new UriBuilder(endpoint) { Path = IssuanceEndpoint(endpoint).AbsolutePath + "/resume" }.Uri;

    /// <summary>Checks the fixed ASCII Base64url credential wire format.</summary>
    public static bool IsCredential(string? value) => value is { Length: CredentialLength }
        && value.All(character => character is >= 'a' and <= 'z' or >= 'A' and <= 'Z'
            or >= '0' and <= '9' or '-' or '_');
}
