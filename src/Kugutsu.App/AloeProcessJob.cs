using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Kugutsu.Contracts;
using Microsoft.Win32.SafeHandles;

namespace Kugutsu.App;

/// <summary>A private Windows job caps helper memory and kills it when its owner closes.</summary>
internal sealed class AloeProcessJob : IDisposable
{
    private const uint KillOnClose = 0x2000, ProcessMemoryLimit = 0x100, ActiveProcessLimit = 0x8, DieOnUnhandledException = 0x400;
    private readonly SafeFileHandle _handle;
    internal AloeProcessJob(Process process, int memoryMiB)
    {
        _handle = CreateJobObjectW(0, null);
        if (_handle.IsInvalid) { _handle.Dispose(); throw Unavailable(); }
        try
        {
            var limits = new ExtendedLimits { Basic = new() { Flags = KillOnClose | ProcessMemoryLimit | ActiveProcessLimit | DieOnUnhandledException, ActiveProcesses = 1 },
                ProcessMemory = checked((nuint)((long)memoryMiB * 1048576)) };
            if (!SetInformationJobObject(_handle, 9, ref limits, (uint)Marshal.SizeOf<ExtendedLimits>())
                || !AssignProcessToJobObject(_handle, process.SafeHandle)) { throw Unavailable(); }
        }
        catch { _handle.Dispose(); throw; }
    }
    public void Dispose() => _handle.Dispose();
    private static WindowReferenceException Unavailable() => new(ErrorCode.ALOE_UNAVAILABLE);
    [StructLayout(LayoutKind.Sequential)] private struct BasicLimits
    {
        public long PerProcessTime, PerJobTime;
        public uint Flags;
        public nuint MinimumWorkingSet, MaximumWorkingSet;
        public uint ActiveProcesses;
        public nuint Affinity;
        public uint PriorityClass, SchedulingClass;
    }
    [StructLayout(LayoutKind.Sequential)] private struct IoCounters
    { public ulong ReadOperations, WriteOperations, OtherOperations, ReadBytes, WriteBytes, OtherBytes; }
    [StructLayout(LayoutKind.Sequential)] private struct ExtendedLimits
    {
        public BasicLimits Basic;
        public IoCounters Io;
        public nuint ProcessMemory, JobMemory, PeakProcessMemory, PeakJobMemory;
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern SafeFileHandle CreateJobObjectW(nint attributes, string? name);
    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(SafeFileHandle job, int informationClass, ref ExtendedLimits information, uint size);
    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(SafeFileHandle job, SafeProcessHandle process);
}
