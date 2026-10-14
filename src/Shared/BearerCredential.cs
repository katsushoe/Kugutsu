using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;

namespace Kugutsu.Hosting;

/// <summary>Owns token bytes without exposing a serializable token property.</summary>
internal sealed class BearerCredential : IDisposable
{
    private readonly object _gate = new();
    private readonly byte[] _token;
    private bool _disposed;

    internal BearerCredential(byte[] token) { _token = token; }

    /// <summary>Creates an authorization header only for the transport sending a request.</summary>
    public AuthenticationHeaderValue CreateAuthorizationHeader()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return new AuthenticationHeaderValue("Bearer", Encoding.ASCII.GetString(_token));
        }
    }

    /// <summary>Compares supplied token bytes without early exit on byte differences.</summary>
    public bool Matches(ReadOnlySpan<byte> token)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return CryptographicOperations.FixedTimeEquals(_token, token);
        }
    }

    /// <summary>Prevents diagnostic formatting from displaying the token.</summary>
    public override string ToString() => "(redacted)";

    /// <summary>Clears the owned bytes when the host or proxy releases its credential.</summary>
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) { return; }
            CryptographicOperations.ZeroMemory(_token);
            _disposed = true;
        }
    }
}
