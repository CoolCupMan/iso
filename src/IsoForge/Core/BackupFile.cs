using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace IsoForge.Core;

/// <summary>
/// Oeffnet Dateien mit Sicherungssemantik. Damit kommt ein Administrator auch an Dateien heran, deren
/// Zugriffsliste nur SYSTEM erlaubt - etwa an C:\Recovery\WindowsRE\Winre.wim.
/// </summary>
public static class BackupFile
{
    public static bool Exists(string path)
    {
        if (File.Exists(path))
        {
            return true;
        }

        try
        {
            using FileStream stream = Open(path);
            return true;
        }
        catch (Exception ex)
        {
            Log.Debug($"'{path}' nicht lesbar: {ex.Message}");
            return false;
        }
    }

    public static FileStream Open(string path)
    {
        nint handle = NativeMethods.CreateFile(
            path,
            NativeMethods.GenericRead,
            NativeMethods.FileShareRead | NativeMethods.FileShareWrite | NativeMethods.FileShareDelete,
            nint.Zero,
            NativeMethods.OpenExisting,
            NativeMethods.FileFlagBackupSemantics,
            nint.Zero);

        if (handle == -1)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), $"'{path}' liess sich nicht oeffnen.");
        }

        return new FileStream(new SafeFileHandle(handle, ownsHandle: true), FileAccess.Read, 1 << 20);
    }

    public static void Copy(string sourcePath, string destinationPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
        using FileStream source = Open(sourcePath);
        using FileStream destination = new(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20);
        source.CopyTo(destination, 1 << 20);
    }

    public static long Length(string path)
    {
        using FileStream stream = Open(path);
        return stream.Length;
    }
}
