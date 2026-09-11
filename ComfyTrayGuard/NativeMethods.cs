using System.IO.Pipes;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace ComfyTray;

/// <summary>
/// The one Win32 call the guard cannot reach from managed code.
///
/// <para>
/// <c>[LibraryImport]</c> rather than <c>[DllImport]</c>, because the repository builds with
/// <c>TreatWarningsAsErrors</c> and <c>AnalysisLevel=latest-All</c>, under which SYSLIB1054
/// makes the older attribute an error.
/// </para>
/// </summary>
internal static partial class NativeMethods
{
    /// <summary>
    /// Identifies the process at the other end of a connected pipe. Used only to record who
    /// connected — see <see cref="PipeServer"/> for why that is an audit trail rather than a
    /// security control.
    /// </summary>
    public static bool TryGetClientProcessId(SafePipeHandle handle, out uint clientProcessId)
    {
        clientProcessId = 0;

        // The source-generated marshaller cannot take a SafeHandle (SYSLIB1051), so the raw
        // handle is passed instead. Ref-counting it around the call is what a SafeHandle would
        // otherwise be doing: without it the pipe could be closed on another thread mid-call and
        // the handle reused for something else.
        var referenced = false;
        try
        {
            handle.DangerousAddRef(ref referenced);
            return GetNamedPipeClientProcessId(handle.DangerousGetHandle(), out clientProcessId);
        }
        finally
        {
            if (referenced)
            {
                handle.DangerousRelease();
            }
        }
    }

    [System.Runtime.InteropServices.DefaultDllImportSearchPaths(System.Runtime.InteropServices.DllImportSearchPath.System32)]
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetNamedPipeClientProcessId(nint pipe, out uint clientProcessId);
}
