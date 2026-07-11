using System.Runtime.InteropServices;

namespace TinadecTools.Runtime.Sandbox.Windows;

[System.Runtime.Versioning.SupportedOSPlatform("windows")]
internal sealed class JobObjectManager : IDisposable
{
    private IntPtr _hJob;

    internal JobObjectManager()
    {
        _hJob = Win32ProcessApi.CreateJobObjectW(IntPtr.Zero, null);
        if (_hJob == IntPtr.Zero)
            throw new InvalidOperationException("CreateJobObjectW failed.");

        var limits = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION
        {
            BasicLimitInformation = new JOBOBJECT_BASIC_LIMIT_INFORMATION
            {
                LimitFlags = Win32Constants.JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
            }
        };

        var size = Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>();
        var ptr = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(limits, ptr, false);
            if (!Win32ProcessApi.SetInformationJobObject(
                _hJob, Win32Constants.JOB_OBJECT_EXTENDED_LIMIT_INFORMATION,
                ptr, (uint)size))
            {
                throw new InvalidOperationException("SetInformationJobObject failed.");
            }
        }
        finally
        {
            Marshal.FreeHGlobal(ptr);
        }
    }

    internal void Assign(IntPtr hProcess)
    {
        if (!Win32ProcessApi.AssignProcessToJobObject(_hJob, hProcess))
            throw new InvalidOperationException("AssignProcessToJobObject failed.");
    }

    internal void Kill()
    {
        if (_hJob != IntPtr.Zero)
            Win32ProcessApi.TerminateJobObject(_hJob, 1);
    }

    public void Dispose()
    {
        Kill();
        if (_hJob != IntPtr.Zero)
        {
            Win32ProcessApi.CloseHandle(_hJob);
            _hJob = IntPtr.Zero;
        }
    }
}