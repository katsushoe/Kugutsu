using System.Runtime.InteropServices;
using System.Text.Json;
using Kugutsu.Contracts;

namespace Kugutsu.App;

internal sealed class KeyboardOperations(WindowStateOperations windows)
{
    private readonly record struct KeyStroke(ushort Code, bool Extended = false);
    private static readonly IReadOnlyDictionary<string, KeyStroke> Keys = CreateKeys();
    private static readonly IReadOnlyDictionary<string, KeyStroke> Modifiers = new Dictionary<string, KeyStroke>
    {
        ["Ctrl"] = new(0xa2), ["Alt"] = new(0xa4), ["Shift"] = new(0xa0), ["Win"] = new(0x5b, true)
    };
    internal static IEnumerable<string> KeyNames => Keys.Keys;
    internal static IEnumerable<string> ModifierNames => Modifiers.Keys;

    internal OperationDefinition PressKeyDefinition() => new("keyboard.press_key",
        ArgumentSchema.Object([
            new("windowId", ArgumentSchema.String(34, 34)),
            new("key", ArgumentSchema.String(16, 1, Keys.Keys)),
            new("modifiers", ArgumentSchema.Array(ArgumentSchema.String(5, 3, Modifiers.Keys), 4), false)]),
        async (args, token) => await PressAsync(args.GetProperty("windowId").GetString()!,
            args.GetProperty("key").GetString()!, ReadModifiers(args), token),
        args => WindowReferenceRegistry.IsValidId(args.GetProperty("windowId").GetString())
            && ReadModifiers(args).Distinct(StringComparer.Ordinal).Count() == ReadModifiers(args).Length);

    internal OperationDefinition TypeTextDefinition() => new("keyboard.type_text",
        ArgumentSchema.Object([
            new("windowId", ArgumentSchema.String(34, 34)),
            new("text", ArgumentSchema.String(65536, 1))]),
        async (args, token) => await TypeTextAsync(args.GetProperty("windowId").GetString()!,
            args.GetProperty("text").GetString()!, token),
        args => WindowReferenceRegistry.IsValidId(args.GetProperty("windowId").GetString())
            && ValidText(args.GetProperty("text").GetString()!));

    internal static bool ValidText(string text)
    {
        if (text.Length is < 1 or > 65536 || text.Contains('\0')) { return false; }
        for (var index = 0; index < text.Length; index++)
        {
            if (char.IsHighSurrogate(text[index]))
            {
                if (++index >= text.Length || !char.IsLowSurrogate(text[index])) { return false; }
            }
            else if (char.IsLowSurrogate(text[index])) { return false; }
        }
        return true;
    }

    internal async ValueTask<JsonElement> TypeTextAsync(string windowId, string text, CancellationToken token,
        Func<CancellationToken, ValueTask>? verifyFocus = null)
    {
        if (!ValidText(text)) { throw new WindowReferenceException(ErrorCode.INVALID_ARGUMENT); }
        await windows.ActivateAsync(windowId, token);
        VerifyReady(windowId, token);
        for (var offset = 0; offset < text.Length;)
        {
            token.ThrowIfCancellationRequested();
            windows.VerifyInputTarget(windowId, token);
            var count = Math.Min(256, text.Length - offset);
            if (char.IsHighSurrogate(text[offset + count - 1])) { count--; }
            var inputs = new Input[count * 2];
            for (var index = 0; index < count; index++)
            {
                inputs[index * 2] = UnicodeInput(text[offset + index], false);
                inputs[index * 2 + 1] = UnicodeInput(text[offset + index], true);
            }
            token.ThrowIfCancellationRequested();
            if (verifyFocus is not null) { await verifyFocus(token); }
            VerifyReady(windowId, token);
            windows.VerifyInputTarget(windowId, token);
            token.ThrowIfCancellationRequested();
            var sent = SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Input>());
            if (sent != inputs.Length)
            {
                if (sent % 2 == 1 && SendInput(1,
                    [UnicodeInput(inputs[(int)sent - 1].Data.Keyboard.Scan, true)], Marshal.SizeOf<Input>()) != 1)
                { throw new WindowReferenceException(ErrorCode.INPUT_RELEASE_FAILED); }
                throw new WindowReferenceException(ErrorCode.INPUT_SEND_FAILED);
            }
            offset += count;
        }
        return JsonSerializer.SerializeToElement(new { windowId, utf16Length = text.Length, inputSent = true });
    }

    internal static string[] ReadModifiers(JsonElement args) => args.TryGetProperty("modifiers", out var value)
        ? value.EnumerateArray().Select(item => item.GetString()!).ToArray() : [];

    internal async ValueTask<JsonElement> PressAsync(string windowId, string key, string[] modifiers,
        CancellationToken token, Func<CancellationToken, ValueTask>? verifyFocus = null)
    {
        if (!Keys.TryGetValue(key, out var main) || modifiers.Length > 4
            || modifiers.Distinct(StringComparer.Ordinal).Count() != modifiers.Length
            || modifiers.Any(item => !Modifiers.ContainsKey(item)))
        { throw new WindowReferenceException(ErrorCode.INVALID_ARGUMENT); }
        await windows.ActivateAsync(windowId, token);
        var strokes = modifiers.Select(item => Modifiers[item]).Append(main).ToArray();
        var inputs = strokes.Select(item => KeyInput(item, false))
            .Concat(strokes.Reverse().Select(item => KeyInput(item, true))).ToArray();
        VerifyReady(windowId, token);
        if (verifyFocus is not null) { await verifyFocus(token); }
        VerifyReady(windowId, token);
        windows.VerifyInputTarget(windowId, token);
        token.ThrowIfCancellationRequested();
        var sent = SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Input>());
        if (sent != inputs.Length)
        {
            var held = new List<KeyStroke>();
            for (var index = 0; index < Math.Min(sent, (uint)inputs.Length); index++)
            {
                var keyboard = inputs[index].Data.Keyboard;
                var stroke = new KeyStroke(keyboard.Code, (keyboard.Flags & 1) != 0);
                if ((keyboard.Flags & 2) == 0) { held.Add(stroke); }
                else { held.Remove(stroke); }
            }
            var releases = held.AsEnumerable().Reverse().Select(item => KeyInput(item, true)).ToArray();
            // 部分送出で自分が押したキーだけを解放し、主キーを再送しない。
            if (releases.Length != 0 && SendInput((uint)releases.Length, releases,
                Marshal.SizeOf<Input>()) != releases.Length)
            { throw new WindowReferenceException(ErrorCode.INPUT_RELEASE_FAILED); }
            throw new WindowReferenceException(ErrorCode.INPUT_SEND_FAILED);
        }
        return JsonSerializer.SerializeToElement(new { windowId, key, modifiers, inputSent = true });
    }

    private void VerifyReady(string windowId, CancellationToken token)
    {
        windows.VerifyInputTarget(windowId, token);
        // 利用者が押しているキーを解除したり、入力へ混ぜたりしない。
        for (var code = 1; code <= 254; code++)
        {
            if ((GetAsyncKeyState(code) & 0x8000) != 0)
            { throw new WindowReferenceException(ErrorCode.INPUT_STATE_UNSAFE); }
        }
        windows.VerifyInputTarget(windowId, token);
    }

    private static Input UnicodeInput(ushort character, bool release) => new()
    {
        Type = 1, Data = new() { Keyboard = new() { Scan = character, Flags = release ? 6u : 4u } }
    };

    private static Dictionary<string, KeyStroke> CreateKeys()
    {
        var keys = new Dictionary<string, KeyStroke>(StringComparer.Ordinal)
        {
            ["Enter"] = new(0x0d), ["Tab"] = new(9), ["Escape"] = new(0x1b),
            ["Backspace"] = new(8), ["Space"] = new(0x20),
            ["Insert"] = new(0x2d, true), ["Delete"] = new(0x2e, true),
            ["Home"] = new(0x24, true), ["End"] = new(0x23, true),
            ["PageUp"] = new(0x21, true), ["PageDown"] = new(0x22, true),
            ["Left"] = new(0x25, true), ["Up"] = new(0x26, true),
            ["Right"] = new(0x27, true), ["Down"] = new(0x28, true)
        };
        for (var code = 'A'; code <= 'Z'; code++) { keys[code.ToString()] = new(code); }
        for (var code = '0'; code <= '9'; code++) { keys[code.ToString()] = new(code); }
        for (var index = 1; index <= 24; index++) { keys[$"F{index}"] = new((ushort)(0x6f + index)); }
        return keys;
    }

    private static Input KeyInput(KeyStroke key, bool release) => new()
    {
        Type = 1, Data = new() { Keyboard = new() { Code = key.Code,
            Flags = (key.Extended ? 1u : 0u) | (release ? 2u : 0u) } }
    };

    [StructLayout(LayoutKind.Sequential)]
    private struct Input { public uint Type; public InputUnion Data; }
    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public NativeKeyboard Keyboard;
        [FieldOffset(0)] public NativeMouse Mouse;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct NativeKeyboard { public ushort Code, Scan; public uint Flags, Time; public nuint Extra; }
    [StructLayout(LayoutKind.Sequential)]
    private struct NativeMouse { public int X, Y; public uint Data, Flags, Time; public nuint Extra; }
    [DllImport("user32.dll", ExactSpelling = true, SetLastError = true)]
    private static extern uint SendInput(uint count, Input[] inputs, int size);
    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern short GetAsyncKeyState(int code);
}
