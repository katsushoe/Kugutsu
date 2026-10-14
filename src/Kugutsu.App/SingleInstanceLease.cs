using System.Diagnostics;
using System.Security.Principal;

namespace Kugutsu.App;

/// <summary>Holds a session-scoped named kernel object for this process lifetime.</summary>
internal sealed class SingleInstanceLease : IDisposable
{
    private readonly EventWaitHandle _presenceHandle;

    private SingleInstanceLease(EventWaitHandle presenceHandle) => _presenceHandle = presenceHandle;

    internal static SingleInstanceLease? TryAcquire()
    {
        var userSid = WindowsIdentity.GetCurrent().User?.Value
            ?? throw new InvalidOperationException("The current Windows user has no SID.");
        using var process = Process.GetCurrentProcess();
        var name = $"Local\\Kugutsu-{userSid}-{process.SessionId}";
        EventWaitHandle presenceHandle;
        bool createdNew;
        try
        {
            presenceHandle = new EventWaitHandle(false, EventResetMode.ManualReset,
                name, out createdNew);
        }
        catch (WaitHandleCannotBeOpenedException)
        { return null; }
        if (!createdNew)
        {
            presenceHandle.Dispose();
            return null;
        }
        return new SingleInstanceLease(presenceHandle);
    }

    public void Dispose() => _presenceHandle.Dispose();
}
