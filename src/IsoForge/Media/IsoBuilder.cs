using System.Security.Cryptography;
using IsoForge.Capture;
using IsoForge.Core;

namespace IsoForge.Media;

public sealed record IsoBuildResult(string Path, long SizeBytes, string Sha256, string Method);

/// <summary>
/// Schreibt das fertige Medium. Bevorzugt wird oscdimg.exe aus dem Windows-ADK, weil das Ergebnis dann
/// bitgleich zu dem ist, was Microsoft selbst erzeugt. Fehlt das ADK - der Normalfall -, uebernimmt der
/// mitgelieferte ISO-Schreiber.
/// </summary>
public static class IsoBuilder
{
    public static IsoBuildResult Build(
        string staging,
        string outputPath,
        string volumeLabel,
        BootFileSet bootFiles,
        string? oscdimgOverride)
    {
        Log.Step("ISO-Abbild wird geschrieben");

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath))!);
        if (File.Exists(outputPath))
        {
            File.Delete(outputPath);
        }

        string? oscdimg = oscdimgOverride ?? BootFileLocator.FindOscdimg();
        string method;

        if (oscdimg is not null && File.Exists(oscdimg) && bootFiles.EtfsBoot is not null && bootFiles.EfiSys is not null)
        {
            BuildWithOscdimg(oscdimg, staging, outputPath, volumeLabel, bootFiles);
            method = "oscdimg";
        }
        else
        {
            BuildWithBuiltInWriter(staging, outputPath, volumeLabel, bootFiles);
            method = "eingebauter ISO-Schreiber";
        }

        long size = new FileInfo(outputPath).Length;
        Log.Info($"Abbild geschrieben: {Format.Bytes(size)}");

        Log.Info("Pruefsumme wird berechnet ...");
        string hash = ComputeSha256(outputPath);
        File.WriteAllText(outputPath + ".sha256", $"{hash} *{Path.GetFileName(outputPath)}{Environment.NewLine}");

        return new IsoBuildResult(outputPath, size, hash, method);
    }

    private static void BuildWithOscdimg(
        string oscdimg,
        string staging,
        string outputPath,
        string volumeLabel,
        BootFileSet bootFiles)
    {
        Log.Info("oscdimg.exe aus dem Windows-ADK wird verwendet.");

        string bootData =
            $"2#p0,e,b\"{bootFiles.EtfsBoot}\"#pEF,e,b\"{bootFiles.EfiSys}\"";

        string arguments =
            $"-m -o -u2 -udfver102 -bootdata:{bootData} -l\"{volumeLabel}\" \"{staging}\" \"{outputPath}\"";

        ProcessRunner.RunOrThrow(oscdimg, arguments, line =>
        {
            if (line.Trim().Length > 0)
            {
                Log.Debug(line.Trim());
            }
        });
    }

    private static void BuildWithBuiltInWriter(string staging, string outputPath, string volumeLabel, BootFileSet bootFiles)
    {
        Log.Info("Eingebauter ISO-Schreiber wird verwendet (ISO 9660 + Joliet + El Torito).");

        byte[]? biosImage = null;
        if (bootFiles.EtfsBoot is not null)
        {
            biosImage = File.ReadAllBytes(bootFiles.EtfsBoot);
            Log.Debug($"BIOS-Startabbild: {bootFiles.EtfsBoot} ({biosImage.Length} Bytes)");
        }
        else
        {
            Log.Warn("etfsboot.com wurde nicht gefunden - das Medium startet nur im UEFI-Modus.");
        }

        byte[]? efiImage = BuildEfiBootImage(staging, bootFiles);

        if (biosImage is null && efiImage is null)
        {
            throw new InvalidOperationException(
                "Es liess sich weder ein BIOS- noch ein UEFI-Startabbild erzeugen. Das Medium waere nicht startfaehig.");
        }

        IsoDirectory root = IsoDirectory.FromDisk(staging);

        IsoImageOptions options = new()
        {
            VolumeLabel = volumeLabel,
            SystemIdentifier = "WIN32",
            VolumeSetIdentifier = volumeLabel,
            PublisherIdentifier = "ISOFORGE",
            DataPreparerIdentifier = $"ISOFORGE {BuildIdentity.ShortId}",
            ApplicationIdentifier = $"ISOFORGE {BuildIdentity.Version}",
            Timestamp = DateTimeOffset.Now,
            BiosBootImage = biosImage,
            EfiBootImage = efiImage,
            Progress = ReportProgress,
        };

        using FileStream output = new(outputPath, FileMode.Create, FileAccess.ReadWrite, FileShare.None, 1 << 20);
        IsoWriteResult result = IsoImageWriter.Write(root, options, output);
        Console.WriteLine();
        Log.Debug($"{result.TotalSectors} Sektoren, Startkatalog bei LBA {result.BootCatalogLba}.");
    }

    /// <summary>
    /// Das UEFI-Startabbild eines El-Torito-Mediums ist ein FAT-Dateisystem. Windows liefert es als
    /// efisys.bin mit; fehlt es, bauen wir eines mit bootmgfw.efi als \EFI\BOOT\BOOTX64.EFI.
    /// </summary>
    private static byte[]? BuildEfiBootImage(string staging, BootFileSet bootFiles)
    {
        if (bootFiles.EfiSys is not null)
        {
            Log.Debug($"UEFI-Startabbild: {bootFiles.EfiSys}");
            return File.ReadAllBytes(bootFiles.EfiSys);
        }

        string stagedBootx64 = Path.Combine(staging, "efi", "boot", "bootx64.efi");
        if (!File.Exists(stagedBootx64))
        {
            Log.Warn("Weder efisys.bin noch bootmgfw.efi vorhanden - das Medium startet nicht im UEFI-Modus.");
            return null;
        }

        Log.Info("efisys.bin fehlt - ein FAT-Startabbild wird selbst erzeugt.");
        byte[] image = FatImageBuilder.CreateEfiBootImage(File.ReadAllBytes(stagedBootx64));
        Log.Debug($"Erzeugtes UEFI-Startabbild: {Format.Bytes(image.Length)}");
        return image;
    }

    private static void ReportProgress(long written, long total)
    {
        if (total <= 0)
        {
            return;
        }

        int percent = (int)(written * 100 / total);
        int filled = percent * 40 / 100;
        Console.Write($"\r  Daten werden geschrieben: [{new string('#', filled)}{new string('.', 40 - filled)}] {percent,3}%");
    }

    public static string ComputeSha256(string path)
    {
        using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, FileOptions.SequentialScan);
        using SHA256 sha = SHA256.Create();
        return Convert.ToHexString(sha.ComputeHash(stream)).ToLowerInvariant();
    }
}
