using System.Runtime.InteropServices;
using System.Text.Json;
using Kugutsu.Contracts;
using Kugutsu.Hosting;

namespace Kugutsu.App;

internal sealed class WindowStateSignalProvider(WindowReferenceRegistry windows, KugutsuOptions settings)
    : ISignalConditionProvider
{
    public string Kind => "window.state";
    public ArgumentSchema Schema { get; } = ArgumentSchema.Object([
        new("kind", ArgumentSchema.String(12, 12, ["window.state"])),
        new("windowId", ArgumentSchema.String(34, 34)),
        new("property", ArgumentSchema.String(9, 1, ["exists", "active", "minimized", "maximized"])),
        new("equals", ArgumentSchema.Boolean)]);

    public ValueTask ValidateAsync(JsonElement condition, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        windows.Resolve(condition.GetProperty("windowId").GetString()!);
        return ValueTask.CompletedTask;
    }

    public ValueTask<IAsyncDisposable> StartAsync(JsonElement condition, Action<SignalObservation> observe,
        CancellationToken startup, CancellationToken lifetime)
    {
        var reference = windows.Resolve(condition.GetProperty("windowId").GetString()!);
        var property = condition.GetProperty("property").GetString()!;
        var expected = condition.GetProperty("equals").GetBoolean();
        SignalObservation ReadState(CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (windows.HasWindowEnded(reference))
            {
                return property == "exists" && !expected
                    ? new(true, ResponseJson.ToElement(new { windowId = reference.WindowId, property, value = false }))
                    : new(false, null, ErrorCode.SIGNAL_TARGET_INVALIDATED);
            }
            var handle = windows.Resolve(reference.WindowId).Identity.Handle;
            var value = property switch
            {
                "exists" => true,
                "active" => GetForegroundWindow() == handle,
                "minimized" => IsIconic(handle),
                "maximized" => IsZoomed(handle),
                _ => throw new WindowReferenceException(ErrorCode.INVALID_ARGUMENT)
            };
            // A recycled or closed HWND cannot contribute an observation.
            if (windows.HasWindowEnded(reference))
            {
                return property == "exists" && !expected
                    ? new(true, ResponseJson.ToElement(new { windowId = reference.WindowId, property, value = false }))
                    : new(false, null, ErrorCode.SIGNAL_TARGET_INVALIDATED);
            }
            windows.Resolve(reference.WindowId);
            return new(value == expected, ResponseJson.ToElement(new { windowId = reference.WindowId, property, value }));
        }
        SignalObservation Read(CancellationToken token)
        {
            try { return ReadState(token); }
            catch (WindowReferenceException error) when (error.Code == ErrorCode.WINDOW_NOT_FOUND)
            {
                if (property == "exists" && !expected && windows.HasWindowEnded(reference))
                { return new(true, ResponseJson.ToElement(new { windowId = reference.WindowId, property, value = false })); }
                return new(false, null, ErrorCode.SIGNAL_TARGET_INVALIDATED);
            }
        }
        return ValueTask.FromResult<IAsyncDisposable>(new PollingSignalMonitor(
            settings.SignalPollIntervalMilliseconds, Read, observe, startup, lifetime));
    }

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern nint GetForegroundWindow();
    [DllImport("user32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(nint window);
    [DllImport("user32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsZoomed(nint window);
}
