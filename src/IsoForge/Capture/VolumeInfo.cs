using IsoForge.Core;

namespace IsoForge.Capture;

public sealed record VolumeInfo(
    string DriveLetter,
    string Label,
    string FileSystem,
    long TotalBytes,
    long FreeBytes,
    bool IsSystemVolume)
{
    public long UsedBytes => TotalBytes - FreeBytes;

    public string Root => DriveLetter + @":\";

    public override string ToString() =>
        $"{DriveLetter}: {(string.IsNullOrWhiteSpace(Label) ? "(ohne Bezeichnung)" : Label)} - " +
        $"{Format.Bytes(UsedBytes)} belegt von {Format.Bytes(TotalBytes)}{(IsSystemVolume ? " [System]" : string.Empty)}";
}

public static class VolumeScanner
{
    public static IReadOnlyList<VolumeInfo> LocalVolumes()
    {
        string systemDrive = Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.Windows))!
            .TrimEnd('\\', ':');

        List<VolumeInfo> volumes = new();
        foreach (DriveInfo drive in DriveInfo.GetDrives())
        {
            if (drive.DriveType != DriveType.Fixed || !drive.IsReady)
            {
                continue;
            }

            string letter = drive.Name.TrimEnd('\\', ':');
            try
            {
                volumes.Add(new VolumeInfo(
                    letter,
                    drive.VolumeLabel,
                    drive.DriveFormat,
                    drive.TotalSize,
                    drive.TotalFreeSpace,
                    string.Equals(letter, systemDrive, StringComparison.OrdinalIgnoreCase)));
            }
            catch (Exception ex)
            {
                Log.Debug($"Laufwerk {letter}: uebersprungen ({ex.Message}).");
            }
        }

        return volumes;
    }

    public static VolumeInfo SystemVolume() =>
        LocalVolumes().FirstOrDefault(v => v.IsSystemVolume)
        ?? throw new InvalidOperationException("Das Systemlaufwerk liess sich nicht bestimmen.");

    public static VolumeInfo ByLetter(string letter)
    {
        string normalized = letter.TrimEnd('\\', ':').ToUpperInvariant();
        return LocalVolumes().FirstOrDefault(v => string.Equals(v.DriveLetter, normalized, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"Laufwerk {normalized}: wurde nicht gefunden oder ist kein fest eingebauter Datentraeger.");
    }

    public static long FreeSpaceAt(string path)
    {
        string root = Path.GetPathRoot(Path.GetFullPath(path)) ?? throw new ArgumentException("Pfad ohne Wurzel.", nameof(path));
        return NativeMethods.GetDiskFreeSpaceEx(root, out _, out _, out ulong free) ? (long)free : -1;
    }
}

public static class Format
{
    public static string Bytes(long value)
    {
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        double size = value;
        int unit = 0;
        while (size >= 1024 && unit < units.Length - 1)
        {
            size /= 1024;
            unit++;
        }

        return unit == 0 ? $"{value} B" : $"{size:0.##} {units[unit]}";
    }

    public static string Duration(TimeSpan value) =>
        value.TotalHours >= 1
            ? $"{(int)value.TotalHours}h {value.Minutes}m {value.Seconds}s"
            : value.TotalMinutes >= 1
                ? $"{value.Minutes}m {value.Seconds}s"
                : $"{value.Seconds}s";
}
