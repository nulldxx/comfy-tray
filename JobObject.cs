using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace ComfyTray;

/// <summary>
/// A Win32 job object holding the ComfyUI process and everything it spawns.
///
/// <para>
/// A job is the only way to enumerate a process tree that is reliably correct. Walking parent
/// ids falls apart exactly when it matters — an intermediate process exiting reparents its
/// children, so a node that shells out through a launcher disappears from the tree — and process
/// ids are reused. Membership of a job is decided by the kernel at creation time and cannot be
/// escaped, unless the job permits it, which this one does not.
/// </para>
/// </summary>
internal sealed partial class JobObject : IDisposable
{
    /// <summary>Job information classes, from the SDK.</summary>
    private const int BasicProcessIdListClass = 3;
    private const int ExtendedLimitInformationClass = 9;
    private const uint JobObjectLimitKillOnJobClose = 0x2000;
    private const int ErrorMoreData = 234;

    private readonly SafeJobHandle _handle;

    /// <summary>
    /// Creates the job.
    /// </summary>
    /// <param name="killOnClose">
    /// When true, closing the last handle kills everything in the job — including when the tray
    /// crashes rather than exits. That is deliberate under firewall enforcement: the guard
    /// removes its rules the moment the tray's pipe drops, and a ComfyUI left running after that
    /// would be an unguarded server the user believes is contained. Tying the two together means
    /// there is never a live ComfyUI without the rules that were supposed to hold it.
    /// </param>
    public JobObject(bool killOnClose)
    {
        _handle = new SafeJobHandle(CreateJobObjectW(nint.Zero, null));
        if (_handle.IsInvalid)
        {
            throw new InvalidOperationException(
                $"Could not create a job object (error {Marshal.GetLastWin32Error()}).");
        }

        if (killOnClose)
        {
            SetKillOnClose();
        }
    }

    /// <summary>
    /// Puts a process, and by inheritance everything it goes on to start, into the job.
    ///
    /// <para>
    /// There is a window between <c>CreateProcess</c> and this call in which a grandchild could
    /// start outside the job. It is microseconds wide and Python takes far longer than that to
    /// reach any code that could spawn something; closing it properly would mean creating the
    /// process suspended, which would cost the output redirection the Logs window depends on.
    /// </para>
    /// </summary>
    public void Assign(Process process)
    {
        ArgumentNullException.ThrowIfNull(process);

        if (!WithHandle(job => AssignProcessToJobObject(job, process.Handle)))
        {
            throw new InvalidOperationException(
                $"Could not assign the server to the job object (error {Marshal.GetLastWin32Error()}).");
        }
    }

    /// <summary>Every process currently in the job.</summary>
    public IReadOnlyList<int> GetProcessIds()
    {
        // Start with room for a typical session and grow only if the kernel says there is more.
        var capacity = 64;

        for (var attempt = 0; attempt < 4; attempt++)
        {
            // NumberOfAssignedProcesses + NumberOfProcessIdsInList, then the ids themselves,
            // each of which is pointer-sized.
            var size = (sizeof(uint) * 2) + (capacity * nint.Size);
            var buffer = Marshal.AllocHGlobal(size);

            try
            {
                if (WithHandle(job => QueryInformationJobObject(
                        job, BasicProcessIdListClass, buffer, (uint)size, nint.Zero)))
                {
                    return ReadIds(buffer);
                }

                if (Marshal.GetLastWin32Error() != ErrorMoreData)
                {
                    return [];
                }

                // ERROR_MORE_DATA still fills in how many there actually are.
                var assigned = (int)(uint)Marshal.ReadInt32(buffer, 0);
                capacity = Math.Max(assigned + 16, capacity * 2);
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        return [];
    }

    private static IReadOnlyList<int> ReadIds(nint buffer)
    {
        var count = (int)(uint)Marshal.ReadInt32(buffer, sizeof(uint));
        var ids = new List<int>(count);

        for (var i = 0; i < count; i++)
        {
            var entry = Marshal.ReadIntPtr(buffer, (sizeof(uint) * 2) + (i * nint.Size));
            ids.Add((int)entry);
        }

        return ids;
    }

    private void SetKillOnClose()
    {
        var limits = new JobObjectExtendedLimitInformation();
        limits.BasicLimitInformation.LimitFlags = JobObjectLimitKillOnJobClose;

        var size = Marshal.SizeOf<JobObjectExtendedLimitInformation>();
        var buffer = Marshal.AllocHGlobal(size);

        try
        {
            Marshal.StructureToPtr(limits, buffer, fDeleteOld: false);

            if (!WithHandle(job => SetInformationJobObject(
                    job, ExtendedLimitInformationClass, buffer, (uint)size)))
            {
                throw new InvalidOperationException(
                    "Could not configure the job object to stop ComfyUI with the tray " +
                    $"(error {Marshal.GetLastWin32Error()}).");
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    /// <summary>
    /// Runs a call against the raw handle, holding a reference for its duration so the handle
    /// cannot be closed and its number reused underneath the call. This is the bookkeeping a
    /// SafeHandle parameter would do, which the P/Invoke source generator cannot marshal.
    /// </summary>
    private bool WithHandle(Func<nint, bool> call)
    {
        var referenced = false;
        try
        {
            _handle.DangerousAddRef(ref referenced);
            return call(_handle.DangerousGetHandle());
        }
        finally
        {
            if (referenced)
            {
                _handle.DangerousRelease();
            }
        }
    }

    public void Dispose() => _handle.Dispose();

    // Layouts are the SDK's; the default sequential layout with the runtime's own padding
    // matches what the kernel expects on both x64 and ARM64.

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectBasicLimitInformation
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public nuint MinimumWorkingSetSize;
        public nuint MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public nuint Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectExtendedLimitInformation
    {
        public JobObjectBasicLimitInformation BasicLimitInformation;
        public IoCounters IoInfo;
        public nuint ProcessMemoryLimit;
        public nuint JobMemoryLimit;
        public nuint PeakProcessMemoryUsed;
        public nuint PeakJobMemoryUsed;
    }

    [LibraryImport("kernel32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint CreateJobObjectW(nint attributes, string? name);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AssignProcessToJobObject(nint job, nint process);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetInformationJobObject(
        nint job, int infoClass, nint info, uint length);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool QueryInformationJobObject(
        nint job, int infoClass, nint info, uint length, nint returnedLength);
}

/// <summary>Owns a job object handle. Closing it is what triggers kill-on-close.</summary>
internal sealed partial class SafeJobHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    public SafeJobHandle(nint handle)
        : base(ownsHandle: true) => SetHandle(handle);

    protected override bool ReleaseHandle() => CloseHandle(handle);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(nint handle);
}
