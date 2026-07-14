using System.Runtime.InteropServices;

namespace TinadecTools.Runtime.Sandbox.Windows;

internal static class Win32AclApi
{
    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool CreateWellKnownSid(
        int WellKnownSidType, IntPtr DomainSid,
        IntPtr pSid, ref uint cbSid);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool InitializeAcl(
        IntPtr pAcl, uint nAclLength, uint dwAclRevision);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool AddAccessAllowedAce(
        IntPtr pAcl, uint dwAceRevision, uint AccessMask, IntPtr pSid);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool AddAccessDeniedAce(
        IntPtr pAcl, uint dwAceRevision, uint AccessMask, IntPtr pSid);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool InitializeSecurityDescriptor(
        IntPtr pSecurityDescriptor, uint dwRevision);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetSecurityDescriptorDacl(
        IntPtr pSecurityDescriptor, [MarshalAs(UnmanagedType.Bool)] bool bDaclPresent,
        IntPtr pDacl, [MarshalAs(UnmanagedType.Bool)] bool bDaclDefaulted);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetNamedSecurityInfoW(
        string pObjectName, uint ObjectType,
        uint SecurityInfo,
        IntPtr psidOwner, IntPtr psidGroup,
        IntPtr pDacl, IntPtr pSacl);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetNamedSecurityInfoW(
        string pObjectName, uint ObjectType,
        uint SecurityInfo,
        out IntPtr ppsidOwner, out IntPtr ppsidGroup,
        out IntPtr ppDacl, out IntPtr ppSacl,
        out IntPtr ppSecurityDescriptor);
}