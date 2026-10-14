using System.Text.Json;
using System.Windows.Automation;
using Kugutsu.Contracts;

namespace Kugutsu.App;

internal sealed partial class UiaDriver
{
    private JsonElement SetGridCellValue(AutomationElement grid, UiaElementHandle gridHandle,
        UiOperationTarget target, JsonElement arguments, CancellationToken token)
    {
        if (grid.GetCurrentPropertyValue(AutomationElement.IsEnabledProperty, true) is not true)
        { throw new WindowReferenceException(ErrorCode.UIA_ELEMENT_NOT_ENABLED); }
        var row = arguments.GetProperty("row").GetInt32();
        var column = arguments.GetProperty("column").GetInt32();
        var cell = ResolveGridCell(grid, gridHandle, target, row, column, token);
        ValidateGridCell(grid, gridHandle, cell, target, row, column, token);
        var outcome = WriteValue(cell.Element, cell, target, arguments.GetProperty("value").GetString()!, token, true);
        ValidateGridCell(grid, gridHandle, cell, target, row, column, token);
        return ResponseJson.ToElement(new { windowId = target.Window.WindowId, elementId = target.ElementId, row, column,
            valueSet = true, redacted = outcome.Redacted, verification = outcome.Redacted ? "unverified" : "verified",
            observedValue = outcome.ObservedValue });
    }

    private UiaElementHandle ResolveGridCell(AutomationElement gridElement, UiaElementHandle gridHandle,
        UiOperationTarget target, int row, int column, CancellationToken token)
    {
        ValidateElement(target.Window, gridHandle, token);
        if (!gridElement.TryGetCurrentPattern(GridPattern.Pattern, out var raw) || raw is not GridPattern grid)
        { throw new WindowReferenceException(ErrorCode.UIA_PATTERN_UNSUPPORTED); }
        var info = grid.Current;
        if (info.RowCount < 0 || info.ColumnCount < 0)
        { throw new WindowReferenceException(ErrorCode.UIA_GRID_CELL_UNAVAILABLE); }
        if (row >= info.RowCount || column >= info.ColumnCount)
        { throw new WindowReferenceException(ErrorCode.INVALID_ARGUMENT); }
        try
        {
            token.ThrowIfCancellationRequested();
            var cell = grid.GetItem(row, column) ?? throw new WindowReferenceException(ErrorCode.UIA_ELEMENT_NOT_FOUND);
            // Gridから返された仮想セルだけを実体化し、通常プロパティはその後に照合する。
            if (cell.TryGetCurrentPattern(VirtualizedItemPattern.Pattern, out var virtualRaw) && virtualRaw is VirtualizedItemPattern virtualized)
            {
                ValidateElement(target.Window, gridHandle, token);
                token.ThrowIfCancellationRequested();
                virtualized.Realize();
            }
            var handle = new UiaElementHandle(cell);
            ValidateGridCell(gridElement, gridHandle, handle, target, row, column, token);
            return handle;
        }
        catch (ElementNotAvailableException) { throw; }
        catch (ElementNotEnabledException) { throw; }
        catch (ArgumentException) { throw new WindowReferenceException(ErrorCode.INVALID_ARGUMENT); }
        catch (InvalidOperationException) { throw new WindowReferenceException(ErrorCode.UIA_GRID_CELL_UNAVAILABLE); }
    }

    private void ValidateGridCell(AutomationElement grid, UiaElementHandle gridHandle, UiaElementHandle cell,
        UiOperationTarget target, int row, int column, CancellationToken token)
    {
        ValidateElement(target.Window, gridHandle, token);
        ValidateElement(target.Window, cell, token);
        if (!cell.Element.TryGetCurrentPattern(GridItemPattern.Pattern, out var raw) || raw is not GridItemPattern item)
        { throw new WindowReferenceException(ErrorCode.UIA_GRID_CELL_UNAVAILABLE); }
        var info = item.Current;
        if (info.ContainingGrid is not { } owner || !Automation.Compare(owner, grid)
            || info.Row < 0 || info.Column < 0 || info.RowSpan < 1 || info.ColumnSpan < 1
            || row < info.Row || row >= (long)info.Row + info.RowSpan
            || column < info.Column || column >= (long)info.Column + info.ColumnSpan)
        { throw new WindowReferenceException(ErrorCode.UIA_GRID_CELL_UNAVAILABLE); }
    }

    private JsonElement ReadGridCell(AutomationElement grid, UiaElementHandle gridHandle,
        UiOperationTarget target, JsonElement arguments, CancellationToken token)
    {
        var row = arguments.GetProperty("row").GetInt32();
        var column = arguments.GetProperty("column").GetInt32();
        var cell = ResolveGridCell(grid, gridHandle, target, row, column, token);
        var name = cell.Element.GetCurrentPropertyValue(AutomationElement.NameProperty, true) as string;
        var nameTruncated = name is { Length: > 4096 };
        if (name is { Length: > 4096 }) { name = name[..(char.IsHighSurrogate(name[4095]) ? 4095 : 4096)]; }
        var type = cell.Element.GetCurrentPropertyValue(AutomationElement.ControlTypeProperty, true) as ControlType;
        var patterns = cell.Element.GetSupportedPatterns();
        var operations = GetSupportedOperations(cell.Element);
        ValidateGridCell(grid, gridHandle, cell, target, row, column, token);
        var reference = elementRegistry().Register(target.Window, "uia", cell, "s_" + Guid.NewGuid().ToString("N"), null, null, null, token);
        try
        {
            var result = ResponseJson.ToElement(new { windowId = target.Window.WindowId, gridElementId = target.ElementId,
                elementId = reference.Target.ElementId, driver = "uia", row, column, name, nameTruncated,
                controlType = type?.ProgrammaticName.Split('.').Last(), hasValue = patterns.Contains(ValuePattern.Pattern),
                supportedPatterns = patterns.Select(pattern => pattern.ProgrammaticName).Order(StringComparer.Ordinal).ToArray(),
                supportedOperations = operations });
            ValidateGridCell(grid, gridHandle, cell, target, row, column, token);
            token.ThrowIfCancellationRequested();
            return result;
        }
        catch { elementRegistry().Invalidate(reference.Target.ElementId); throw; }
    }
}
