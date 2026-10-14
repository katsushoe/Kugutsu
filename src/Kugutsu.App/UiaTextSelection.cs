using System.Text.Json;
using System.Windows.Automation;
using System.Windows.Automation.Text;
using Kugutsu.Contracts;

namespace Kugutsu.App;

internal sealed partial class UiaDriver
{
    private JsonElement SetTextSelection(AutomationElement element, UiaElementHandle handle,
        UiOperationTarget target, JsonElement arguments, CancellationToken token)
    {
        if (element.GetCurrentPropertyValue(AutomationElement.IsEnabledProperty, true) is not true)
        { throw new WindowReferenceException(ErrorCode.UIA_ELEMENT_NOT_ENABLED); }
        if (element.GetCurrentPropertyValue(AutomationElement.IsPasswordProperty, true) is not false
            || !element.TryGetCurrentPattern(TextPattern.Pattern, out var raw) || raw is not TextPattern pattern
            || pattern.SupportedTextSelection == SupportedTextSelection.None)
        { throw new WindowReferenceException(ErrorCode.UIA_TEXT_RANGE_UNAVAILABLE); }
        try
        {
            var document = pattern.DocumentRange;
            var text = document.GetText(65537);
            if (text.Length > 65536) { throw new WindowReferenceException(ErrorCode.UIA_TEXT_RANGE_UNAVAILABLE); }
            var start = arguments.GetProperty("start").GetInt32();
            var length = arguments.GetProperty("length").GetInt32();
            var end = checked(start + length);
            if (end > text.Length || SplitsPair(start) || SplitsPair(end))
            { throw new WindowReferenceException(ErrorCode.INVALID_ARGUMENT); }
            var first = Locate(start);
            var last = Locate(end);
            var requested = document.Clone();
            requested.MoveEndpointByRange(TextPatternRangeEndpoint.Start, first, TextPatternRangeEndpoint.End);
            requested.MoveEndpointByRange(TextPatternRangeEndpoint.End, last, TextPatternRangeEndpoint.End);
            ValidateElement(target.Window, handle, token);
            VerifyDocument();
            if (requested.GetText(65537) != text.Substring(start, length))
            { throw new WindowReferenceException(ErrorCode.UIA_TEXT_RANGE_UNAVAILABLE); }
            token.ThrowIfCancellationRequested();
            requested.Select();
            ValidateElement(target.Window, handle, token);
            VerifyDocument();
            var observed = pattern.GetSelection();
            if (observed.Length != 1
                || observed[0].CompareEndpoints(TextPatternRangeEndpoint.Start, requested, TextPatternRangeEndpoint.Start) != 0
                || observed[0].CompareEndpoints(TextPatternRangeEndpoint.End, requested, TextPatternRangeEndpoint.End) != 0)
            { throw new WindowReferenceException(ErrorCode.UIA_TEXT_SELECTION_VERIFICATION_FAILED); }
            return ResponseJson.ToElement(new { windowId = target.Window.WindowId, elementId = target.ElementId,
                start, length, selectionVerified = true });

            bool SplitsPair(int offset) => offset > 0 && offset < text.Length
                && char.IsHighSurrogate(text[offset - 1]) && char.IsLowSurrogate(text[offset]);

            void VerifyDocument()
            {
                token.ThrowIfCancellationRequested();
                if (element.GetCurrentPropertyValue(AutomationElement.IsPasswordProperty, true) is not false
                    || document.GetText(65537) != text)
                { throw new WindowReferenceException(ErrorCode.UIA_TEXT_RANGE_UNAVAILABLE); }
            }

            TextPatternRange Locate(int offset)
            {
                // UIAのCharacterとUTF-16単位を同一視せず、接頭辞の実長で境界を照合する。
                var low = 0;
                var high = offset;
                while (low <= high)
                {
                    ValidateElement(target.Window, handle, token);
                    var units = low + (high - low) / 2;
                    var prefix = document.Clone();
                    prefix.MoveEndpointByRange(TextPatternRangeEndpoint.End, document, TextPatternRangeEndpoint.Start);
                    if (units > 0) { prefix.MoveEndpointByUnit(TextPatternRangeEndpoint.End, TextUnit.Character, units); }
                    var prefixText = prefix.GetText(65537);
                    if (!text.StartsWith(prefixText, StringComparison.Ordinal))
                    { throw new WindowReferenceException(ErrorCode.UIA_TEXT_RANGE_UNAVAILABLE); }
                    if (prefixText.Length == offset) { return prefix; }
                    if (prefixText.Length < offset) { low = units + 1; } else { high = units - 1; }
                }
                throw new WindowReferenceException(ErrorCode.UIA_TEXT_RANGE_UNAVAILABLE);
            }
        }
        catch (ElementNotAvailableException) { throw; }
        catch (ElementNotEnabledException) { throw; }
        catch (InvalidOperationException) { throw new WindowReferenceException(ErrorCode.UIA_TEXT_RANGE_UNAVAILABLE); }
        catch (ArgumentException) { throw new WindowReferenceException(ErrorCode.UIA_TEXT_RANGE_UNAVAILABLE); }
    }
}
