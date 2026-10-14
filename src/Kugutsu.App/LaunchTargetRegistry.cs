using System.Collections.Frozen;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Kugutsu.Contracts;
using Kugutsu.Hosting;
using Microsoft.Win32.SafeHandles;

namespace Kugutsu.App;

internal sealed record RegisteredLaunchTarget(string Id, string ExecutablePath, IReadOnlyList<string> Arguments);

/// <summary>検証した実行ファイルの読取りリース。起動完了まで置換・書換えを抑止する。</summary>
internal sealed class LaunchTargetLease(RegisteredLaunchTarget target, FileStream stream) : IDisposable
{
    internal RegisteredLaunchTarget Target { get; } = target;
    public void Dispose() => stream.Dispose();
}

/// <summary>設定に登録した起動対象を固定し、利用時のファイル同一性を照合する。</summary>
internal sealed class LaunchTargetRegistry
{
    private sealed class Entry(RegisteredLaunchTarget target)
    {
        internal RegisteredLaunchTarget Target { get; } = target;
        internal object Gate { get; } = new();
        internal FileIdentity? Identity { get; set; }
        internal byte[]? Hash { get; set; }
    }

    private sealed record FileIdentity(uint Volume, ulong Index, ulong LastWrite, ulong Length);
    private readonly FrozenDictionary<string, Entry> _entries;

    public LaunchTargetRegistry(KugutsuOptions settings)
    {
        _entries = settings.AllowedLaunchTargets.ToFrozenDictionary(target => target.Id,
            target => new Entry(new(target.Id, Path.GetFullPath(target.ExecutablePath),
                Array.AsReadOnly(target.Arguments.ToArray()))), StringComparer.Ordinal);
    }

    internal bool IsRegistered(string id) => _entries.ContainsKey(id);

    internal async ValueTask<LaunchTargetLease> ResolveAsync(string id, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!_entries.TryGetValue(id, out var entry))
        { throw new WindowReferenceException(ErrorCode.LAUNCH_TARGET_NOT_ALLOWED); }
        FileStream? stream = null;
        try
        {
            var path = entry.Target.ExecutablePath;
            var root = Path.GetPathRoot(path);
            if (root is not { Length: 3 } || !char.IsAsciiLetter(root[0]) || root[1] != ':' || root[2] != '\\'
                || !path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            { throw new WindowReferenceException(ErrorCode.LAUNCH_TARGET_NOT_ALLOWED); }
            if (new DriveInfo(root).DriveType is not (DriveType.Fixed or DriveType.Removable or DriveType.CDRom or DriveType.Ram))
            { throw new WindowReferenceException(ErrorCode.LAUNCH_TARGET_NOT_ALLOWED); }
            VerifyPath(path, token);
            stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            var before = ReadIdentity(stream.SafeFileHandle);
            if (before.Length is 0 or > 536870912)
            { throw new WindowReferenceException(ErrorCode.LAUNCH_TARGET_NOT_ALLOWED); }
            var finalPath = new StringBuilder(32768);
            var count = GetFinalPathNameByHandleW(stream.SafeFileHandle, finalPath, (uint)finalPath.Capacity, 0);
            if (count == 0 || count >= finalPath.Capacity
                || !string.Equals(finalPath.ToString(), "\\\\?\\" + path, StringComparison.OrdinalIgnoreCase))
            { throw new WindowReferenceException(ErrorCode.LAUNCH_TARGET_NOT_ALLOWED); }
            var hash = await SHA256.HashDataAsync(stream, token);
            var after = ReadIdentity(stream.SafeFileHandle);
            VerifyPath(path, token);
            if (before != after) { throw new WindowReferenceException(ErrorCode.LAUNCH_TARGET_NOT_ALLOWED); }
            lock (entry.Gate)
            {
                token.ThrowIfCancellationRequested();
                if (entry.Identity is null) { entry.Identity = after; entry.Hash = hash; }
                else if (entry.Identity != after || entry.Hash is null
                    || !CryptographicOperations.FixedTimeEquals(entry.Hash, hash))
                { throw new WindowReferenceException(ErrorCode.LAUNCH_TARGET_NOT_ALLOWED); }
            }
            var lease = new LaunchTargetLease(entry.Target, stream);
            stream = null;
            return lease;
        }
        catch (FileNotFoundException) { throw new WindowReferenceException(ErrorCode.APP_NOT_FOUND); }
        catch (DirectoryNotFoundException) { throw new WindowReferenceException(ErrorCode.APP_NOT_FOUND); }
        catch (UnauthorizedAccessException) { throw new WindowReferenceException(ErrorCode.ACCESS_DENIED); }
        catch (System.Security.SecurityException) { throw new WindowReferenceException(ErrorCode.ACCESS_DENIED); }
        catch (IOException) { throw new WindowReferenceException(ErrorCode.LAUNCH_TARGET_NOT_ALLOWED); }
        finally { stream?.Dispose(); }
    }

    private static void VerifyPath(string path, CancellationToken token)
    {
        string? current = path;
        while (current is not null)
        {
            token.ThrowIfCancellationRequested();
            var attributes = File.GetAttributes(current);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
            { throw new WindowReferenceException(ErrorCode.LAUNCH_TARGET_NOT_ALLOWED); }
            current = Path.GetDirectoryName(current);
        }
    }

    private static FileIdentity ReadIdentity(SafeFileHandle handle)
    {
        if (!GetFileInformationByHandle(handle, out var info))
        { throw new WindowReferenceException(Marshal.GetLastWin32Error() == 5 ? ErrorCode.ACCESS_DENIED : ErrorCode.LAUNCH_TARGET_NOT_ALLOWED); }
        if ((info.Attributes & (uint)(FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
        { throw new WindowReferenceException(ErrorCode.LAUNCH_TARGET_NOT_ALLOWED); }
        return new(info.Volume, ((ulong)info.IndexHigh << 32) | info.IndexLow,
            ((ulong)info.WriteHigh << 32) | info.WriteLow, ((ulong)info.SizeHigh << 32) | info.SizeLow);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeFileInformation
    {
        public uint Attributes, CreatedLow, CreatedHigh, AccessLow, AccessHigh, WriteLow, WriteHigh;
        public uint Volume, SizeHigh, SizeLow, Links, IndexHigh, IndexLow;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out NativeFileInformation info);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandleW(SafeFileHandle handle, StringBuilder path, uint size, uint flags);
}
