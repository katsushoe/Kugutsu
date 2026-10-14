using System.Runtime.InteropServices;
using System.Text;
using Kugutsu.Contracts;

namespace Kugutsu.App;

internal sealed record BrowserDefault(string? Browser, string? DisplayName, string? ProgId,
    string? ExecutablePath, bool Supported, string? Reason);
internal sealed record BrowserDefaultResult(BrowserDefault Http, BrowserDefault Https);

/// <summary>Windowsが現在ユーザーへ解決するプロトコル関連付けを読み取る。</summary>
internal sealed class BrowserDefaults
{
    private const uint ProtocolFlags = 0x1000 | 0x100 | 0x20; // IS_PROTOCOL, NOFIXUPS, NOTRUNCATE
    private const int MaxAssociationLength = 32768;
    private const int NoAssociation = unchecked((int)0x80070483);
    private const int FileNotFound = unchecked((int)0x80070002);

    internal OperationDefinition Definition() => new("browser.get_default", ArgumentSchema.Object([]),
        async (_, token) => ResponseJson.ToElement(await ReadAsync(token)));

    internal async ValueTask<BrowserDefaultResult> ReadAsync(CancellationToken token) =>
        await Task.Run(() => new BrowserDefaultResult(Read("http", token), Read("https", token)), token);

    private static BrowserDefault Read(string protocol, CancellationToken token)
    {
        var progId = Query(protocol, 20, token); // ASSOCSTR_PROGID
        if (progId is null) { return new(null, null, null, null, false, "association_not_found"); }
        var executable = Query(protocol, 2, token);
        var displayName = Query(protocol, 4, token);
        token.ThrowIfCancellationRequested();
        if (executable is null) { return new(null, displayName, progId, null, false, "executable_unresolved"); }
        var browser = Identify(executable);
        return new(browser, displayName, progId, executable, browser != "other",
            browser == "other" ? "unsupported_handler" : null);
    }

    internal static string Identify(string executable) => Path.GetFileName(executable).ToLowerInvariant() switch
    {
        "chrome.exe" => "chrome", "msedge.exe" => "edge", "firefox.exe" => "firefox", _ => "other"
    };

    private static string? Query(string protocol, uint kind, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        uint size = 0;
        var status = AssocQueryString(ProtocolFlags, kind, protocol, null, null, ref size);
        if (status is NoAssociation or FileNotFound) { return null; }
        if (status != 1 || size is < 2 or > MaxAssociationLength)
        { throw new WindowReferenceException(ErrorCode.BROWSER_DISCOVERY_FAILED); }
        var buffer = new StringBuilder((int)size);
        status = AssocQueryString(ProtocolFlags, kind, protocol, null, buffer, ref size);
        token.ThrowIfCancellationRequested();
        if (status is NoAssociation or FileNotFound) { return null; }
        if (status != 0 || size is < 2 or > MaxAssociationLength)
        { throw new WindowReferenceException(ErrorCode.BROWSER_DISCOVERY_FAILED); }
        return buffer.Length == 0 ? null : buffer.ToString();
    }

    [DllImport("shlwapi.dll", EntryPoint = "AssocQueryStringW", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int AssocQueryString(uint flags, uint kind, string association, string? extra,
        StringBuilder? output, ref uint length);
}
