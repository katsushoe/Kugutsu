using Kugutsu.Contracts;

namespace Kugutsu.App;

internal interface IExitableShellBinding : IManagedShellBinding
{
    ValueTask RequestExitAsync(CancellationToken token);
}

/// <summary>管理されたシェル専用の終了経路を呼び、プロセスの終了を待つ。</summary>
internal sealed class ShellExitOperations(ShellSessionRegistry sessions, ApplicationReferenceRegistry applications)
{
    internal OperationDefinition TerminalDefinition() => Definition("terminal.exit", ShellSessionKind.Terminal);
    internal OperationDefinition PowerShellDefinition() => Definition("powershell.exit", ShellSessionKind.PowerShell);

    private OperationDefinition Definition(string name, ShellSessionKind kind) => new(name,
        ArgumentSchema.Object([new("sessionId", ArgumentSchema.String(35, 35))]),
        async (args, token) =>
        {
            var id = args.GetProperty("sessionId").GetString()!;
            var session = await sessions.ResolveAsync(id, kind, token);
            if (session.Binding is not IExitableShellBinding binding)
            { throw new WindowReferenceException(ErrorCode.SESSION_EXIT_UNSUPPORTED); }
            await binding.RequestExitAsync(token);
            while (!applications.HasEnded(session.Reference.ShellProcess, token)) { await Task.Delay(50, token); }
            token.ThrowIfCancellationRequested();
            sessions.Remove(id);
            return ResponseJson.ToElement(new { sessionId = id, exited = true, shellProcessId = session.Reference.ShellProcess.ProcessId });
        }, args => ShellSessionRegistry.IsValidId(args.GetProperty("sessionId").GetString()));
}
