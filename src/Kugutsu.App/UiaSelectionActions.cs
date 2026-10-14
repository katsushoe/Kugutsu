using System.Text.Json;
using System.Windows.Automation;
using Kugutsu.Contracts;

namespace Kugutsu.App;

internal sealed partial class UiaDriver
{
    private JsonElement SelectItems(AutomationElement element, UiaElementHandle container,
        UiOperationTarget target, JsonElement arguments, CancellationToken token)
    {
        if (element.GetCurrentPropertyValue(AutomationElement.IsEnabledProperty, true) is not true)
        { throw new WindowReferenceException(ErrorCode.UIA_ELEMENT_NOT_ENABLED); }
        if (!element.TryGetCurrentPattern(SelectionPattern.Pattern, out var raw) || raw is not SelectionPattern selection
            || !selection.Current.CanSelectMultiple)
        { throw new WindowReferenceException(ErrorCode.UIA_NOT_SELECTABLE); }
        var requested = new List<UiaElementHandle>();
        foreach (var id in arguments.GetProperty("items").EnumerateArray())
        {
            var item = elementRegistry().Resolve(id.GetString()!, target.Window.WindowId, "uia", token);
            if (item.NativeTarget is not UiaElementHandle handle)
            { throw new WindowReferenceException(ErrorCode.UI_ELEMENT_STALE); }
            if (requested.Any(existing => Automation.Compare(existing.Element, handle.Element)))
            { throw new WindowReferenceException(ErrorCode.INVALID_ARGUMENT); }
            requested.Add(handle);
        }
        var original = ReadSelected();
        foreach (var item in requested) { ValidateItem(item); }
        var mode = arguments.GetProperty("mode").GetString()!;
        var desired = mode switch
        {
            "replace" => requested,
            "add" => original.Concat(requested.Where(item => !Contains(original, item))).ToList(),
            _ => original.Where(item => !Contains(requested, item)).ToList()
        };
        if (desired.Count > 1000) { throw new WindowReferenceException(ErrorCode.UIA_SEARCH_LIMIT_EXCEEDED); }
        if (selection.Current.IsSelectionRequired && desired.Count == 0)
        { throw new WindowReferenceException(ErrorCode.UIA_NOT_SELECTABLE); }
        var additions = desired.Where(item => !Contains(original, item)).ToArray();
        var removals = original.Where(item => !Contains(desired, item)).ToArray();
        // 全項目を操作前に確認し、必須選択を一時的にも空にしないよう追加を先に行う。
        foreach (var item in additions.Concat(removals)) { ValidateItem(item); }
        var calls = 0;
        foreach (var item in additions) { Change(item, true); }
        foreach (var item in removals) { Change(item, false); }
        var observed = ReadSelected();
        if (observed.Count != desired.Count || desired.Any(item => !Contains(observed, item)))
        { throw new WindowReferenceException(ErrorCode.UIA_SELECTION_VERIFICATION_FAILED); }
        return ResponseJson.ToElement(new { windowId = target.Window.WindowId, elementId = target.ElementId,
            mode, selectedCount = observed.Count, selectionCalls = calls, selectionVerified = true });

        static bool Contains(IEnumerable<UiaElementHandle> list, UiaElementHandle item)
            => list.Any(existing => Automation.Compare(existing.Element, item.Element));

        SelectionItemPattern ValidateItem(UiaElementHandle item)
        {
            ValidateElement(target.Window, container, token);
            ValidateElement(target.Window, item, token);
            if (item.Element.GetCurrentPropertyValue(AutomationElement.IsEnabledProperty, true) is not true)
            { throw new WindowReferenceException(ErrorCode.UIA_ELEMENT_NOT_ENABLED); }
            if (!item.Element.TryGetCurrentPattern(SelectionItemPattern.Pattern, out var value) || value is not SelectionItemPattern pattern
                || pattern.Current.SelectionContainer is not { } owner || !Automation.Compare(owner, element))
            { throw new WindowReferenceException(ErrorCode.UIA_NOT_SELECTABLE); }
            return pattern;
        }

        List<UiaElementHandle> ReadSelected()
        {
            ValidateElement(target.Window, container, token);
            if (!selection.Current.CanSelectMultiple) { throw new WindowReferenceException(ErrorCode.UIA_NOT_SELECTABLE); }
            var selected = selection.Current.GetSelection();
            if (selected.Length > 1000) { throw new WindowReferenceException(ErrorCode.UIA_SEARCH_LIMIT_EXCEEDED); }
            var result = new List<UiaElementHandle>();
            foreach (var item in selected)
            {
                var current = new UiaElementHandle(item);
                ValidateItem(current);
                if (Contains(result, current)) { throw new WindowReferenceException(ErrorCode.UIA_SELECTION_VERIFICATION_FAILED); }
                result.Add(current);
            }
            return result;
        }

        void Change(UiaElementHandle item, bool add)
        {
            var pattern = ValidateItem(item);
            if (!selection.Current.CanSelectMultiple) { throw new WindowReferenceException(ErrorCode.UIA_NOT_SELECTABLE); }
            if (pattern.Current.IsSelected == add) { return; }
            token.ThrowIfCancellationRequested();
            try
            {
                if (add) { pattern.AddToSelection(); } else { pattern.RemoveFromSelection(); }
            }
            catch (ElementNotAvailableException) { throw; }
            catch (ElementNotEnabledException) { throw; }
            catch (InvalidOperationException) { throw new WindowReferenceException(ErrorCode.UIA_NOT_SELECTABLE); }
            calls++;
            ValidateItem(item);
            if (pattern.Current.IsSelected != add) { throw new WindowReferenceException(ErrorCode.UIA_SELECTION_VERIFICATION_FAILED); }
        }
    }
}
