namespace TinadecTools.Runtime.Sandbox.Windows;

internal static class Win32Constants
{
    // Job Object
    public const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x2000;
    public const int JOB_OBJECT_EXTENDED_LIMIT_INFORMATION = 9;

    // Process creation
    public const uint EXTENDED_STARTUPINFO_PRESENT = 0x00080000;
    public const uint CREATE_SUSPENDED = 0x00000004;
    public const uint CREATE_NO_WINDOW = 0x08000000;
    public const uint CREATE_UNICODE_ENVIRONMENT = 0x00000400;
    public const uint CREATE_NEW_PROCESS_GROUP = 0x00000200;

    // Token
    public const uint TOKEN_ASSIGN_PRIMARY = 0x0001;
    public const uint TOKEN_DUPLICATE = 0x0002;
    public const uint TOKEN_IMPERSONATE = 0x0004;
    public const uint TOKEN_QUERY = 0x0008;
    public const uint TOKEN_ALL_ACCESS = 0xF01FF;
    public const uint TokenLinkedToken = 19;

    // ACL / Security
    public const uint DACL_SECURITY_INFORMATION = 0x00000004;
    public const uint OWNER_SECURITY_INFORMATION = 0x00000001;
    public const uint PROTECTED_DACL_SECURITY_INFORMATION = 0x80000000;
    public const uint SE_GROUP_USE_DENY_ONLY = 0x00000010;

    // Well-known SIDs
    public const int SECURITY_BUILTIN_DOMAIN_RID = 0x00000020;
    public const int DOMAIN_ALIAS_RID_USERS = 0x00000222;
    public const int DOMAIN_ALIAS_RID_ADMINS = 0x00000220;

    // NetUser
    public const uint USER_PRIV_USER = 1;
    public const uint UF_DONT_EXPIRE_PASSWD = 0x00000200;
    public const uint UF_NORMAL_ACCOUNT = 0x00000200;
    public const uint UF_SCRIPT = 0x00000001;
    public const int NERR_Success = 0;
    public const int NERR_UserNotFound = 2221;
    public const int NERR_UserExists = 2224;
    public const int ERROR_SUCCESS = 0;

    // Wait
    public const uint WAIT_OBJECT_0 = 0;
    public const uint WAIT_TIMEOUT = 0x00000102;
    public const uint INFINITE = 0xFFFFFFFF;

    // ACL revision
    public const uint ACL_REVISION = 2;

    // SE_OBJECT_TYPE
    public const uint SE_FILE_OBJECT = 1;

    // GENERIC access rights for files
    public const uint GENERIC_READ = 0x80000000;
    public const uint GENERIC_WRITE = 0x40000000;
    public const uint GENERIC_EXECUTE = 0x20000000;
    public const uint GENERIC_ALL = 0x10000000;
    public const uint DELETE = 0x00010000;
    public const uint READ_CONTROL = 0x00020000;
    public const uint WRITE_DAC = 0x00040000;
    public const uint SYNCHRONIZE = 0x00100000;

    // Logon
    public const uint LOGON32_LOGON_BATCH = 2;
    public const uint LOGON32_LOGON_INTERACTIVE = 2;
    public const uint LOGON32_PROVIDER_DEFAULT = 0;

    // Security descriptor revision
    public const uint SECURITY_DESCRIPTOR_REVISION = 1;

    // Token elevation type
    public const uint TokenElevationType = 18;
    public const uint TokenElevation = 20;
}
