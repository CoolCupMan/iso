using System.Security.Cryptography;
using IsoForge.Capture;
using IsoForge.Core;

namespace IsoForge.Media;

/// <summary>Welcher Weg das ISO schreibt.</summary>
public enum IsoEngine
{
    /// <summary>Den besten verfuegbaren Weg selbst waehlen.</summary>
    Auto,

    /// <summary>oscdimg.exe aus dem Windows-ADK.</summary>
    Oscdimg,

    /// <summary>Image Mastering API - in Windows enthalten, erzeugt UDF.</summary>
    Imapi,

    /// <summary>Der mitgelieferte Schreiber (ISO 9660 + Joliet, ohne UDF).</summary>
    BuiltIn,
}

public sealed record IsoBuildResult(string Path, long SizeBytes, string Sha256, string Method);

/// <summary>
/// Schreibt das fertige Medium. Es gibt drei Wege, in dieser Reihenfolge:
///
///   1. oscdimg.exe aus dem Windows-ADK - falls installiert; das Ergebnis entspricht dann bis ins
///      Detail dem, was Microsoft selbst erzeugt.
///   2. Die Image Mastering API (IMAPI2FS) - in jedem Windows enthalten und der Regelfall. Sie legt
///      neben ISO 9660 und Joliet auch UDF an, worauf die UEFI-Firmware vieler Rechner und
///      virtueller Maschinen angewiesen ist.
///   3. Der eingebaute Schreiber - ohne UDF, dafuer voellig unabhaengig vom Betriebssystem. Er
///      springt ein, wenn die beiden anderen Wege ausfallen.
/// </summary>
public static class IsoBuilder
{
    public static IsoBuildResult Build(
        string staging,
        string outputPath,
        string volumeLabel,
        BootFileSet bootFiles,
        string? oscdimgOverride,
        IsoEngine engine = IsoEngine.Auto,
        string? workDirectory = null)
    {
        Log.Step("ISO-Abbild wird geschrieben");

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath))!);
        if (File.Exists(outputPath))
        {
            File.Delete(outputPath);
        }

        string work = workDirectory ?? Path.GetDirectoryName(Path.GetFullPath(outputPath))!;
        string? oscdimg = oscdimgOverride ?? BootFileLocator.FindOscdimg();
        bool oscdimgUsable = oscdimg is not null && File.Exists(oscdimg)
                             && bootFiles.EtfsBoot is not null && bootFiles.EfiSys is not null;

        string method = engine switch
        {
            IsoEngine.Oscdimg when !oscdimgUsable =>
                throw new InvalidOperationException(
                    "oscdimg wurde angefordert, ist aber nicht verwendbar. Es braucht das Windows-ADK sowie " +
                    "etfsboot.com und efisys.bin auf diesem System."),

            IsoEngine.Oscdimg => BuildWithOscdimg(oscdimg!, staging, outputPath, volumeLabel, bootFiles),
            IsoEngine.Imapi => BuildWithImapi(staging, outputPath, volumeLabel, bootFiles, work),
            IsoEngine.BuiltIn => BuildWithBuiltInWriter(staging, outputPath, volumeLabel, bootFiles),
            _ => BuildAutomatically(staging, outputPath, volumeLabel, bootFiles, oscdimg, oscdimgUsable, work),
        };

        long size = new FileInfo(outputPath).Length;
        Log.Info($"Abbild geschrieben: {Format.Bytes(size)}");

        Log.Info("Pruefsumme wird berechnet ...");
        string hash = ComputeSha256(outputPath);
        File.WriteAllText(outputPath + ".sha256", $"{hash} *{Path.GetFileName(outputPath)}{Environment.NewLine}");

        return new IsoBuildResult(outputPath, size, hash, method);
    }

    private static string BuildAutomatically(
        string staging,
        string outputPath,
        string volumeLabel,
        BootFileSet bootFiles,
        string? oscdimg,
        bool oscdimgUsable,
        string work)
    {
        if (oscdimgUsable)
        {
            return BuildWithOscdimg(oscdimg!, staging, outputPath, volumeLabel, bootFiles);
        }

        if (ImapiIsoWriter.IsAvailable())
        {
            try
            {
                return BuildWithImapi(staging, outputPath, volumeLabel, bootFiles, work);
            }
            catch (Exception ex)
            {
                Log.Warn($"Die Image Mastering API ist ausgefallen: {ex.Message}");
                Log.Warn("Es wird auf den eingebauten Schreiber ausgewichen. Das Ergebnis traegt dann kein " +
                         "UDF-Dateisystem; auf einzelnen UEFI-Firmwares kann der Start damit scheitern.");
            }
        }
        else
        {
            Log.Warn("Die Image Mastering API steht nicht zur Verfuegung - es wird ohne UDF geschrieben.");
        }

        return BuildWithBuiltInWriter(staging, outputPath, volumeLabel, bootFiles);
    }

    private static string BuildWithOscdimg(
        string oscdimg,
        string staging,
        string outputPath,
        string volumeLabel,
        BootFileSet bootFiles)
    {
        Log.Info("oscdimg.exe aus dem Windows-ADK wird verwendet.");

        string bootData = $"2#p0,e,b\"{bootFiles.EtfsBoot}\"#pEF,e,b\"{bootFiles.EfiSys}\"";
        string arguments = $"-m -o -u2 -udfver102 -bootdata:{bootData} -l\"{volumeLabel}\" \"{staging}\" \"{outputPath}\"";

        ProcessRunner.RunOrThrow(oscdimg, arguments, line =>
        {
            if (line.Trim().Length > 0)
            {
                Log.Debug(line.Trim());
            }
        });

        return "oscdimg (ISO 9660 + UDF)";
    }

    private static string BuildWithImapi(
        string staging,
        string outputPath,
        string volumeLabel,
        BootFileSet bootFiles,
        string work)
    {
        Log.Info("Die in Windows enthaltene Image Mastering API wird verwendet.");

        (byte[]? bios, byte[]? efi) = LoadBootImages(staging, bootFiles);
        RequireAtLeastOneBootImage(bios, efi);

        ImapiIsoWriter.Write(staging, outputPath, volumeLabel, bios, efi, Path.Combine(work, "bootimages"));
        return "IMAPI2FS (ISO 9660 + Joliet + UDF 1.02)";
    }

    private static string BuildWithBuiltInWriter(string staging, string outputPath, string volumeLabel, BootFileSet bootFiles)
    {
        Log.Info("Der eingebaute ISO-Schreiber wird verwendet (ISO 9660 + Joliet + El Torito).");

        (byte[]? bios, byte[]? efi) = LoadBootImages(staging, bootFiles);
        RequireAtLeastOneBootImage(bios, efi);

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
            BiosBootImage = bios,
            EfiBootImage = efi,
            Progress = ReportProgress,
        };

        using FileStream output = new(outputPath, FileMode.Create, FileAccess.ReadWrite, FileShare.None, 1 << 20);
        IsoWriteResult result = IsoImageWriter.Write(root, options, output);
        Console.WriteLine();
        Log.Debug($"{result.TotalSectors} Sektoren, Startkatalog bei LBA {result.BootCatalogLba}.");

        return "eingebauter Schreiber (ISO 9660 + Joliet, ohne UDF)";
    }

    private static (byte[]? Bios, byte[]? Efi) LoadBootImages(string staging, BootFileSet bootFiles)
    {
        byte[]? bios = null;
        if (bootFiles.EtfsBoot is not null)
        {
            bios = File.ReadAllBytes(bootFiles.EtfsBoot);
            Log.Debug($"BIOS-Startabbild: {bootFiles.EtfsBoot} ({bios.Length} Bytes)");
        }
        else
        {
            Log.Warn("etfsboot.com wurde nicht gefunden - das Medium startet nur im UEFI-Modus.");
        }

        return (bios, BuildEfiBootImage(staging, bootFiles));
    }

    private static void RequireAtLeastOneBootImage(byte[]? bios, byte[]? efi)
    {
        if (bios is null && efi is null)
        {
            throw new InvalidOperationException(
                "Es liess sich weder ein BIOS- noch ein UEFI-Startabbild erzeugen. Das Medium waere nicht startfaehig.");
        }
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

    public static bool TryParseEngine(string value, out IsoEngine engine)
    {
        engine = value.Trim().ToLowerInvariant() switch
        {
            "auto" => IsoEngine.Auto,
            "oscdimg" => IsoEngine.Oscdimg,
            "imapi" => IsoEngine.Imapi,
            "builtin" or "eingebaut" => IsoEngine.BuiltIn,
            _ => (IsoEngine)(-1),
        };

        return Enum.IsDefined(engine);
    }
}
