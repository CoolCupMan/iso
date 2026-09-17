using System.Reflection;
using System.Text;
using IsoForge.Capture;
using IsoForge.Core;

namespace IsoForge.Media;

/// <summary>
/// Macht aus der Windows-Wiederherstellungsumgebung des laufenden Systems das Startsystem des Mediums.
/// WinRE ist bereits ein vollstaendiges WinPE mit diskpart, DISM und bcdboot an Bord - damit entfaellt
/// die Installation des Windows-ADK.
/// </summary>
public static class WinPeBuilder
{
    public static void Build(string sourceWim, string targetBootWim, string mountDirectory, string logPath)
    {
        Log.Step("Startsystem wird vorbereitet");

        Log.Info($"Wiederherstellungsumgebung wird kopiert ({Format.Bytes(BackupFile.Length(sourceWim))}) ...");
        BackupFile.Copy(sourceWim, targetBootWim);

        // Die Quelldatei ist versteckt und schreibgeschuetzt; die Kopie muss beschreibbar sein.
        File.SetAttributes(targetBootWim, FileAttributes.Normal);

        if (Directory.Exists(mountDirectory) && Directory.EnumerateFileSystemEntries(mountDirectory).Any())
        {
            Log.Warn("Der Einhaengepunkt war nicht leer - Reste eines frueheren Laufs werden entfernt.");
            DismRunner.CleanupMountPoints();
        }

        Directory.CreateDirectory(mountDirectory);
        DismRunner.MountImage(targetBootWim, 1, mountDirectory, readOnly: false, logPath);

        try
        {
            InjectPayload(mountDirectory);
        }
        catch
        {
            Log.Error("Das Startsystem konnte nicht angepasst werden - die Einhaengung wird verworfen.");
            TryUnmount(mountDirectory, commit: false, logPath);
            throw;
        }

        DismRunner.UnmountImage(mountDirectory, commit: true, logPath);
        Log.Info($"Startsystem fertig: {Format.Bytes(new FileInfo(targetBootWim).Length)}");
    }

    private static void InjectPayload(string mountDirectory)
    {
        string isoForgeDirectory = Path.Combine(mountDirectory, "IsoForge");
        Directory.CreateDirectory(isoForgeDirectory);

        WriteResource("Boot.cmd", Path.Combine(isoForgeDirectory, "Boot.cmd"));
        WriteResource("Restore.cmd", Path.Combine(isoForgeDirectory, "Restore.cmd"));

        // winpeshl.ini ersetzt die Wiederherstellungsoberflaeche durch unser Skript.
        string system32 = Path.Combine(mountDirectory, "Windows", "System32");
        Directory.CreateDirectory(system32);
        WriteResource("winpeshl.ini", Path.Combine(system32, "winpeshl.ini"));

        // startnet.cmd wird bei gesetztem winpeshl.ini nicht mehr ausgefuehrt, bleibt aber als
        // Rueckfallebene sinnvoll gefuellt.
        File.WriteAllText(
            Path.Combine(system32, "startnet.cmd"),
            "@echo off\r\nwpeinit\r\ncall X:\\IsoForge\\Boot.cmd\r\n",
            Encoding.ASCII);

        Log.Info("Wiederherstellungsskripte in das Startsystem eingebettet.");
    }

    private static void WriteResource(string name, string targetPath)
    {
        Assembly assembly = Assembly.GetExecutingAssembly();
        string? resourceName = assembly.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith("." + name, StringComparison.OrdinalIgnoreCase));

        if (resourceName is null)
        {
            throw new InvalidOperationException($"Die eingebettete Datei '{name}' fehlt in dieser Programmfassung.");
        }

        using Stream source = assembly.GetManifestResourceStream(resourceName)!;
        Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
        using FileStream target = new(targetPath, FileMode.Create, FileAccess.Write);
        source.CopyTo(target);
    }

    private static void TryUnmount(string mountDirectory, bool commit, string logPath)
    {
        try
        {
            DismRunner.UnmountImage(mountDirectory, commit, logPath);
        }
        catch (Exception ex)
        {
            Log.Warn($"Die Einhaengung {mountDirectory} blieb bestehen: {ex.Message}");
            Log.Warn("Mit 'dism /Cleanup-Wim' laesst sie sich spaeter aufloesen.");
        }
    }
}
