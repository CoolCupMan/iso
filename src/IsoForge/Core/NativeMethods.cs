using System.ComponentModel;
using System.Runtime.InteropServices;

namespace IsoForge.Core;

internal static partial class NativeMethods
{
    internal const uint SymbolicLinkFlagDirectory = 0x1;
    internal const uint SymbolicLinkFlagAllowUnprivilegedCreate = 0x2;

    internal const uint GenericRead = 0x8000_0000;
    internal const uint FileShareRead = 0x1;
    internal const uint FileShareWrite = 0x2;
    internal const uint FileShareDelete = 0x4;
    internal const uint OpenExisting = 3;
    internal const uint FileFlagBackupSemantics = 0x0200_0000;

    internal const uint TokenAdjustPrivileges = 0x0020;
    internal const uint TokenQuery = 0x0008;
    internal const uint SePrivilegeEnabled = 0x0002;

    [LibraryImport("kernel32.dll", EntryPoint = "CreateSymbolicLinkW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.I1)]
    internal static partial bool CreateSymbolicLink(string symlinkFileName, string targetFileName, uint flags);

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    internal static partial nint CreateFile(
        string fileName,
        uint desiredAccess,
        uint shareMode,
        nint securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        nint templateFile);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.I1)]
    internal static partial bool CloseHandle(nint handle);

    [LibraryImport("kernel32.dll", EntryPoint = "GetDiskFreeSpaceExW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.I1)]
    internal static partial bool GetDiskFreeSpaceEx(
        string directoryName,
        out ulong freeBytesAvailable,
        out ulong totalNumberOfBytes,
        out ulong totalNumberOfFreeBytes);

    // Der LibraryImport-Generator kann [Out] char[] nicht abbilden, daher hier der klassische Weg.
    [DllImport("kernel32.dll", EntryPoint = "GetShortPathNameW", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern uint GetShortPathName(string longPath, [Out] char[]? shortPath, uint bufferLength);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.I1)]
    internal static partial bool OpenProcessToken(nint processHandle, uint desiredAccess, out nint tokenHandle);

    [LibraryImport("advapi32.dll", EntryPoint = "LookupPrivilegeValueW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.I1)]
    internal static partial bool LookupPrivilegeValue(string? systemName, string name, out long luid);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.I1)]
    internal static partial bool AdjustTokenPrivileges(
        nint tokenHandle,
        [MarshalAs(UnmanagedType.I1)] bool disableAllPrivileges,
        ref TokenPrivileges newState,
        uint bufferLength,
        nint previousState,
        nint returnLength);

    [StructLayout(LayoutKind.Sequential)]
    internal struct TokenPrivileges
    {
        public uint PrivilegeCount;
        public long Luid;
        public uint Attributes;
    }
}

/// <summary>Schaltet Prozess-Privilegien frei, die ein Administrator besitzt, aber nicht automatisch aktiviert hat.</summary>
public static class Privileges
{
    public const string Backup = "SeBackupPrivilege";
    public const string Restore = "SeRestorePrivilege";
    public const string Security = "SeSecurityPrivilege";
    public const string CreateSymbolicLink = "SeCreateSymbolicLinkPrivilege";
    public const string TakeOwnership = "SeTakeOwnershipPrivilege";

    public static bool TryEnable(string privilegeName)
    {
        if (!NativeMethods.OpenProcessToken(
                GetCurrentProcessHandle(),
                NativeMethods.TokenAdjustPrivileges | NativeMethods.TokenQuery,
                out nint token))
        {
            Log.Debug($"OpenProcessToken fehlgeschlagen: {new Win32Exception(Marshal.GetLastWin32Error()).Message}");
            return false;
        }

        try
        {
            if (!NativeMethods.LookupPrivilegeValue(null, privilegeName, out long luid))
            {
                return false;
            }

            NativeMethods.TokenPrivileges privileges = new()
            {
                PrivilegeCount = 1,
                Luid = luid,
                Attributes = NativeMethods.SePrivilegeEnabled,
            };

            if (!NativeMethods.AdjustTokenPrivileges(token, false, ref privileges, 0, nint.Zero, nint.Zero))
            {
                return false;
            }

            // AdjustTokenPrivileges meldet auch dann Erfolg, wenn nicht alle Rechte gesetzt wurden.
            return Marshal.GetLastWin32Error() == 0;
        }
        finally
        {
            NativeMethods.CloseHandle(token);
        }
    }

    public static void EnableAll()
    {
        foreach (string privilege in new[] { Backup, Restore, Security, CreateSymbolicLink, TakeOwnership })
        {
            if (!TryEnable(privilege))
            {
                Log.Debug($"Privileg {privilege} konnte nicht aktiviert werden.");
            }
        }
    }

    private static nint GetCurrentProcessHandle() => System.Diagnostics.Process.GetCurrentProcess().Handle;
}
