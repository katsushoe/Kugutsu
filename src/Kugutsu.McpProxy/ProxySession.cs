using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Kugutsu.Hosting;

namespace Kugutsu.McpProxy;

/// <summary>Creates exactly one logical owner and holds its credentials only in memory.</summary>
internal sealed class ProxySession(HttpClient client, Uri endpoint, int timeoutSeconds,
    CancellationToken lifetime) : IDisposable
{
    private readonly object _gate = new();
    private readonly Dictionary<string, string> _headers = [];
    private Task? _initialization;
    private byte[]? _resumeCredential;

    /// <summary>Shared SDK headers populated before the first MCP transmission, then left unchanged.</summary>
    public IDictionary<string, string> TransportHeaders => _headers;

    /// <summary>Coalesces initial callers. A failed issuance is never automatically repeated.</summary>
    public Task EnsureAsync(CancellationToken token)
    {
        lock (_gate) { return (_initialization ??= InitializeAsync()).WaitAsync(token); }
    }

    /// <summary>Allows shutdown to observe and finish the private initialization task.</summary>
    public Task Completion { get { lock (_gate) { return _initialization ?? Task.CompletedTask; } } }

    /// <summary>Confirms the existing logical owner after reachability recovery, without reissuing it.</summary>
    public async Task ResumeAsync(CancellationToken token)
    {
        if (_resumeCredential is null || !_headers.TryGetValue(SessionProtocol.IdHeader, out var id))
        { throw new ProxyException(LogError.McpSessionFailed); }
        using var request = new HttpRequestMessage(HttpMethod.Post, SessionProtocol.ResumeEndpoint(endpoint))
        { Content = new ByteArrayContent([]) };
        request.Headers.Add(SessionProtocol.IdHeader, id);
        request.Headers.Add(SessionProtocol.ResumeHeader, Encoding.ASCII.GetString(_resumeCredential));
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        { throw new ProxyException(LogError.McpAuthenticationFailed); }
        if (response.StatusCode != HttpStatusCode.OK)
        {
            // 一時的なHTTP障害は回復周期へ戻す。確認は冪等で操作や資格情報を変更しない。
            if (response.StatusCode is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests
                or HttpStatusCode.InternalServerError or HttpStatusCode.BadGateway
                or HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout)
            { throw new HttpRequestException("Session confirmation failed.", null, response.StatusCode); }
            throw new ProxyException(LogError.McpSessionFailed);
        }
        try
        {
            await InspectResponseAsync(response, root =>
            {
                if (root.ValueKind != JsonValueKind.Object || root.EnumerateObject().Count() != 2
                    || !string.Equals(root.GetProperty("sessionId").GetString(), id, StringComparison.Ordinal)
                    || root.GetProperty("resumed").ValueKind != JsonValueKind.True)
                { throw new ProxyException(LogError.McpSessionFailed); }
            }, token);
        }
        catch (Exception exception) when (exception is JsonException or KeyNotFoundException
            or InvalidOperationException or FormatException)
        { throw new ProxyException(LogError.McpSessionFailed); }
    }

    private async Task InitializeAsync()
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
        deadline.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, SessionProtocol.IssuanceEndpoint(endpoint))
            { Content = new ByteArrayContent([]) };
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            { throw new ProxyException(LogError.McpAuthenticationFailed); }
            if (response.StatusCode != HttpStatusCode.Created
                || response.Content.Headers.ContentType?.MediaType != "application/json")
            { throw new ProxyException(LogError.McpSessionFailed); }
            await InspectResponseAsync(response, root =>
            {
                if (root.ValueKind != JsonValueKind.Object || root.EnumerateObject().Count() != 3)
                { throw new ProxyException(LogError.McpSessionFailed); }
                var id = root.GetProperty("sessionId").GetString();
                var credential = root.GetProperty("credential").GetString();
                var resume = root.GetProperty("resumeCredential").GetString();
                if (!Guid.TryParseExact(id, "N", out _) || !SessionProtocol.IsCredential(credential)
                    || !SessionProtocol.IsCredential(resume)) { throw new ProxyException(LogError.McpSessionFailed); }
                _resumeCredential = Encoding.ASCII.GetBytes(resume!);
                _headers.Add(SessionProtocol.IdHeader, id!);
                _headers.Add(SessionProtocol.CredentialHeader, credential!);
            }, deadline.Token);
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { throw; }
        catch (ProxyException) { throw; }
        catch (Exception exception) when (exception is HttpRequestException or IOException or OperationCanceledException
            or JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        { throw new ProxyException(LogError.McpSessionFailed); }
    }

    private static async Task InspectResponseAsync(HttpResponseMessage response, Action<JsonElement> inspect,
        CancellationToken token)
    {
        if (response.Content.Headers.ContentType?.MediaType != "application/json")
        { throw new ProxyException(LogError.McpSessionFailed); }
        await using var stream = await response.Content.ReadAsStreamAsync(token);
        var bytes = new byte[2049];
        try
        {
            var length = 0;
            while (length < bytes.Length)
            {
                var read = await stream.ReadAsync(bytes.AsMemory(length), token);
                if (read == 0) { break; }
                length += read;
            }
            if (length == 0 || length > 2048) { throw new ProxyException(LogError.McpSessionFailed); }
            using var document = JsonDocument.Parse(bytes.AsMemory(0, length));
            inspect(document.RootElement);
        }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    /// <summary>Never displays credentials in diagnostic formatting.</summary>
    public override string ToString() => "(redacted)";

    /// <summary>Releases headers and clears the owned resumption credential bytes after shutdown.</summary>
    public void Dispose()
    {
        _headers.Clear();
        if (_resumeCredential is not null) { CryptographicOperations.ZeroMemory(_resumeCredential); }
    }
}
