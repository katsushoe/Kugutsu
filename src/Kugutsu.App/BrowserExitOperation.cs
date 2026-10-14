using System.ComponentModel;
using Kugutsu.Contracts;
using Microsoft.Extensions.Logging;

namespace Kugutsu.App;

/// <summary>固定WebDriverセッションを終了し、管理プロセス終了後に参照を破棄する。</summary>
internal sealed class BrowserExitOperation(BrowserSessionRegistry sessions, ApplicationReferenceRegistry applications,
    ILogger<BrowserExitOperation> logger, DomElementRegistry elements)
{
    internal OperationDefinition Definition() => new("browser.exit", ArgumentSchema.Object([
        new("browser", ArgumentSchema.String(7, 4, ["chrome", "edge", "firefox"])),
        new("browserId", ArgumentSchema.String(34, 34))]),
        async (args, token) =>
        {
            var id = args.GetProperty("browserId").GetString()!;
            var kind = BrowserSessionRegistry.Parse(args.GetProperty("browser").GetString()!);
            var target = await sessions.ResolveAsync(id, kind, token);
            var browser = applications.GetProcess(target.Reference.Application, token);
            var driver = applications.GetProcess(target.Reference.Driver, token);
            try
            {
                try { await target.Connection.ExecuteAsync(HttpMethod.Delete, "", null, token); }
                catch (WindowReferenceException error) when (error.Code is ErrorCode.BROWSER_CONNECTION_LOST or ErrorCode.BROWSER_NOT_FOUND)
                {
                    // 終了応答より先に接続が閉じた場合も、保持ハンドルの終了事実で判定する。
                    if (!browser.HasExited) { throw; }
                }
                while (!browser.HasExited) { await Task.Delay(50, token); }
                token.ThrowIfCancellationRequested();
                if (!driver.HasExited)
                {
                    // ブラウザ終了確認後、専用サービスだけを保持ハンドル経由で後処理する。
                    try
                    {
                        applications.Resolve(target.Reference.Driver.ApplicationId, token);
                        driver.Kill(false);
                        await driver.WaitForExitAsync(token);
                    }
                    catch (Exception error) when ((error is Win32Exception or InvalidOperationException
                        || error is WindowReferenceException { Code: ErrorCode.APP_NOT_FOUND }) && driver.HasExited)
                    { logger.LogDebug("[BrowserExit] Driver exited during cleanup."); }
                }
                token.ThrowIfCancellationRequested();
                sessions.Remove(id);
                elements.InvalidateBrowser(id);
                return ResponseJson.ToElement(new { browser = BrowserSessionRegistry.Name(kind), browserId = id,
                    exited = true, driverExited = true, profileRetained = true });
            }
            catch (Win32Exception error)
            { throw new WindowReferenceException(error.NativeErrorCode == 5 ? ErrorCode.ACCESS_DENIED : ErrorCode.BROWSER_EXIT_TIMEOUT); }
            catch (InvalidOperationException) { throw new WindowReferenceException(ErrorCode.BROWSER_CONNECTION_LOST); }
        }, args => BrowserSessionRegistry.ValidBrowserId(args.GetProperty("browserId").GetString()));
}
