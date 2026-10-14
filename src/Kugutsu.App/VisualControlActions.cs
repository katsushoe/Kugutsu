using System.Text.Json;
using Kugutsu.Contracts;

namespace Kugutsu.App;

internal sealed class VisualControlActions(VisualReferenceValidator references, MouseOperations mouse,
    VisualAnalysisPipeline pipeline, VisualFocusVerifier focus, KeyboardOperations keyboard, VisualValueReader values)
{
    internal async ValueTask<JsonElement?> ExecuteAsync(UiOperationTarget target, string operation, JsonElement arguments,
        CancellationToken token)
    {
        if (operation == "control.get_value") { return await values.ReadAsync(target, arguments, token); }
        if (operation == "control.set_value") { return await SetValueAsync(target, arguments, token); }
        if (operation == "control.send_keys") { return await SendKeysAsync(target, arguments, token); }
        if (operation != "control.invoke") { throw new WindowReferenceException(ErrorCode.UI_OPERATION_UNSUPPORTED); }
        if (!arguments.TryGetProperty("method", out var method) || method.GetString() != "coordinate_click")
        { throw new WindowReferenceException(ErrorCode.INVALID_ARGUMENT); }
        var initial = await references.ValidateAsync(target, token);
        if (initial.Handle.Meaning.Source.Shape.Kind != "button_candidate" || initial.Handle.Meaning.Rule is null)
        { throw new WindowReferenceException(ErrorCode.UI_TARGET_UNVERIFIED); }
        var bounds = initial.Handle.Meaning.Source.Shape.Bounds;
        try
        {
            await mouse.ClickAsync(target.Window.WindowId, new(bounds.X + bounds.Width / 2, bounds.Y + bounds.Height / 2),
                "left", 1, token, async cancellation => { await references.ValidateAsync(target, cancellation); });
            return JsonSerializer.SerializeToElement(new { windowId = target.Window.WindowId, elementId = target.ElementId,
                driver = target.Driver, method = "coordinate_click", inputSent = true, applicationResultVerified = false });
        }
        finally { pipeline.Discard(initial.Handle.SnapshotId); }
    }

    private async ValueTask<JsonElement?> SendKeysAsync(UiOperationTarget target, JsonElement arguments, CancellationToken token)
    {
        if (!arguments.TryGetProperty("method", out var method) || method.GetString() != "coordinate_keyboard")
        { throw new WindowReferenceException(ErrorCode.INVALID_ARGUMENT); }
        var initial = await references.ValidateAsync(target, token);
        if (initial.Handle.Meaning.Source.Shape.Kind != "input_candidate"
            || initial.Handle.Meaning.Rule is not { AllowTextInput: true, FocusIndicator: not null })
        { throw new WindowReferenceException(ErrorCode.UI_TARGET_UNVERIFIED); }
        var bounds = initial.Handle.Meaning.Source.Shape.Bounds;
        try
        {
            await mouse.ClickAsync(target.Window.WindowId, new(bounds.X + bounds.Width / 2, bounds.Y + bounds.Height / 2),
                "left", 1, token, async cancellation => { await references.ValidateAsync(target, cancellation); });
            var count = 0;
            foreach (var stroke in arguments.GetProperty("keySequence").EnumerateArray())
            {
                await keyboard.PressAsync(target.Window.WindowId, stroke.GetProperty("key").GetString()!,
                    KeyboardOperations.ReadModifiers(stroke), token,
                    async cancellation => { await focus.VerifyAsync(target, cancellation); });
                count++;
            }
            return JsonSerializer.SerializeToElement(new { windowId = target.Window.WindowId, elementId = target.ElementId,
                driver = target.Driver, method = "coordinate_keyboard", inputSent = true, keyCount = count,
                applicationResultVerified = false });
        }
        finally { pipeline.Discard(initial.Handle.SnapshotId); }
    }

    private async ValueTask<JsonElement?> SetValueAsync(UiOperationTarget target, JsonElement arguments, CancellationToken token)
    {
        if (!arguments.TryGetProperty("method", out var method) || method.GetString() != "coordinate_keyboard")
        { throw new WindowReferenceException(ErrorCode.INVALID_ARGUMENT); }
        var value = arguments.GetProperty("value").GetString()!;
        if (value.Any(char.IsControl) || value.Length != 0 && !KeyboardOperations.ValidText(value))
        { throw new WindowReferenceException(ErrorCode.INVALID_ARGUMENT); }
        var initial = await references.ValidateAsync(target, token);
        if (initial.Handle.Meaning.Source.Shape.Kind != "input_candidate"
            || initial.Handle.Meaning.Rule is not { AllowTextInput: true, FocusIndicator: not null })
        { throw new WindowReferenceException(ErrorCode.UI_TARGET_UNVERIFIED); }
        var bounds = initial.Handle.Meaning.Source.Shape.Bounds;
        try
        {
            await mouse.ClickAsync(target.Window.WindowId, new(bounds.X + bounds.Width / 2, bounds.Y + bounds.Height / 2),
                "left", 1, token, async cancellation => { await references.ValidateAsync(target, cancellation); });
            async ValueTask VerifyFocus(CancellationToken cancellation) { await focus.VerifyAsync(target, cancellation); }
            await VerifyFocus(token);
            await keyboard.PressAsync(target.Window.WindowId, "A", ["Ctrl"], token, VerifyFocus);
            await keyboard.PressAsync(target.Window.WindowId, "Delete", [], token, VerifyFocus);
            if (value.Length != 0) { await keyboard.TypeTextAsync(target.Window.WindowId, value, token, VerifyFocus); }
            return JsonSerializer.SerializeToElement(new { windowId = target.Window.WindowId, elementId = target.ElementId,
                driver = target.Driver, method = "coordinate_keyboard", inputSent = true, utf16Length = value.Length,
                applicationResultVerified = false });
        }
        finally { pipeline.Discard(initial.Handle.SnapshotId); }
    }
}
