using Kugutsu.Contracts;

namespace Kugutsu.App;

internal interface IKeyboardShellBinding : IManagedShellBinding
{
    ValueTask FocusAsync(CancellationToken token);
    ValueTask VerifyFocusAsync(CancellationToken token);
}

/// <summary>セッション入力先とフォーカスを再照合し、文字とキーを順番に送る。</summary>
internal sealed class ShellInputOperations(ShellSessionRegistry sessions, WindowStateOperations windows, KeyboardOperations keyboard)
{
    internal OperationDefinition TerminalDefinition() => Definition("terminal.send_keys", ShellSessionKind.Terminal);
    internal OperationDefinition PowerShellDefinition() => Definition("powershell.send_keys", ShellSessionKind.PowerShell);

    private OperationDefinition Definition(string name, ShellSessionKind kind) => new(name,
        ArgumentSchema.Object([
            new("sessionId", ArgumentSchema.String(35, 35)),
            new("text", ArgumentSchema.String(65536, 1), false),
            new("keySequence", ArgumentSchema.Array(ArgumentSchema.Object([
                new("key", ArgumentSchema.String(16, 1, KeyboardOperations.KeyNames)),
                new("modifiers", ArgumentSchema.Array(ArgumentSchema.String(5, 3, KeyboardOperations.ModifierNames), 4), false)]), 100, 1), false)]),
        async (args, token) =>
        {
            var id = args.GetProperty("sessionId").GetString()!;
            var session = await sessions.ResolveAsync(id, kind, token);
            if (session.Binding is not IKeyboardShellBinding binding)
            { throw new WindowReferenceException(ErrorCode.SESSION_TARGET_UNRESOLVED); }
            await windows.ActivateAsync(session.Reference.Window.WindowId, token);
            await VerifyReference(token);
            await binding.FocusAsync(token);
            var sentTextUnits = 0;
            var sentKeys = 0;
            if (args.TryGetProperty("text", out var text))
            {
                var content = text.GetString()!;
                await keyboard.TypeTextAsync(session.Reference.Window.WindowId, content, token, VerifyFocus);
                sentTextUnits = content.Length;
            }
            if (args.TryGetProperty("keySequence", out var keys))
            {
                foreach (var item in keys.EnumerateArray())
                {
                    await keyboard.PressAsync(session.Reference.Window.WindowId, item.GetProperty("key").GetString()!,
                        KeyboardOperations.ReadModifiers(item), token, VerifyFocus);
                    sentKeys++;
                }
            }
            return ResponseJson.ToElement(new { sessionId = id, windowId = session.Reference.Window.WindowId,
                inputSent = true, sentTextUnits, sentKeys, commandCompletion = "not_observed" });

            async ValueTask VerifyReference(CancellationToken cancellation)
            {
                var current = await sessions.ResolveAsync(id, kind, cancellation);
                if (current.Reference != session.Reference || !ReferenceEquals(current.Binding, binding))
                { throw new WindowReferenceException(ErrorCode.SESSION_TARGET_UNRESOLVED); }
            }

            async ValueTask VerifyFocus(CancellationToken cancellation)
            {
                await VerifyReference(cancellation);
                windows.VerifyInputTarget(session.Reference.Window.WindowId, cancellation);
                await binding.VerifyFocusAsync(cancellation);
                windows.VerifyInputTarget(session.Reference.Window.WindowId, cancellation);
            }
        }, args => ShellSessionRegistry.IsValidId(args.GetProperty("sessionId").GetString())
            && (args.TryGetProperty("text", out _) || args.TryGetProperty("keySequence", out _))
            && (!args.TryGetProperty("text", out var text) || KeyboardOperations.ValidText(text.GetString()!))
            && (!args.TryGetProperty("keySequence", out var keys) || keys.EnumerateArray().All(item =>
                KeyboardOperations.ReadModifiers(item).Distinct(StringComparer.Ordinal).Count() == KeyboardOperations.ReadModifiers(item).Length)));
}
