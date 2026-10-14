using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Kugutsu.Contracts;

namespace Kugutsu.App;

/// <summary>所有ドライバーのループバック待受を照合してW3C WebDriverを送信する。</summary>
internal sealed class WebDriverTransport(ApplicationReference driver, int port, ApplicationReferenceRegistry applications) : IDisposable
{
    private const int ResponseLimit = 4 * 1024 * 1024;
    private readonly HttpClient _client = new(new SocketsHttpHandler
    {
        UseProxy = false, AllowAutoRedirect = false, UseCookies = false,
        ConnectTimeout = TimeSpan.FromSeconds(3), MaxConnectionsPerServer = 1
    }) { Timeout = Timeout.InfiniteTimeSpan };

    internal void Verify(CancellationToken token)
    {
        try
        {
            applications.Resolve(driver.ApplicationId, token);
            VerifyListener(port, driver.ProcessId, token);
            applications.Resolve(driver.ApplicationId, token);
        }
        catch (WindowReferenceException error) when (error.Code is ErrorCode.APP_NOT_FOUND or ErrorCode.APP_INSTANCE_UNRESOLVED)
        { throw Lost(); }
    }

    internal async ValueTask<JsonElement> SendAsync(HttpMethod method, IReadOnlyList<string> path,
        JsonElement? body, CancellationToken token)
    {
        Verify(token);
        if (path.Count is < 1 or > 8 || path.Any(part => string.IsNullOrEmpty(part) || part.Length > 1024 || part.Contains('\0')))
        { throw new WindowReferenceException(ErrorCode.INVALID_ARGUMENT); }
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        using var request = new HttpRequestMessage(method,
            new Uri($"http://127.0.0.1:{port}/" + string.Join('/', path.Select(Uri.EscapeDataString))));
        if (body is { } content)
        {
            var bytes = Encoding.UTF8.GetBytes(content.GetRawText());
            if (bytes.Length > ResponseLimit) { throw new WindowReferenceException(ErrorCode.INVALID_ARGUMENT); }
            request.Content = new ByteArrayContent(bytes);
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        }
        try
        {
            using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if ((int)response.StatusCode is >= 300 and < 400 || response.Content.Headers.ContentLength > ResponseLimit)
            { throw new WindowReferenceException(ErrorCode.BROWSER_CONNECTION_LOST); }
            await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
            using var memory = new MemoryStream();
            var buffer = new byte[8192];
            int read;
            while ((read = await stream.ReadAsync(buffer, timeout.Token)) != 0)
            {
                if (memory.Length + read > ResponseLimit) { throw new WindowReferenceException(ErrorCode.BROWSER_CONNECTION_LOST); }
                memory.Write(buffer, 0, read);
            }
            using var document = JsonDocument.Parse(memory.ToArray(), new JsonDocumentOptions { MaxDepth = 32 });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || HasDuplicateProperties(root) || !root.TryGetProperty("value", out var value))
            { throw new WindowReferenceException(ErrorCode.BROWSER_CONNECTION_LOST); }
            Verify(token);
            if (!response.IsSuccessStatusCode)
            {
                var code = value.ValueKind == JsonValueKind.Object && value.TryGetProperty("error", out var error)
                    && error.ValueKind == JsonValueKind.String ? error.GetString() : null;
                throw new WindowReferenceException(MapError(code));
            }
            return value.Clone();
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        { throw new WindowReferenceException(ErrorCode.TIMEOUT); }
        catch (Exception error) when (error is HttpRequestException or IOException or JsonException)
        { throw new WindowReferenceException(ErrorCode.BROWSER_CONNECTION_LOST); }
    }

    private static ErrorCode MapError(string? error) => error switch
    {
        "invalid session id" => ErrorCode.BROWSER_CONNECTION_LOST, "no such window" => ErrorCode.BROWSER_TAB_NOT_FOUND,
        "no such element" => ErrorCode.DOM_ELEMENT_NOT_FOUND, "stale element reference" => ErrorCode.DOM_ELEMENT_STALE,
        "element not interactable" or "element click intercepted" or "invalid element state" => ErrorCode.DOM_ELEMENT_NOT_INTERACTABLE,
        "no such frame" => ErrorCode.DOM_FRAME_NOT_FOUND, "timeout" or "script timeout" => ErrorCode.TIMEOUT,
        "invalid argument" or "invalid selector" => ErrorCode.INVALID_ARGUMENT,
        "session not created" => ErrorCode.BROWSER_LAUNCH_FAILED,
        "unsupported operation" or "unknown command" => ErrorCode.BROWSER_CAPABILITY_UNSUPPORTED,
        _ => ErrorCode.BROWSER_NAVIGATION_FAILED
    };

    internal static bool HasDuplicateProperties(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Array) { return element.EnumerateArray().Any(HasDuplicateProperties); }
        if (element.ValueKind != JsonValueKind.Object) { return false; }
        var names = new HashSet<string>(StringComparer.Ordinal);
        return element.EnumerateObject().Any(property => !names.Add(property.Name) || HasDuplicateProperties(property.Value));
    }

    internal static void VerifyListener(int port, int pid, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        uint size = 0;
        var status = GetExtendedTcpTable(0, ref size, false, 2, 3, 0); // AF_INET / OWNER_PID_LISTENER
        if (status != 122 || size is < 4 or > 1024 * 1024) { throw Lost(); }
        var memory = Marshal.AllocHGlobal((int)size);
        try
        {
            status = GetExtendedTcpTable(memory, ref size, false, 2, 3, 0);
            if (status != 0) { throw Lost(); }
            var count = Marshal.ReadInt32(memory);
            var rowSize = Marshal.SizeOf<TcpRow>();
            if (count < 0 || 4L + (long)count * rowSize > size) { throw Lost(); }
            var matches = 0;
            for (var index = 0; index < count; index++)
            {
                var row = Marshal.PtrToStructure<TcpRow>(memory + 4 + index * rowSize);
                var nativePort = (int)((row.Port & 255) << 8 | (row.Port >> 8 & 255));
                if (nativePort != port || row.Address != 0x0100007f) { continue; }
                if (row.Pid != pid || row.State != 2) { throw Lost(); }
                matches++;
            }
            token.ThrowIfCancellationRequested();
            if (matches != 1) { throw Lost(); }
        }
        finally { Marshal.FreeHGlobal(memory); }
    }

    private static WindowReferenceException Lost() => new(ErrorCode.BROWSER_CONNECTION_LOST);
    public void Dispose() => _client.Dispose();
    [StructLayout(LayoutKind.Sequential)]
    private struct TcpRow { internal uint State, Address, Port, RemoteAddress, RemotePort, Pid; }
    [DllImport("iphlpapi.dll", ExactSpelling = true)]
    private static extern uint GetExtendedTcpTable(nint table, ref uint size, [MarshalAs(UnmanagedType.Bool)] bool order,
        uint family, uint tableClass, uint reserved);
}
