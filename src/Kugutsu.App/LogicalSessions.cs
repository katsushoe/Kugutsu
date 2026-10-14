using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http;
using Kugutsu.Hosting;

namespace Kugutsu.App;

/// <summary>A server-verified owner snapshot to copy into future queued requests.</summary>
internal sealed record LogicalOwner(Guid Id);

/// <summary>Owns logical identities for the lifetime of this server, storing only credential hashes.</summary>
internal sealed class LogicalSessions(int capacity) : IDisposable
{
    private static readonly object OwnerKey = new();
    private readonly object _gate = new();
    private readonly Dictionary<Guid, Entry> _entries = [];
    private bool _disposed;

    /// <summary>Issues one independent identity. A missing grant means the configured limit was reached.</summary>
    public Grant? Issue()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_entries.Count >= capacity) { return null; }
            var id = Guid.NewGuid();
            var credential = NewCredential();
            var resumeCredential = NewCredential();
            _entries.Add(id, new Entry(Hash(credential), Hash(resumeCredential)));
            return new Grant(id.ToString("N"), credential, resumeCredential);
        }
    }

    /// <summary>Validates both logical headers and installs a trusted owner snapshot.</summary>
    public bool Authenticate(HttpContext context) => Validate(context, SessionProtocol.CredentialHeader, false);

    /// <summary>Verifies resumption against the existing owner without allocating or rotating credentials.</summary>
    public bool Resume(HttpContext context) => Validate(context, SessionProtocol.ResumeHeader, true);

    private bool Validate(HttpContext context, string credentialHeader, bool resuming)
    {
        var headers = context.Request.Headers;
        if (!headers.TryGetValue(SessionProtocol.IdHeader, out var ids) || ids.Count != 1
            || !Guid.TryParseExact(ids[0], "N", out var id)
            || !headers.TryGetValue(credentialHeader, out var credentials)
            || credentials.Count != 1 || !SessionProtocol.IsCredential(credentials[0])) { return false; }
        var supplied = Hash(credentials[0]!);
        try
        {
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (!_entries.TryGetValue(id, out var entry)
                    || !CryptographicOperations.FixedTimeEquals(supplied,
                        resuming ? entry.ResumeHash : entry.CredentialHash)) { return false; }
                context.Items[OwnerKey] = new LogicalOwner(id);
                return true;
            }
        }
        finally { CryptographicOperations.ZeroMemory(supplied); }
    }

    /// <summary>Retrieves only the identity installed by authenticated HTTP middleware.</summary>
    public static LogicalOwner GetOwner(HttpContext context) =>
        context.Items.TryGetValue(OwnerKey, out var owner) && owner is LogicalOwner verified
            ? verified : throw new InvalidOperationException("Logical owner is unavailable.");

    private static string NewCredential()
    {
        var bytes = RandomNumberGenerator.GetBytes(32);
        try { return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_'); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    private static byte[] Hash(string credential)
    {
        Span<byte> bytes = stackalloc byte[SessionProtocol.CredentialLength];
        try { Encoding.ASCII.GetBytes(credential, bytes); return SHA256.HashData(bytes); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    /// <summary>Clears hashes when the host is disposed. Sessions are never written to disk.</summary>
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) { return; }
            foreach (var entry in _entries.Values)
            {
                CryptographicOperations.ZeroMemory(entry.CredentialHash);
                CryptographicOperations.ZeroMemory(entry.ResumeHash);
            }
            _entries.Clear();
            _disposed = true;
        }
    }

    private sealed record Entry(byte[] CredentialHash, byte[] ResumeHash);

    /// <summary>One-time private HTTP response, never an MCP tool result or diagnostic.</summary>
    internal sealed record Grant(string SessionId, string Credential, string ResumeCredential)
    {
        public override string ToString() => "(redacted)";
    }
}
