using System.Text;
using IsoForge.Capture;
using IsoForge.Core;

namespace IsoForge.Media;

/// <summary>Beschreibt, was auf dem Medium landet.</summary>
public sealed record MediaMetadata(
    Guid BuildGuid,
    string BuildId,
    CaptureTier Tier,
    DateTimeOffset Created,
    string SourceMachine,
    string SourceVolume,
    string ImageFileName,
    bool Split,
    IReadOnlyList<string> ExtraVolumes,
    bool HasBootsect);

/// <summary>Legt den Verzeichnisbaum an, aus dem anschliessend das ISO gebrannt wird.</summary>
public static class MediaStager
{
    public static void StageBootFiles(string staging, BootFileSet bootFiles, string buildId)
    {
        Log.Step("Startdateien werden zusammengestellt");

        Directory.CreateDirectory(Path.Combine(staging, "boot"));
        Directory.CreateDirectory(Path.Combine(staging, "efi", "boot"));
        Directory.CreateDirectory(Path.Combine(staging, "efi", "microsoft", "boot"));
        Directory.CreateDirectory(Path.Combine(staging, "sources"));

        if (bootFiles.BootSdi is null)
        {
            throw new InvalidOperationException(
                "boot.sdi wurde auf diesem System nicht gefunden. Ohne diese Datei laesst sich kein " +
                "startfaehiges Medium bauen. Geben Sie mit --bootfiles ein Verzeichnis an, das sie enthaelt " +
                "(z. B. den Ordner 'boot' eines Windows-Installationsmediums).");
        }

        BackupFile.Copy(bootFiles.BootSdi, Path.Combine(staging, "boot", "boot.sdi"));

        if (bootFiles.Bootmgr is not null)
        {
            BackupFile.Copy(bootFiles.Bootmgr, Path.Combine(staging, "bootmgr"));
        }
        else
        {
            Log.Warn("bootmgr wurde nicht gefunden - das Medium startet nur im UEFI-Modus.");
        }

        if (bootFiles.BootmgrEfi is not null)
        {
            BackupFile.Copy(bootFiles.BootmgrEfi, Path.Combine(staging, "bootmgr.efi"));
        }

        if (bootFiles.BootmgfwEfi is not null)
        {
            BackupFile.Copy(bootFiles.BootmgfwEfi, Path.Combine(staging, "efi", "boot", "bootx64.efi"));
        }
        else
        {
            Log.Warn("bootmgfw.efi wurde nicht gefunden - das Medium startet nur im BIOS-Modus.");
        }

        CopyBootFonts(staging);

        // Zwei Startkonfigurationen: eine fuer den BIOS-Startmanager, eine fuer den UEFI-Startmanager.
        string description = $"IsoForge Wiederherstellung {buildId}";
        BcdBuilder.CreateStore(Path.Combine(staging, "boot", "bcd"), BcdBuilder.Firmware.Bios, description);
        BcdBuilder.CreateStore(Path.Combine(staging, "efi", "microsoft", "boot", "bcd"), BcdBuilder.Firmware.Uefi, description);
    }

    private static void CopyBootFonts(string staging)
    {
        string source = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Boot", "Fonts");
        if (!Directory.Exists(source))
        {
            Log.Debug("Keine Startschriftarten gefunden - der Startmanager verwendet seine Standardschrift.");
            return;
        }

        foreach (string target in new[]
                 {
                     Path.Combine(staging, "boot", "fonts"),
                     Path.Combine(staging, "efi", "microsoft", "boot", "fonts"),
                 })
        {
            Directory.CreateDirectory(target);
            foreach (string file in Directory.GetFiles(source))
            {
                try
                {
                    BackupFile.Copy(file, Path.Combine(target, Path.GetFileName(file)));
                }
                catch (Exception ex)
                {
                    Log.Debug($"Schriftart {Path.GetFileName(file)} uebersprungen: {ex.Message}");
                }
            }
        }
    }

    /// <summary>
    /// Schreibt die Angaben zum Abbild als Batch-Datei. Das Wiederherstellungsskript laeuft in WinPE, wo
    /// kein JSON-Werkzeug verfuegbar ist - eine aufrufbare .cmd ist dort die einfachste Form.
    /// </summary>
    public static void WriteMetadata(string staging, MediaMetadata metadata)
    {
        string directory = Path.Combine(staging, "IsoForge");
        Directory.CreateDirectory(directory);

        StringBuilder builder = new();
        builder.AppendLine("@echo off");
        builder.AppendLine("rem Von IsoForge erzeugt - Angaben zum Abbild auf diesem Medium.");
        builder.AppendLine($"set \"IF_BUILDID={metadata.BuildId}\"");
        builder.AppendLine($"set \"IF_BUILDGUID={metadata.BuildGuid:B}\"");
        builder.AppendLine($"set \"IF_TIER={metadata.Tier.ShortName()}\"");
        builder.AppendLine($"set \"IF_TIERNAME={Ascii(metadata.Tier.DisplayName())}\"");
        builder.AppendLine($"set \"IF_CREATED={metadata.Created:yyyy-MM-dd HH:mm}\"");
        builder.AppendLine($"set \"IF_SOURCE={Ascii(metadata.SourceMachine)} ({metadata.SourceVolume})\"");
        builder.AppendLine($"set \"IF_BOOTABLE={(metadata.Tier.IsBootable() ? "1" : "0")}\"");
        builder.AppendLine($"set \"IF_IMAGE={metadata.ImageFileName}\"");
        builder.AppendLine($"set \"IF_IMAGESTEM={Path.GetFileNameWithoutExtension(metadata.ImageFileName)}\"");
        builder.AppendLine($"set \"IF_SPLIT={(metadata.Split ? "1" : "0")}\"");
        builder.AppendLine($"set \"IF_EXTRA={string.Join(' ', metadata.ExtraVolumes)}\"");
        builder.AppendLine($"set \"IF_HASBOOTSECT={(metadata.HasBootsect ? "1" : "0")}\"");

        File.WriteAllText(Path.Combine(directory, "media.cmd"), builder.ToString(), Encoding.ASCII);

        // Dieselben Angaben noch einmal maschinenlesbar.
        string extraVolumes = string.Join(", ", metadata.ExtraVolumes.Select(v => "\"" + v + "\""));
        string manifest = string.Join(Environment.NewLine,
            "{",
            "  \"tool\": \"IsoForge\",",
            $"  \"buildId\": \"{metadata.BuildId}\",",
            $"  \"buildGuid\": \"{metadata.BuildGuid}\",",
            $"  \"version\": \"{BuildIdentity.Version}\",",
            $"  \"tier\": \"{metadata.Tier.ShortName()}\",",
            $"  \"tierName\": \"{metadata.Tier.DisplayName()}\",",
            $"  \"created\": \"{metadata.Created:O}\",",
            $"  \"sourceMachine\": \"{metadata.SourceMachine}\",",
            $"  \"sourceVolume\": \"{metadata.SourceVolume}\",",
            $"  \"image\": \"{metadata.ImageFileName}\",",
            $"  \"split\": {(metadata.Split ? "true" : "false")},",
            $"  \"bootable\": {(metadata.Tier.IsBootable() ? "true" : "false")},",
            $"  \"extraVolumes\": [{extraVolumes}]",
            "}");

        File.WriteAllText(Path.Combine(directory, "manifest.json"), manifest, new UTF8Encoding(false));

        File.WriteAllText(
            Path.Combine(directory, "LIESMICH.txt"),
            $"""
            IsoForge - Abbild {metadata.BuildId}

            Dieses Medium enthaelt eine Sicherung von {metadata.SourceMachine} ({metadata.SourceVolume}),
            erstellt am {metadata.Created:dd.MM.yyyy HH:mm}.

            Abstufung: {metadata.Tier.DisplayName()}
            {metadata.Tier.Description()}

            Starten Sie den Zielrechner bzw. die virtuelle Maschine von diesem Medium. Das
            Wiederherstellungsmenue fuehrt durch die weiteren Schritte.

            Ordner 'drivers' auf dem Medium: Alle darin abgelegten Treiber (.inf) werden bei der
            Wiederherstellung automatisch in das System eingespielt.
            """.ReplaceLineEndings("\r\n"),
            Encoding.UTF8);
    }

    /// <summary>bootsect.exe wird nur fuer BIOS-Starts auf einem komplett geleerten Datentraeger gebraucht.</summary>
    public static bool TryStageBootsect(string staging, string? explicitPath)
    {
        string? source = explicitPath ?? FindBootsect();
        if (source is null || !File.Exists(source))
        {
            Log.Warn("bootsect.exe wurde nicht gefunden. Eine Wiederherstellung auf einen vollstaendig " +
                     "geleerten BIOS/MBR-Datentraeger ist damit nicht moeglich; UEFI-Ziele sind nicht betroffen.");
            Log.Warn("Mit --bootsect <Pfad> laesst sich die Datei nachreichen (z. B. \\boot\\bootsect.exe " +
                     "eines Windows-Installationsmediums).");
            return false;
        }

        string target = Path.Combine(staging, "IsoForge", "tools", "bootsect.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.Copy(source, target, overwrite: true);
        Log.Info($"bootsect.exe uebernommen aus {source}");
        return true;
    }

    private static string? FindBootsect()
    {
        List<string> candidates = new()
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "bootsect.exe"),
        };

        foreach (string programFiles in new[]
                 {
                     Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                     Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                 })
        {
            if (string.IsNullOrEmpty(programFiles))
            {
                continue;
            }

            string adk = Path.Combine(programFiles, "Windows Kits", "10", "Assessment and Deployment Kit", "Deployment Tools");
            if (Directory.Exists(adk))
            {
                try
                {
                    candidates.AddRange(Directory.EnumerateFiles(adk, "bootsect.exe", new EnumerationOptions
                    {
                        RecurseSubdirectories = true,
                        MaxRecursionDepth = 4,
                        IgnoreInaccessible = true,
                    }));
                }
                catch (Exception ex)
                {
                    Log.Debug($"ADK-Suche fehlgeschlagen: {ex.Message}");
                }
            }
        }

        return candidates.FirstOrDefault(File.Exists);
    }

    /// <summary>Wandelt Umlaute um - die Batch-Dateien auf dem Medium bleiben reines ASCII.</summary>
    private static string Ascii(string value) => value
        .Replace("ae", "ae").Replace("ä", "ae").Replace("ö", "oe").Replace("ü", "ue")
        .Replace("Ä", "Ae").Replace("Ö", "Oe").Replace("Ü", "Ue").Replace("ß", "ss");
}
