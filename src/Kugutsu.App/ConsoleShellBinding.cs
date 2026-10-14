using Kugutsu.Contracts;

namespace Kugutsu.App;

/// <summary>専用Console HostのウィンドウとPowerShellのコンソール所属を再照合する。</summary>
internal sealed class ConsoleShellBinding(ApplicationReference host, ApplicationReference shell, WindowReference window,
    PowerShellFlavor flavor, ManagedProcessTree tree, ConsoleAssociationProbe probe, WindowStateOperations states) : IKeyboardShellBinding, IExitableShellBinding
{
    private sealed record ConsoleIdentity(ApplicationReference Host, ApplicationReference Shell, WindowReference Window);
    private readonly ConsoleIdentity _identity = new(host, shell, window);
    public ShellSessionKind Kind => ShellSessionKind.PowerShell;
    public PowerShellFlavor? PowerShell => flavor;
    public ApplicationReference Application => shell;
    public ApplicationReference ShellProcess => shell;
    public WindowReference Window => window;
    public object InputIdentity => _identity;

    public async ValueTask VerifyBindingAsync(CancellationToken token)
    {
        tree.VerifyRelationship(host, shell, token);
        if (window.Identity.ProcessId != host.ProcessId || window.Identity.ProcessStartedAt != host.ProcessStartedAt)
        { throw new WindowReferenceException(ErrorCode.SESSION_TARGET_UNRESOLVED); }
        await probe.VerifyAsync(shell, window, token);
        tree.VerifyRelationship(host, shell, token);
    }

    public ValueTask FocusAsync(CancellationToken token) => VerifyFocusAsync(token);
    public async ValueTask RequestExitAsync(CancellationToken token)
    {
        await VerifyBindingAsync(token);
        await states.CloseAsync(window.WindowId, token);
    }

    public ValueTask VerifyFocusAsync(CancellationToken token)
    {
        states.VerifyInputTarget(window.WindowId, token);
        return ValueTask.CompletedTask;
    }
    public void Dispose() { /* 管理プロセスを終了せず、共通レジストリの所有権を維持する。 */ }
}
