using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows.Automation;
using Kugutsu.Contracts;

namespace Kugutsu.App;

/// <summary>UIA参照と実際のRuntimeIdを保持する。外部へプロバイダー参照を公開しない。</summary>
internal sealed class UiaElementHandle(AutomationElement element)
{
    internal AutomationElement Element { get; } = element;
    private readonly int[] _runtimeId = element.GetRuntimeId().ToArray();
    internal bool IsCurrent() => _runtimeId.SequenceEqual(Element.GetRuntimeId());
}

/// <summary>UIA基盤。探索・スナップショット・意味操作は各後続項目で公開する。</summary>
internal sealed partial class UiaDriver(WindowReferenceRegistry windows, WindowCoordinates coordinates,
    Func<UiElementRegistry> elementRegistry, ScreenCapture capture, WindowStateOperations windowStates,
    KeyboardOperations keyboard) : IUiDriver
{
    private static readonly string[] OperationNames = ["control.get_state", "control.get_value", "control.invoke",
        "control.set_value", "control.send_keys", "control.select", "control.set_toggle", "control.set_range_value",
        "control.expand_collapse", "control.scroll", "control.select_items", "control.set_text_selection", "grid.get_cell", "grid.set_cell_value"];
    public UiDriverInfo Info { get; } = new("uia", true, null,
        ["root_access", "element_validation", "pattern_discovery", "find", "snapshot"], OperationNames);

    internal ValueTask<UiaElementHandle> GetRootAsync(WindowReference window, CancellationToken token) =>
        RunAsync(() =>
        {
            VerifyWindow(window, token);
            var element = AutomationElement.FromHandle(window.Identity.Handle)
                ?? throw new WindowReferenceException(ErrorCode.UIA_ELEMENT_NOT_FOUND);
            var handle = new UiaElementHandle(element);
            ValidateElement(window, handle, token);
            return handle;
        }, token);

    internal ValueTask<T> WithElementAsync<T>(WindowReference window, UiaElementHandle handle,
        Func<AutomationElement, T> action, CancellationToken token) => RunAsync(() =>
        {
            ValidateElement(window, handle, token);
            var result = action(handle.Element);
            ValidateElement(window, handle, token);
            return result;
        }, token);

    internal async ValueTask InvokeElementAsync(WindowReference window, UiaElementHandle handle, CancellationToken token,
        Action<AutomationElement>? validateAction = null)
    {
        await RunAsync(() =>
        {
            ValidateElement(window, handle, token);
            if (handle.Element.GetCurrentPropertyValue(AutomationElement.IsEnabledProperty, true) is not true)
            { throw new WindowReferenceException(ErrorCode.UIA_ELEMENT_NOT_ENABLED); }
            if (!handle.Element.TryGetCurrentPattern(InvokePattern.Pattern, out var raw) || raw is not InvokePattern invoke)
            { throw new WindowReferenceException(ErrorCode.UIA_PATTERN_UNSUPPORTED); }
            ValidateElement(window, handle, token);
            validateAction?.Invoke(handle.Element);
            token.ThrowIfCancellationRequested();
            invoke.Invoke();
            // Invokeによる対象消滅は許容し、呼出し後の再照合を行わない。
            return true;
        }, token);
    }

    internal ValueTask<string[]> GetPatternsAsync(WindowReference window, UiaElementHandle handle,
        CancellationToken token) => WithElementAsync(window, handle, element =>
            element.GetSupportedPatterns().Select(pattern => pattern.ProgrammaticName)
                .Order(StringComparer.Ordinal).ToArray(), token);

    internal ValueTask<T> WithPatternAsync<T>(WindowReference window, UiaElementHandle handle,
        AutomationPattern pattern, Func<object, T> action, CancellationToken token) => WithElementAsync(window, handle, element =>
        {
            if (!element.TryGetCurrentPattern(pattern, out var value) || value is null)
            { throw new WindowReferenceException(ErrorCode.UIA_PATTERN_UNSUPPORTED); }
            return action(value);
        }, token);

    private void VerifyWindow(WindowReference window, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (windows.Resolve(window.WindowId) != window)
        { throw new WindowReferenceException(ErrorCode.UIA_ELEMENT_STALE); }
    }

    private void ValidateElement(WindowReference window, UiaElementHandle handle, CancellationToken token)
    {
        VerifyWindow(window, token);
        if (!handle.IsCurrent()) { throw new WindowReferenceException(ErrorCode.UIA_ELEMENT_STALE); }
        var root = AutomationElement.FromHandle(window.Identity.Handle)
            ?? throw new WindowReferenceException(ErrorCode.UIA_ELEMENT_NOT_FOUND);
        var current = handle.Element;
        for (var depth = 0; depth < 256 && current is not null; depth++)
        {
            token.ThrowIfCancellationRequested();
            if ((uint)current.Current.ProcessId != window.Identity.ProcessId)
            { throw new WindowReferenceException(ErrorCode.ACCESS_DENIED); }
            if (Automation.Compare(current, root))
            {
                VerifyWindow(window, token);
                if (!handle.IsCurrent()) { throw new WindowReferenceException(ErrorCode.UIA_ELEMENT_STALE); }
                return;
            }
            current = TreeWalker.RawViewWalker.GetParent(current);
        }
        throw new WindowReferenceException(ErrorCode.UIA_ELEMENT_STALE);
    }

    private static async ValueTask<T> RunAsync<T>(Func<T> action, CancellationToken token)
    {
        // WinFormsのUIスレッドからUIAを呼ばず、WindowsのMTA ThreadPoolで実行する。
        return await Task.Run(() =>
        {
            token.ThrowIfCancellationRequested();
            if (Thread.CurrentThread.GetApartmentState() != ApartmentState.MTA)
            { throw new WindowReferenceException(ErrorCode.UI_DRIVER_UNAVAILABLE); }
            try
            {
                var result = action();
                token.ThrowIfCancellationRequested();
                return result;
            }
            catch (ElementNotAvailableException) { throw new WindowReferenceException(ErrorCode.UIA_ELEMENT_STALE); }
            catch (ElementNotEnabledException) { throw new WindowReferenceException(ErrorCode.UIA_ELEMENT_NOT_ENABLED); }
            catch (UnauthorizedAccessException) { throw new WindowReferenceException(ErrorCode.ACCESS_DENIED); }
            catch (NotSupportedException) { throw new WindowReferenceException(ErrorCode.UIA_PATTERN_UNSUPPORTED); }
            catch (COMException exception)
            { throw new WindowReferenceException(exception.HResult == unchecked((int)0x80070005)
                ? ErrorCode.ACCESS_DENIED : ErrorCode.UI_DRIVER_UNAVAILABLE); }
        }, token);
    }

    public ValueTask<UiSnapshot> FindAsync(WindowReference window, UiFindQuery query, CancellationToken token)
        => FindCoreAsync(window, query, 64, false, token);

    private ValueTask<UiSnapshot> FindCoreAsync(WindowReference window, UiFindQuery query, int maxDepth,
        bool hierarchy, CancellationToken token)
    {
        if (query.MaxCandidates is < 1 or > 1000 || query.ProfileId is not null || query.SemanticRole is not null)
        { throw new WindowReferenceException(ErrorCode.INVALID_ARGUMENT); }
        return RunAsync(() =>
        {
            VerifyWindow(window, token);
            var geometry = coordinates.GetGeometry(window.WindowId, token);
            var root = AutomationElement.FromHandle(window.Identity.Handle)
                ?? throw new WindowReferenceException(ErrorCode.UIA_ELEMENT_NOT_FOUND);
            var snapshotId = "s_" + Guid.NewGuid().ToString("N");
            var registry = elementRegistry();
            var candidates = new List<UiCandidate>();
            var pending = new Stack<(AutomationElement Element, int Depth, string? Parent)>();
            pending.Push((root, 0, null));
            var enqueued = 1;
            var truncated = false;
            try
            {
                while (pending.TryPop(out var node))
                {
                    token.ThrowIfCancellationRequested();
                    var element = node.Element;
                    string? currentId = null;
                    try
                    {
                        if ((uint)element.Current.ProcessId != window.Identity.ProcessId)
                        { truncated = true; continue; }
                        var type = element.GetCurrentPropertyValue(AutomationElement.ControlTypeProperty, true) as ControlType;
                        var name = element.GetCurrentPropertyValue(AutomationElement.NameProperty, true) as string;
                        var automationId = element.GetCurrentPropertyValue(AutomationElement.AutomationIdProperty, true) as string;
                        if ((query.AutomationId is null || query.AutomationId == automationId)
                            && (query.Name is null || query.Name == name)
                            && (query.ControlType is null || query.ControlType == type?.ProgrammaticName.Split('.').Last()))
                        {
                            if (candidates.Count == query.MaxCandidates) { truncated = true; break; }
                            var handle = new UiaElementHandle(element);
                            ValidateElement(window, handle, token);
                            var rectangle = element.Current.BoundingRectangle;
                            UiBounds? bounds = null;
                            if (!rectangle.IsEmpty && double.IsFinite(rectangle.Left) && double.IsFinite(rectangle.Top)
                                && double.IsFinite(rectangle.Right) && double.IsFinite(rectangle.Bottom))
                            {
                                try
                                {
                                    var x = checked((int)Math.Floor(rectangle.Left - geometry.ClientScreenBounds.X));
                                    var y = checked((int)Math.Floor(rectangle.Top - geometry.ClientScreenBounds.Y));
                                    bounds = new(x, y, checked((int)Math.Ceiling(rectangle.Right - geometry.ClientScreenBounds.X) - x),
                                        checked((int)Math.Ceiling(rectangle.Bottom - geometry.ClientScreenBounds.Y) - y));
                                }
                                catch (OverflowException) { bounds = null; }
                            }
                            bool? ReadBool(AutomationProperty property) => element.GetCurrentPropertyValue(property, true) is bool value ? value : null;
                            var state = new UiElementState(Enabled: ReadBool(AutomationElement.IsEnabledProperty),
                                Offscreen: ReadBool(AutomationElement.IsOffscreenProperty),
                                Focused: ReadBool(AutomationElement.HasKeyboardFocusProperty));
                            var supported = GetSupportedOperations(element);
                            var reference = registry.Register(window, "uia", handle, snapshotId, null, null, null, token);
                            currentId = reference.Target.ElementId;
                            candidates.Add(new(reference.Target.ElementId, snapshotId, "uia", null, null,
                                type?.ProgrammaticName.Split('.').Last(), null, name, bounds,
                                state,
                                [new("uia", automationId)], null, null, supported, hierarchy ? node.Parent : null));
                        }
                        var child = TreeWalker.ControlViewWalker.GetFirstChild(element);
                        if (node.Depth >= maxDepth) { if (child is not null) { truncated = true; } continue; }
                        while (child is not null)
                        {
                            token.ThrowIfCancellationRequested();
                            if (enqueued >= 10000) { truncated = true; break; }
                            pending.Push((child, node.Depth + 1, hierarchy ? currentId : null)); enqueued++;
                            child = TreeWalker.ControlViewWalker.GetNextSibling(child);
                        }
                    }
                    catch (ElementNotAvailableException) { truncated = true; }
                }
                coordinates.VerifyGeometry(geometry, token);
                VerifyWindow(window, token);
                foreach (var candidate in candidates)
                {
                    var reference = registry.Resolve(candidate.ElementId, window.WindowId, "uia", token);
                    ValidateElement(window, (UiaElementHandle)reference.NativeTarget, token);
                }
                return new UiSnapshot(window.WindowId, "uia", snapshotId, candidates, truncated);
            }
            catch { registry.InvalidateSnapshot(snapshotId); throw; }
        }, token);
    }
    public async ValueTask<UiSnapshot> SnapshotAsync(WindowReference window, UiSnapshotQuery query, CancellationToken token)
    {
        if (query.MaxDepth is < 0 or > 64 || query.MaxCandidates is < 1 or > 1000 || query.ProfileId is not null)
        { throw new WindowReferenceException(ErrorCode.INVALID_ARGUMENT); }
        var geometry = coordinates.GetGeometry(window.WindowId, token);
        var snapshot = await FindCoreAsync(window, new(MaxCandidates: query.MaxCandidates), query.MaxDepth, true, token);
        try
        {
            coordinates.VerifyGeometry(geometry, token);
            if (!query.IncludeImage) { return snapshot; }
            var data = capture.Capture(window.WindowId, token);
            var image = new UiSnapshotImage(Convert.FromBase64String(data.GetProperty("image").GetProperty("data").GetString()!));
            coordinates.VerifyGeometry(geometry, token);
            foreach (var candidate in snapshot.Candidates)
            {
                var reference = elementRegistry().Resolve(candidate.ElementId, window.WindowId, "uia", token);
                await WithElementAsync(window, (UiaElementHandle)reference.NativeTarget, _ => true, token);
            }
            token.ThrowIfCancellationRequested();
            return new(snapshot.WindowId, snapshot.Driver, snapshot.SnapshotId, snapshot.Candidates, snapshot.Truncated, image);
        }
        catch { elementRegistry().InvalidateSnapshot(snapshot.SnapshotId); throw; }
    }
    public async ValueTask<JsonElement?> ExecuteAsync(UiOperationTarget target, string operation,
        JsonElement arguments, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!OperationNames.Contains(operation, StringComparer.Ordinal))
        { throw new WindowReferenceException(ErrorCode.UI_OPERATION_UNSUPPORTED); }
        var reference = elementRegistry().Resolve(target.ElementId, target.Window.WindowId, "uia", token);
        if (reference.Target != target || reference.NativeTarget is not UiaElementHandle handle)
        { throw new WindowReferenceException(ErrorCode.UI_ELEMENT_STALE); }
        if (operation == "control.send_keys")
        {
            await windowStates.ActivateAsync(target.Window.WindowId, token);
            await WithElementAsync(target.Window, handle, element =>
            {
                if (element.GetCurrentPropertyValue(AutomationElement.IsEnabledProperty, true) is not true)
                { throw new WindowReferenceException(ErrorCode.UIA_ELEMENT_NOT_ENABLED); }
                if (element.GetCurrentPropertyValue(AutomationElement.IsKeyboardFocusableProperty, true) is not true)
                { throw new WindowReferenceException(ErrorCode.FOCUS_FAILED); }
                element.SetFocus();
                return true;
            }, token);
            var started = System.Diagnostics.Stopwatch.GetTimestamp();
            while (!await WithElementAsync(target.Window, handle,
                element => element.GetCurrentPropertyValue(AutomationElement.HasKeyboardFocusProperty, true) is true, token))
            {
                if (System.Diagnostics.Stopwatch.GetElapsedTime(started) >= TimeSpan.FromSeconds(1))
                { throw new WindowReferenceException(ErrorCode.FOCUS_FAILED); }
                await Task.Delay(50, token);
            }
            var sentKeys = 0;
            foreach (var item in arguments.GetProperty("keySequence").EnumerateArray())
            {
                await keyboard.PressAsync(target.Window.WindowId, item.GetProperty("key").GetString()!,
                    KeyboardOperations.ReadModifiers(item), token, async cancellation =>
                    {
                        elementRegistry().Resolve(target.ElementId, target.Window.WindowId, "uia", cancellation);
                        await WithElementAsync(target.Window, handle, element =>
                        {
                            if (element.GetCurrentPropertyValue(AutomationElement.HasKeyboardFocusProperty, true) is not true)
                            { throw new WindowReferenceException(ErrorCode.FOCUS_FAILED); }
                            return true;
                        }, cancellation);
                    });
                sentKeys++;
            }
            return ResponseJson.ToElement(new { windowId = target.Window.WindowId, elementId = target.ElementId,
                sentKeys, inputSent = true });
        }
        if (operation == "control.invoke")
        {
            await InvokeElementAsync(target.Window, handle, token);
            return ResponseJson.ToElement(new { windowId = target.Window.WindowId, elementId = target.ElementId, invoked = true });
        }
        return await WithElementAsync(target.Window, handle, element =>
        {
            if (operation == "control.get_value") { return ReadValue(element, target); }
            if (operation == "control.set_toggle") { return SetToggle(element, handle, target, arguments, token); }
            if (operation == "control.set_range_value") { return SetRangeValue(element, handle, target, arguments, token); }
            if (operation == "control.expand_collapse") { return ExpandCollapse(element, handle, target, arguments, token); }
            if (operation == "control.scroll") { return Scroll(element, handle, target, arguments, token); }
            if (operation == "control.select_items") { return SelectItems(element, handle, target, arguments, token); }
            if (operation == "control.set_text_selection") { return SetTextSelection(element, handle, target, arguments, token); }
            if (operation == "grid.get_cell") { return ReadGridCell(element, handle, target, arguments, token); }
            if (operation == "grid.set_cell_value") { return SetGridCellValue(element, handle, target, arguments, token); }
            if (operation == "control.select")
            {
                var selectionElement = element;
                var itemSpecified = arguments.TryGetProperty("item", out var selector);
                if (itemSpecified)
                {
                    if (!element.TryGetCurrentPattern(SelectionPattern.Pattern, out _))
                    { throw new WindowReferenceException(ErrorCode.UIA_NOT_SELECTABLE); }
                    selectionElement = FindSelectionItem(element, selector, target.Window.Identity.ProcessId, token);
                }
                var selectedHandle = new UiaElementHandle(selectionElement);
                ValidateElement(target.Window, selectedHandle, token);
                if (selectionElement.GetCurrentPropertyValue(AutomationElement.IsEnabledProperty, true) is not true)
                { throw new WindowReferenceException(ErrorCode.UIA_ELEMENT_NOT_ENABLED); }
                if (!selectionElement.TryGetCurrentPattern(SelectionItemPattern.Pattern, out var raw) || raw is not SelectionItemPattern selectionItem)
                { throw new WindowReferenceException(ErrorCode.UIA_NOT_SELECTABLE); }
                if (itemSpecified && (selectionItem.Current.SelectionContainer is not { } owner || !Automation.Compare(owner, element)))
                { throw new WindowReferenceException(ErrorCode.UIA_NOT_SELECTABLE); }
                if (!selectionItem.Current.IsSelected)
                {
                    token.ThrowIfCancellationRequested();
                    try { selectionItem.Select(); }
                    catch (ElementNotAvailableException) { throw; }
                    catch (ElementNotEnabledException) { throw; }
                    catch (InvalidOperationException) { throw new WindowReferenceException(ErrorCode.UIA_NOT_SELECTABLE); }
                }
                ValidateElement(target.Window, selectedHandle, token);
                if (!selectionItem.Current.IsSelected)
                { throw new WindowReferenceException(ErrorCode.UIA_SELECTION_VERIFICATION_FAILED); }
                return ResponseJson.ToElement(new { windowId = target.Window.WindowId, elementId = target.ElementId,
                    selectionVerified = true, itemSelected = itemSpecified });
            }
            if (operation == "control.set_value")
            {
                var outcome = WriteValue(element, handle, target, arguments.GetProperty("value").GetString()!, token);
                return ResponseJson.ToElement(new { windowId = target.Window.WindowId, elementId = target.ElementId,
                    valueSet = true, redacted = outcome.Redacted, verification = outcome.Redacted ? "unverified" : "verified",
                    observedValue = outcome.ObservedValue });
            }
            bool? Read(AutomationProperty property) => element.GetCurrentPropertyValue(property, true) is bool value ? value : null;
            bool? selected = null;
            bool? expanded = null;
            string? toggle = null;
            if (element.TryGetCurrentPattern(SelectionItemPattern.Pattern, out var selection) && selection is SelectionItemPattern item)
            { selected = item.Current.IsSelected; }
            if (element.TryGetCurrentPattern(ExpandCollapsePattern.Pattern, out var collapse) && collapse is ExpandCollapsePattern expander)
            {
                expanded = expander.Current.ExpandCollapseState switch
                { ExpandCollapseState.Expanded => true, ExpandCollapseState.Collapsed => false, _ => null };
            }
            if (element.TryGetCurrentPattern(TogglePattern.Pattern, out var value) && value is TogglePattern toggler)
            { toggle = toggler.Current.ToggleState switch { ToggleState.On => "on", ToggleState.Off => "off",
                ToggleState.Indeterminate => "indeterminate", _ => null }; }
            var offscreen = Read(AutomationElement.IsOffscreenProperty);
            return ResponseJson.ToElement(new { windowId = target.Window.WindowId, elementId = target.ElementId, driver = "uia",
                state = new UiElementState(Enabled: Read(AutomationElement.IsEnabledProperty), Visible: offscreen is { } outside ? !outside : null,
                    Offscreen: offscreen, Focused: Read(AutomationElement.HasKeyboardFocusProperty), Selected: selected,
                    Expanded: expanded, ToggleState: toggle),
                sensitive = Read(AutomationElement.IsPasswordProperty),
                supportedPatterns = element.GetSupportedPatterns().Select(pattern => pattern.ProgrammaticName).Order(StringComparer.Ordinal).ToArray(),
                supportedOperations = GetSupportedOperations(element) });
        }, token);
    }

    private static string[] GetSupportedOperations(AutomationElement element)
    {
        var patterns = element.GetSupportedPatterns();
        var operations = new List<string> { "control.get_state" };
        if (patterns.Contains(ValuePattern.Pattern) || patterns.Contains(TextPattern.Pattern)) { operations.Add("control.get_value"); }
        if (patterns.Contains(InvokePattern.Pattern)) { operations.Add("control.invoke"); }
        if (patterns.Contains(TogglePattern.Pattern)) { operations.Add("control.set_toggle"); }
        if (patterns.Contains(ExpandCollapsePattern.Pattern)) { operations.Add("control.expand_collapse"); }
        if (patterns.Contains(ScrollPattern.Pattern)) { operations.Add("control.scroll"); }
        if (patterns.Contains(GridPattern.Pattern)) { operations.Add("grid.get_cell"); operations.Add("grid.set_cell_value"); }
        if (element.TryGetCurrentPattern(TextPattern.Pattern, out var text) && text is TextPattern textSelection
            && textSelection.SupportedTextSelection != SupportedTextSelection.None)
        { operations.Add("control.set_text_selection"); }
        if (element.TryGetCurrentPattern(SelectionPattern.Pattern, out var multiple) && multiple is SelectionPattern multipleSelection
            && multipleSelection.Current.CanSelectMultiple) { operations.Add("control.select_items"); }
        if (element.TryGetCurrentPattern(RangeValuePattern.Pattern, out var range) && range is RangeValuePattern rangeValue
            && !rangeValue.Current.IsReadOnly) { operations.Add("control.set_range_value"); }
        if (patterns.Contains(SelectionItemPattern.Pattern) || patterns.Contains(SelectionPattern.Pattern))
        { operations.Add("control.select"); }
        if (element.GetCurrentPropertyValue(AutomationElement.IsKeyboardFocusableProperty, true) is true)
        { operations.Add("control.send_keys"); }
        if (element.TryGetCurrentPattern(ValuePattern.Pattern, out var value) && value is ValuePattern writable
            && !writable.Current.IsReadOnly) { operations.Add("control.set_value"); }
        return operations.ToArray();
    }

    private static AutomationElement FindSelectionItem(AutomationElement container, JsonElement selector,
        uint processId, CancellationToken token)
    {
        string? Read(string key) => selector.TryGetProperty(key, out var value) ? value.GetString() : null;
        var name = Read("name");
        var id = Read("automationId");
        var stack = new Stack<(AutomationElement Element, int Depth)>();
        stack.Push((container, 0));
        var enqueued = 1;
        AutomationElement? match = null;
        while (stack.TryPop(out var node))
        {
            token.ThrowIfCancellationRequested();
            if ((uint)node.Element.Current.ProcessId != processId) { continue; }
            if (node.Depth > 0 && (name is null || node.Element.Current.Name == name)
                && (id is null || node.Element.Current.AutomationId == id))
            {
                if (match is not null) { throw new WindowReferenceException(ErrorCode.UIA_ELEMENT_AMBIGUOUS); }
                match = node.Element;
            }
            var child = TreeWalker.ControlViewWalker.GetFirstChild(node.Element);
            if (node.Depth >= 64 && child is not null)
            { throw new WindowReferenceException(ErrorCode.UIA_SEARCH_LIMIT_EXCEEDED); }
            while (child is not null)
            {
                token.ThrowIfCancellationRequested();
                if (enqueued >= 10000) { throw new WindowReferenceException(ErrorCode.UIA_SEARCH_LIMIT_EXCEEDED); }
                stack.Push((child, node.Depth + 1)); enqueued++;
                child = TreeWalker.ControlViewWalker.GetNextSibling(child);
            }
        }
        return match ?? throw new WindowReferenceException(ErrorCode.UIA_ELEMENT_NOT_FOUND);
    }

    private static JsonElement ReadValue(AutomationElement element, UiOperationTarget target)
    {
        JsonElement Redacted() => ResponseJson.ToElement(new { windowId = target.Window.WindowId,
            elementId = target.ElementId, value = (string?)null, redacted = true, source = (string?)null,
            truncated = false, exact = (bool?)null, isReadOnly = (bool?)null });
        if (element.GetCurrentPropertyValue(AutomationElement.IsPasswordProperty, true) is not false)
        { return Redacted(); }
        string text;
        string source;
        bool? readOnly = null;
        if (element.TryGetCurrentPattern(ValuePattern.Pattern, out var value) && value is ValuePattern pattern)
        { text = pattern.Current.Value; readOnly = pattern.Current.IsReadOnly; source = "uia_value"; }
        else if (element.TryGetCurrentPattern(TextPattern.Pattern, out var range) && range is TextPattern textPattern)
        { text = textPattern.DocumentRange.GetText(65537); source = "uia_text"; }
        else { throw new WindowReferenceException(ErrorCode.UIA_PATTERN_UNSUPPORTED); }
        if (element.GetCurrentPropertyValue(AutomationElement.IsPasswordProperty, true) is not false)
        { return Redacted(); }
        var truncated = text.Length > 65536;
        if (truncated)
        {
            var length = char.IsHighSurrogate(text[65535]) ? 65535 : 65536;
            text = text[..length];
        }
        return ResponseJson.ToElement(new { windowId = target.Window.WindowId, elementId = target.ElementId,
            value = text, redacted = false, source, truncated, exact = !truncated, isReadOnly = readOnly });
    }
}
