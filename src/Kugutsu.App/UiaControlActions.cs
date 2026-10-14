using System.Text.Json;
using System.Windows.Automation;
using Kugutsu.Contracts;

namespace Kugutsu.App;

internal sealed partial class UiaDriver
{
    private sealed record ValueWriteOutcome(bool Redacted, string? ObservedValue);

    private ValueWriteOutcome WriteValue(AutomationElement element, UiaElementHandle handle,
        UiOperationTarget target, string requested, CancellationToken token, bool cell = false)
    {
        if (element.GetCurrentPropertyValue(AutomationElement.IsEnabledProperty, true) is not true)
        { throw new WindowReferenceException(ErrorCode.UIA_ELEMENT_NOT_ENABLED); }
        if (!element.TryGetCurrentPattern(ValuePattern.Pattern, out var raw) || raw is not ValuePattern writable)
        { throw new WindowReferenceException(cell ? ErrorCode.UIA_CELL_NOT_EDITABLE : ErrorCode.UIA_PATTERN_UNSUPPORTED); }
        if (writable.Current.IsReadOnly)
        { throw new WindowReferenceException(cell ? ErrorCode.UIA_CELL_NOT_EDITABLE : ErrorCode.UIA_VALUE_READ_ONLY); }
        var redacted = element.GetCurrentPropertyValue(AutomationElement.IsPasswordProperty, true) is not false;
        ValidateElement(target.Window, handle, token);
        token.ThrowIfCancellationRequested();
        try { writable.SetValue(requested); }
        catch (ElementNotAvailableException) { throw; }
        catch (ElementNotEnabledException) { throw; }
        catch (InvalidOperationException) { throw new WindowReferenceException(ErrorCode.UIA_VALUE_SET_FAILED); }
        catch (ArgumentException) { throw new WindowReferenceException(ErrorCode.INVALID_ARGUMENT); }
        ValidateElement(target.Window, handle, token);
        redacted |= element.GetCurrentPropertyValue(AutomationElement.IsPasswordProperty, true) is not false;
        string? observed = null;
        if (!redacted)
        {
            observed = writable.Current.Value;
            if (element.GetCurrentPropertyValue(AutomationElement.IsPasswordProperty, true) is not false)
            { observed = null; redacted = true; }
            else if (!string.Equals(observed, requested, StringComparison.Ordinal))
            { throw new WindowReferenceException(ErrorCode.UIA_VALUE_VERIFICATION_FAILED); }
        }
        return new(redacted, observed);
    }

    private JsonElement Scroll(AutomationElement element, UiaElementHandle handle,
        UiOperationTarget target, JsonElement arguments, CancellationToken token)
    {
        if (element.GetCurrentPropertyValue(AutomationElement.IsEnabledProperty, true) is not true)
        { throw new WindowReferenceException(ErrorCode.UIA_ELEMENT_NOT_ENABLED); }
        if (!element.TryGetCurrentPattern(ScrollPattern.Pattern, out var raw) || raw is not ScrollPattern pattern)
        { throw new WindowReferenceException(ErrorCode.UIA_NOT_SCROLLABLE); }
        var direction = arguments.GetProperty("direction").GetString()!;
        var horizontal = direction is "left" or "right";
        var decrease = direction is "left" or "up";
        var large = arguments.GetProperty("step").GetString() == "large";
        var amount = (decrease, large) switch
        {
            (true, true) => ScrollAmount.LargeDecrement, (true, false) => ScrollAmount.SmallDecrement,
            (false, true) => ScrollAmount.LargeIncrement, _ => ScrollAmount.SmallIncrement
        };
        var repeat = arguments.GetProperty("repeat").GetInt32();
        for (var index = 0; index < repeat; index++)
        {
            ValidateElement(target.Window, handle, token);
            if (element.GetCurrentPropertyValue(AutomationElement.IsEnabledProperty, true) is not true)
            { throw new WindowReferenceException(ErrorCode.UIA_ELEMENT_NOT_ENABLED); }
            var current = pattern.Current;
            if (horizontal ? !current.HorizontallyScrollable : !current.VerticallyScrollable)
            { throw new WindowReferenceException(ErrorCode.UIA_NOT_SCROLLABLE); }
            token.ThrowIfCancellationRequested();
            try { pattern.Scroll(horizontal ? amount : ScrollAmount.NoAmount, horizontal ? ScrollAmount.NoAmount : amount); }
            catch (ElementNotAvailableException) { throw; }
            catch (ElementNotEnabledException) { throw; }
            catch (InvalidOperationException) { throw new WindowReferenceException(ErrorCode.UIA_NOT_SCROLLABLE); }
        }
        ValidateElement(target.Window, handle, token);
        var final = pattern.Current;
        static double? Percent(double value) => double.IsFinite(value) && (value == ScrollPattern.NoScroll || value is >= 0 and <= 100) ? value : null;
        static double? Size(double value) => double.IsFinite(value) && value is >= 0 and <= 100 ? value : null;
        return ResponseJson.ToElement(new { windowId = target.Window.WindowId, elementId = target.ElementId,
            scrollCalls = repeat, horizontalScrollable = final.HorizontallyScrollable,
            verticalScrollable = final.VerticallyScrollable,
            horizontalPercent = Percent(final.HorizontalScrollPercent), verticalPercent = Percent(final.VerticalScrollPercent),
            horizontalViewSize = Size(final.HorizontalViewSize), verticalViewSize = Size(final.VerticalViewSize) });
    }

    private JsonElement ExpandCollapse(AutomationElement element, UiaElementHandle handle,
        UiOperationTarget target, JsonElement arguments, CancellationToken token)
    {
        if (element.GetCurrentPropertyValue(AutomationElement.IsEnabledProperty, true) is not true)
        { throw new WindowReferenceException(ErrorCode.UIA_ELEMENT_NOT_ENABLED); }
        if (!element.TryGetCurrentPattern(ExpandCollapsePattern.Pattern, out var raw) || raw is not ExpandCollapsePattern pattern)
        { throw new WindowReferenceException(ErrorCode.UIA_NOT_EXPANDABLE); }
        var desired = arguments.GetProperty("action").GetString() == "expand"
            ? ExpandCollapseState.Expanded : ExpandCollapseState.Collapsed;
        var observed = pattern.Current.ExpandCollapseState;
        if (!Enum.IsDefined(observed) || observed == ExpandCollapseState.LeafNode)
        { throw new WindowReferenceException(ErrorCode.UIA_NOT_EXPANDABLE); }
        var changed = observed != desired;
        if (changed)
        {
            ValidateElement(target.Window, handle, token);
            token.ThrowIfCancellationRequested();
            try
            {
                if (desired == ExpandCollapseState.Expanded) { pattern.Expand(); }
                else { pattern.Collapse(); }
            }
            catch (ElementNotAvailableException) { throw; }
            catch (ElementNotEnabledException) { throw; }
            catch (InvalidOperationException) { throw new WindowReferenceException(ErrorCode.UIA_NOT_EXPANDABLE); }
        }
        ValidateElement(target.Window, handle, token);
        if (pattern.Current.ExpandCollapseState != desired)
        { throw new WindowReferenceException(ErrorCode.UIA_EXPANSION_VERIFICATION_FAILED); }
        return ResponseJson.ToElement(new { windowId = target.Window.WindowId, elementId = target.ElementId,
            expanded = desired == ExpandCollapseState.Expanded, changed, stateVerified = true });
    }

    private JsonElement SetRangeValue(AutomationElement element, UiaElementHandle handle,
        UiOperationTarget target, JsonElement arguments, CancellationToken token)
    {
        if (element.GetCurrentPropertyValue(AutomationElement.IsEnabledProperty, true) is not true)
        { throw new WindowReferenceException(ErrorCode.UIA_ELEMENT_NOT_ENABLED); }
        if (!element.TryGetCurrentPattern(RangeValuePattern.Pattern, out var raw) || raw is not RangeValuePattern pattern)
        { throw new WindowReferenceException(ErrorCode.UIA_PATTERN_UNSUPPORTED); }
        var info = pattern.Current;
        if (info.IsReadOnly) { throw new WindowReferenceException(ErrorCode.UIA_VALUE_READ_ONLY); }
        var requested = arguments.GetProperty("value").GetDouble();
        var minimum = info.Minimum;
        var maximum = info.Maximum;
        if (!double.IsFinite(minimum) || !double.IsFinite(maximum) || minimum > maximum)
        { throw new WindowReferenceException(ErrorCode.UIA_VALUE_VERIFICATION_FAILED); }
        if (!double.IsFinite(requested) || requested < minimum || requested > maximum)
        { throw new WindowReferenceException(ErrorCode.INVALID_ARGUMENT); }
        double? smallChange = double.IsFinite(info.SmallChange) && info.SmallChange >= 0 ? info.SmallChange : null;
        double? largeChange = double.IsFinite(info.LargeChange) && info.LargeChange >= 0 ? info.LargeChange : null;
        var changed = info.Value != requested;
        if (changed)
        {
            ValidateElement(target.Window, handle, token);
            token.ThrowIfCancellationRequested();
            try { pattern.SetValue(requested); }
            catch (ElementNotAvailableException) { throw; }
            catch (ElementNotEnabledException) { throw; }
            catch (InvalidOperationException) { throw new WindowReferenceException(ErrorCode.UIA_VALUE_SET_FAILED); }
            catch (ArgumentException) { throw new WindowReferenceException(ErrorCode.INVALID_ARGUMENT); }
        }
        ValidateElement(target.Window, handle, token);
        var observed = pattern.Current.Value;
        // プロバイダーが丸めた値を要求値の成功として返さない。
        if (!double.IsFinite(observed) || observed != requested)
        { throw new WindowReferenceException(ErrorCode.UIA_VALUE_VERIFICATION_FAILED); }
        return ResponseJson.ToElement(new { windowId = target.Window.WindowId, elementId = target.ElementId,
            value = observed, changed, minimum, maximum, smallChange, largeChange, valueVerified = true });
    }

    private JsonElement SetToggle(AutomationElement element, UiaElementHandle handle,
        UiOperationTarget target, JsonElement arguments, CancellationToken token)
    {
        if (element.GetCurrentPropertyValue(AutomationElement.IsEnabledProperty, true) is not true)
        { throw new WindowReferenceException(ErrorCode.UIA_ELEMENT_NOT_ENABLED); }
        if (!element.TryGetCurrentPattern(TogglePattern.Pattern, out var raw) || raw is not TogglePattern pattern)
        { throw new WindowReferenceException(ErrorCode.UIA_PATTERN_UNSUPPORTED); }
        var desired = arguments.GetProperty("isOn").GetBoolean() ? ToggleState.On : ToggleState.Off;
        var observed = pattern.Current.ToggleState;
        var seen = new HashSet<ToggleState>();
        var count = 0;
        // 三状態の循環を最大二回で辿り、既に一致する状態を再反転しない。
        while (observed != desired && count < 2 && Enum.IsDefined(observed) && seen.Add(observed))
        {
            ValidateElement(target.Window, handle, token);
            if (element.GetCurrentPropertyValue(AutomationElement.IsEnabledProperty, true) is not true)
            { throw new WindowReferenceException(ErrorCode.UIA_ELEMENT_NOT_ENABLED); }
            token.ThrowIfCancellationRequested();
            try { pattern.Toggle(); }
            catch (ElementNotAvailableException) { throw; }
            catch (ElementNotEnabledException) { throw; }
            catch (InvalidOperationException) { throw new WindowReferenceException(ErrorCode.UIA_TOGGLE_FAILED); }
            count++;
            ValidateElement(target.Window, handle, token);
            observed = pattern.Current.ToggleState;
        }
        if (observed != desired) { throw new WindowReferenceException(ErrorCode.UIA_TOGGLE_VERIFICATION_FAILED); }
        return ResponseJson.ToElement(new { windowId = target.Window.WindowId, elementId = target.ElementId,
            isOn = observed == ToggleState.On, toggleCount = count, stateVerified = true });
    }
}
