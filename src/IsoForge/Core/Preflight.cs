using System.Runtime.InteropServices;
using System.Security.Principal;
using IsoForge.Capture;

namespace IsoForge.Core;

public static class Preflight
{
    /// <summary>Erfahrungswert: eine Windows-Installation schrumpft mit /Compress:max auf etwa die Haelfte.</summary>
    private const double CompressionFactor = 0.55;

    private const long BootEnvironmentBytes = 900L * 1024 * 1024;

    public static void CheckEnvironment()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            throw new PlatformNotSupportedException("IsoForge sichert ein laufendes Windows und laeuft nur dort.");
        }

        if (!IsElevated())
        {
            throw new UnauthorizedAccessException(
                "IsoForge braucht Administratorrechte: Schattenkopien, DISM und der Zugriff auf die " +
                "Wiederherstellungsumgebung sind sonst nicht moeglich. Starten Sie das Programm erneut " +
                "ueber 'Als Administrator ausfuehren'.");
        }

        DismRunner.EnsureAvailable();
        Log.Debug($"DISM-Version: {DismRunner.Version()}");
    }

    public static bool IsElevated()
    {
        using WindowsIdentity identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    public static long EstimateRequiredBytes(VolumeInfo systemVolume, IEnumerable<string> extraVolumes)
    {
        long estimate = (long)(systemVolume.UsedBytes * CompressionFactor) + BootEnvironmentBytes;

        foreach (string letter in extraVolumes)
        {
            try
            {
                estimate += (long)(VolumeScanner.ByLetter(letter).UsedBytes * CompressionFactor);
            }
            catch (Exception ex)
            {
                Log.Debug($"Laufwerk {letter} liess sich nicht einschaetzen: {ex.Message}");
            }
        }

        // Waehrend des Laufs liegen Zwischenstand und fertiges ISO gleichzeitig auf der Platte.
        return (long)(estimate * 2.1);
    }

    public static void CheckDiskSpace(string outputDirectory, VolumeInfo systemVolume, IEnumerable<string> extraVolumes)
    {
        Directory.CreateDirectory(outputDirectory);

        long required = EstimateRequiredBytes(systemVolume, extraVolumes);
        long available = VolumeScanner.FreeSpaceAt(outputDirectory);

        Log.Info($"Geschaetzter Platzbedarf: {Format.Bytes(required)} - verfuegbar: {Format.Bytes(available)}");

        string outputRoot = Path.GetPathRoot(Path.GetFullPath(outputDirectory))!.TrimEnd('\\', ':');
        if (string.Equals(outputRoot, systemVolume.DriveLetter, StringComparison.OrdinalIgnoreCase))
        {
            Log.Warn("Das Ziel liegt auf dem Systemlaufwerk. Die Sicherung selbst gelangt zwar nicht in das " +
                     "Abbild - die Schattenkopie friert den Zustand davor ein -, der Platz wird aber knapp. " +
                     "Ein anderes Laufwerk ist die bessere Wahl.");
        }

        if (available >= 0 && available < required)
        {
            throw new IOException(
                $"Auf {outputRoot}: sind nur {Format.Bytes(available)} frei, gebraucht werden etwa " +
                $"{Format.Bytes(required)}. Waehlen Sie ein anderes Ziel oder eine kleinere Abstufung.");
        }
    }
}
