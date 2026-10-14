using System.Windows.Automation;
using Kugutsu.Contracts;

namespace Kugutsu.App;

/// <summary>Windows Terminalの識別タブ・単一入力ペイン・管理子孫プロセスを照合する。</summary>
internal sealed class TerminalShellBinding : IKeyboardShellBinding, IExitableShellBinding
{
    private sealed record Topology(UiaElementHandle Tab, UiaElementHandle Input);
    private readonly UiaDriver _uia;
    private readonly ManagedProcessTree _processTree;
    private readonly string _marker;
    private readonly Topology _identity;
    public ShellSessionKind Kind => ShellSessionKind.Terminal;
    public PowerShellFlavor? PowerShell { get; }
    public ApplicationReference Application { get; }
    public ApplicationReference ShellProcess { get; }
    public WindowReference Window { get; }
    public object InputIdentity => _identity;
    internal UiaElementHandle Input => _identity.Input;
    internal UiaElementHandle Tab => _identity.Tab;
    internal UiaDriver Driver => _uia;

    private TerminalShellBinding(UiaDriver uia, ManagedProcessTree processTree, string marker, Topology identity,
        ApplicationReference application, ApplicationReference shell, WindowReference window, PowerShellFlavor flavor)
    {
        _uia = uia; _processTree = processTree; _marker = marker; _identity = identity;
        Application = application; ShellProcess = shell; Window = window; PowerShell = flavor;
    }

    internal static async ValueTask<TerminalShellBinding> CreateAsync(UiaDriver uia, ManagedProcessTree tree,
        string marker, ApplicationReference application, ApplicationReference shell, WindowReference window,
        PowerShellFlavor flavor, CancellationToken token)
    {
        while (true)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                var identity = await ReadTopologyAsync(uia, window, marker, token);
                var binding = new TerminalShellBinding(uia, tree, marker, identity, application, shell, window, flavor);
                await binding.VerifyBindingAsync(token);
                return binding;
            }
            catch (WindowReferenceException exception) when (exception.Code == ErrorCode.UIA_ELEMENT_NOT_FOUND)
            { await Task.Delay(100, token); }
        }
    }

    public async ValueTask VerifyBindingAsync(CancellationToken token)
    {
        _processTree.VerifyRelationship(Application, ShellProcess, token);
        var current = await ReadTopologyAsync(_uia, Window, _marker, token);
        await _uia.WithElementAsync(Window, _identity.Tab, element =>
        {
            if (!Automation.Compare(element, current.Tab.Element))
            { throw new WindowReferenceException(ErrorCode.SESSION_TARGET_UNRESOLVED); }
            return true;
        }, token);
        await _uia.WithElementAsync(Window, _identity.Input, element =>
        {
            if (!Automation.Compare(element, current.Input.Element))
            { throw new WindowReferenceException(ErrorCode.SESSION_TARGET_UNRESOLVED); }
            return true;
        }, token);
        _processTree.VerifyRelationship(Application, ShellProcess, token);
    }

    public async ValueTask FocusAsync(CancellationToken token)
    {
        await _uia.WithElementAsync(Window, Input, element =>
        {
            if (element.GetCurrentPropertyValue(AutomationElement.IsEnabledProperty, true) is not true
                || element.GetCurrentPropertyValue(AutomationElement.IsKeyboardFocusableProperty, true) is not true)
            { throw new WindowReferenceException(ErrorCode.FOCUS_FAILED); }
            try { element.SetFocus(); }
            catch (ElementNotAvailableException) { throw; }
            catch (ElementNotEnabledException) { throw; }
            catch (InvalidOperationException) { throw new WindowReferenceException(ErrorCode.FOCUS_FAILED); }
            return true;
        }, token);
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        while (!await _uia.WithElementAsync(Window, Input,
            element => element.GetCurrentPropertyValue(AutomationElement.HasKeyboardFocusProperty, true) is true, token))
        {
            if (System.Diagnostics.Stopwatch.GetElapsedTime(started) >= TimeSpan.FromSeconds(1))
            { throw new WindowReferenceException(ErrorCode.FOCUS_FAILED); }
            await Task.Delay(50, token);
        }
    }

    public async ValueTask VerifyFocusAsync(CancellationToken token)
    {
        await _uia.WithElementAsync(Window, Input, element =>
        {
            if (element.GetCurrentPropertyValue(AutomationElement.HasKeyboardFocusProperty, true) is not true
                || element.GetCurrentPropertyValue(AutomationElement.IsEnabledProperty, true) is not true)
            { throw new WindowReferenceException(ErrorCode.FOCUS_FAILED); }
            return true;
        }, token);
    }

    public async ValueTask RequestExitAsync(CancellationToken token)
    {
        await VerifyBindingAsync(token);
        var close = await _uia.WithElementAsync(Window, Tab, tab =>
        {
            var pending = new Stack<(AutomationElement Element, int Depth)>();
            pending.Push((tab, 0));
            var enqueued = 1;
            AutomationElement? match = null;
            while (pending.TryPop(out var node))
            {
                token.ThrowIfCancellationRequested();
                if (node.Element.GetCurrentPropertyValue(AutomationElement.ControlTypeProperty, true) == ControlType.Button
                    && node.Element.GetCurrentPropertyValue(AutomationElement.AutomationIdProperty, true) as string == "CloseButton")
                {
                    if (match is not null) { throw new WindowReferenceException(ErrorCode.SESSION_EXIT_UNSUPPORTED); }
                    match = node.Element;
                }
                var child = TreeWalker.ControlViewWalker.GetFirstChild(node.Element);
                if (node.Depth >= 16 && child is not null) { throw new WindowReferenceException(ErrorCode.SESSION_EXIT_UNSUPPORTED); }
                while (child is not null)
                {
                    token.ThrowIfCancellationRequested();
                    if (++enqueued > 1000) { throw new WindowReferenceException(ErrorCode.SESSION_EXIT_UNSUPPORTED); }
                    pending.Push((child, node.Depth + 1));
                    child = TreeWalker.ControlViewWalker.GetNextSibling(child);
                }
            }
            if (match is null || match.GetCurrentPropertyValue(AutomationElement.IsEnabledProperty, true) is not true
                || !match.TryGetCurrentPattern(InvokePattern.Pattern, out _))
            { throw new WindowReferenceException(ErrorCode.SESSION_EXIT_UNSUPPORTED); }
            return new UiaElementHandle(match);
        }, token);
        await VerifyBindingAsync(token);
        await _uia.InvokeElementAsync(Window, close, token, element =>
        {
            if (!Tab.IsCurrent() || !Tab.Element.Current.Name.Contains(_marker, StringComparison.Ordinal))
            { throw new WindowReferenceException(ErrorCode.SESSION_TARGET_UNRESOLVED); }
            AutomationElement? current = element;
            for (var depth = 0; depth < 64 && current is not null; depth++)
            {
                token.ThrowIfCancellationRequested();
                if (Automation.Compare(current, Tab.Element)) { return; }
                current = TreeWalker.RawViewWalker.GetParent(current);
            }
            throw new WindowReferenceException(ErrorCode.SESSION_TARGET_UNRESOLVED);
        });
    }

    private static async ValueTask<Topology> ReadTopologyAsync(UiaDriver uia, WindowReference window,
        string marker, CancellationToken token)
    {
        var root = await uia.GetRootAsync(window, token);
        return await uia.WithElementAsync(window, root, element =>
        {
            var tabs = new List<AutomationElement>();
            var inputs = new List<AutomationElement>();
            var paneCount = 0;
            var pending = new Stack<(AutomationElement Element, int Depth)>();
            pending.Push((element, 0));
            var count = 1;
            while (pending.TryPop(out var node))
            {
                token.ThrowIfCancellationRequested();
                var current = node.Element;
                if ((uint)current.Current.ProcessId != window.Identity.ProcessId)
                { throw new WindowReferenceException(ErrorCode.SESSION_TARGET_UNRESOLVED); }
                var name = current.GetCurrentPropertyValue(AutomationElement.NameProperty, true) as string;
                if (current.GetCurrentPropertyValue(AutomationElement.ControlTypeProperty, true) == ControlType.TabItem
                    && name?.Contains(marker, StringComparison.Ordinal) == true) { tabs.Add(current); }
                if (current.GetCurrentPropertyValue(AutomationElement.ClassNameProperty, true) as string == "TermControl")
                {
                    paneCount++;
                    if (name == marker) { inputs.Add(current); }
                }
                var child = TreeWalker.ControlViewWalker.GetFirstChild(current);
                if (node.Depth >= 64 && child is not null)
                { throw new WindowReferenceException(ErrorCode.SESSION_TARGET_UNRESOLVED); }
                while (child is not null)
                {
                    token.ThrowIfCancellationRequested();
                    if (++count > 10000) { throw new WindowReferenceException(ErrorCode.SESSION_TARGET_UNRESOLVED); }
                    pending.Push((child, node.Depth + 1));
                    child = TreeWalker.ControlViewWalker.GetNextSibling(child);
                }
            }
            if (tabs.Count == 0 || inputs.Count == 0) { throw new WindowReferenceException(ErrorCode.UIA_ELEMENT_NOT_FOUND); }
            if (tabs.Count != 1 || inputs.Count != 1 || paneCount != 1
                || !tabs[0].TryGetCurrentPattern(SelectionItemPattern.Pattern, out var raw) || raw is not SelectionItemPattern selection
                || !selection.Current.IsSelected
                || inputs[0].GetCurrentPropertyValue(AutomationElement.IsEnabledProperty, true) is not true
                || inputs[0].GetCurrentPropertyValue(AutomationElement.IsKeyboardFocusableProperty, true) is not true)
            { throw new WindowReferenceException(ErrorCode.SESSION_TARGET_UNRESOLVED); }
            return new Topology(new(tabs[0]), new(inputs[0]));
        }, token);
    }

    public void Dispose() { /* UIA参照は保持のみ。プロセスハンドルは共通レジストリが所有する。 */ }
}
