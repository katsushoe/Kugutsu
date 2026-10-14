using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Kugutsu.Contracts;
using Microsoft.Win32.SafeHandles;

namespace Kugutsu.App;

/// <summary>管理ルートと同じ世代の子孫を照合し、一意な設定実行ファイルだけを登録する。</summary>
internal sealed class ManagedProcessTree(ApplicationReferenceRegistry applications)
{
    private sealed record ProcessEntry(uint Id, uint ParentId, string Name);

    internal async ValueTask<ApplicationReference> FindAsync(ApplicationReference root, RegisteredLaunchTarget target, CancellationToken token,
        Func<Process, bool>? candidateFilter = null)
    {
        using var reservation = applications.Reserve(token);
        while (true)
        {
            applications.Resolve(root.ApplicationId, token);
            var snapshot = ReadSnapshot(token);
            var matches = new List<Process>();
            try
            {
                foreach (var entry in snapshot.Values.Where(entry => entry.Id != root.ProcessId
                    && entry.Name.Equals(Path.GetFileName(target.ExecutablePath), StringComparison.OrdinalIgnoreCase)
                    && HasRoot((uint)root.ProcessId, entry.Id, snapshot)))
                {
                    token.ThrowIfCancellationRequested();
                    Process? current = null;
                    try
                    {
                        if (entry.Id > int.MaxValue) { continue; }
                        current = Process.GetProcessById((int)entry.Id);
                        var started = applications.Inspect(current, target);
                        if (!IsDescendant(root, entry.Id, started, snapshot, token)) { continue; }
                        if (candidateFilter is not null && !candidateFilter(current)) { continue; }
                        matches.Add(current);
                        current = null;
                    }
                    catch (ArgumentException) { continue; }
                    catch (WindowReferenceException exception) when (exception.Code == ErrorCode.APP_INSTANCE_UNRESOLVED) { continue; }
                    finally { current?.Dispose(); }
                }
                if (matches.Count > 1) { throw new WindowReferenceException(ErrorCode.SESSION_TARGET_UNRESOLVED); }
                if (matches.Count == 1)
                {
                    applications.Resolve(root.ApplicationId, token);
                    var reference = reservation.Register(matches[0], target, token);
                    matches.Clear(); // 成功したハンドルはApplicationReferenceRegistryが所有する。
                    return reference;
                }
            }
            catch (Win32Exception exception)
            { throw new WindowReferenceException(exception.NativeErrorCode == 5 ? ErrorCode.ACCESS_DENIED : ErrorCode.SESSION_TARGET_UNRESOLVED); }
            finally { foreach (var process in matches) { process.Dispose(); } }
            await Task.Delay(100, token);
        }
    }

    private static bool HasRoot(uint root, uint id, Dictionary<uint, ProcessEntry> snapshot)
    {
        var seen = new HashSet<uint>();
        for (var depth = 0; depth < 64 && seen.Add(id); depth++)
        {
            if (!snapshot.TryGetValue(id, out var entry)) { return false; }
            if (entry.ParentId == root) { return true; }
            id = entry.ParentId;
        }
        return false;
    }

    internal void VerifyRelationship(ApplicationReference root, ApplicationReference child, CancellationToken token)
    {
        applications.Resolve(root.ApplicationId, token);
        applications.Resolve(child.ApplicationId, token);
        if (!IsDescendant(root, (uint)child.ProcessId, child.ProcessStartedAt, ReadSnapshot(token), token))
        { throw new WindowReferenceException(ErrorCode.SESSION_TARGET_UNRESOLVED); }
        applications.Resolve(root.ApplicationId, token);
        applications.Resolve(child.ApplicationId, token);
    }

    internal void VerifyConsoleMembers(ApplicationReference shell, IReadOnlyList<uint> ids, int probeId, CancellationToken token)
    {
        applications.Resolve(shell.ApplicationId, token);
        var snapshot = ReadSnapshot(token);
        foreach (var id in ids)
        {
            token.ThrowIfCancellationRequested();
            if (id == shell.ProcessId || id == probeId) { continue; }
            if (id is 0 or > int.MaxValue) { throw new WindowReferenceException(ErrorCode.SESSION_TARGET_UNRESOLVED); }
            try
            {
                using var process = Process.GetProcessById((int)id);
                if (process.HasExited || !IsDescendant(shell, id, process.StartTime.ToFileTimeUtc(), snapshot, token))
                { throw new WindowReferenceException(ErrorCode.SESSION_TARGET_UNRESOLVED); }
            }
            catch (ArgumentException) { throw new WindowReferenceException(ErrorCode.SESSION_TARGET_UNRESOLVED); }
            catch (InvalidOperationException) { throw new WindowReferenceException(ErrorCode.SESSION_TARGET_UNRESOLVED); }
            catch (Win32Exception exception)
            { throw new WindowReferenceException(exception.NativeErrorCode == 5 ? ErrorCode.ACCESS_DENIED : ErrorCode.SESSION_TARGET_UNRESOLVED); }
        }
        applications.Resolve(shell.ApplicationId, token);
    }

    private static bool IsDescendant(ApplicationReference root, uint id, long started,
        Dictionary<uint, ProcessEntry> snapshot, CancellationToken token)
    {
        var seen = new HashSet<uint>();
        for (var depth = 0; depth < 64 && seen.Add(id); depth++)
        {
            token.ThrowIfCancellationRequested();
            if (!snapshot.TryGetValue(id, out var entry) || started < root.ProcessStartedAt) { return false; }
            if (entry.ParentId == root.ProcessId) { return true; }
            if (entry.ParentId is 0 or > int.MaxValue) { return false; }
            try
            {
                using var parent = Process.GetProcessById((int)entry.ParentId);
                var parentStarted = parent.StartTime.ToFileTimeUtc();
                if (parent.HasExited || parentStarted > started) { return false; }
                started = parentStarted;
                id = entry.ParentId;
            }
            catch (ArgumentException) { return false; }
            catch (InvalidOperationException) { return false; }
            catch (Win32Exception exception)
            { throw new WindowReferenceException(exception.NativeErrorCode == 5 ? ErrorCode.ACCESS_DENIED : ErrorCode.SESSION_TARGET_UNRESOLVED); }
        }
        return false;
    }

    private static Dictionary<uint, ProcessEntry> ReadSnapshot(CancellationToken token)
    {
        using var snapshot = CreateToolhelp32Snapshot(0x00000002, 0); // TH32CS_SNAPPROCESS
        if (snapshot.IsInvalid) { throw SnapshotFailure(); }
        var native = new NativeProcessEntry { Size = (uint)Marshal.SizeOf<NativeProcessEntry>(), Name = string.Empty };
        var entries = new Dictionary<uint, ProcessEntry>();
        var found = Process32FirstW(snapshot, ref native);
        while (found)
        {
            token.ThrowIfCancellationRequested();
            if (entries.Count >= 16384 || !entries.TryAdd(native.Id, new(native.Id, native.ParentId, native.Name)))
            { throw new WindowReferenceException(ErrorCode.SESSION_TARGET_UNRESOLVED); }
            found = Process32NextW(snapshot, ref native);
        }
        if (Marshal.GetLastWin32Error() != 18) { throw SnapshotFailure(); } // ERROR_NO_MORE_FILES
        return entries;
    }

    private static WindowReferenceException SnapshotFailure() => new(Marshal.GetLastWin32Error() == 5
        ? ErrorCode.ACCESS_DENIED : ErrorCode.SESSION_TARGET_UNRESOLVED);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NativeProcessEntry
    {
        public uint Size, Usage, Id;
        public nuint HeapId;
        public uint ModuleId, Threads, ParentId;
        public int Priority;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string Name;
    }

    private sealed class SnapshotHandle() : SafeHandleZeroOrMinusOneIsInvalid(true)
    {
        protected override bool ReleaseHandle() => CloseHandle(handle);
    }
    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    private static extern SnapshotHandle CreateToolhelp32Snapshot(uint flags, uint id);
    [DllImport("kernel32.dll", ExactSpelling = true, CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32FirstW(SnapshotHandle snapshot, ref NativeProcessEntry entry);
    [DllImport("kernel32.dll", ExactSpelling = true, CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32NextW(SnapshotHandle snapshot, ref NativeProcessEntry entry);
    [DllImport("kernel32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(nint handle);
}
