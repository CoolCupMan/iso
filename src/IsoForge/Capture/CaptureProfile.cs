using System.Text;
using IsoForge.Core;

namespace IsoForge.Capture;

/// <summary>Die Abstufungen, in denen ein System gesichert werden kann.</summary>
public enum CaptureTier
{
    /// <summary>Alles: Betriebssystem, Programme, Einstellungen und persoenliche Dateien - eins zu eins.</summary>
    Full,

    /// <summary>Betriebssystem, Programme und Programmeinstellungen, aber ohne persoenliche Dateien.</summary>
    System,

    /// <summary>Nur persoenliche Dateien. Ergibt ein Datenabbild, kein startfaehiges System.</summary>
    Personal,

    /// <summary>Wie <see cref="Full"/>, zusaetzlich um eine eigene Ausschlussliste ergaenzt.</summary>
    Custom,
}

public static class CaptureTierInfo
{
    public static string DisplayName(this CaptureTier tier) => tier switch
    {
        CaptureTier.Full => "Vollstaendig (1:1)",
        CaptureTier.System => "System und Programme (ohne persoenliche Dateien)",
        CaptureTier.Personal => "Nur persoenliche Dateien",
        CaptureTier.Custom => "Eigene Auswahl",
        _ => tier.ToString(),
    };

    public static string Description(this CaptureTier tier) => tier switch
    {
        CaptureTier.Full =>
            "Der komplette Datentraeger inklusive Windows, aller installierten Programme, Einstellungen und " +
            "aller Benutzerdateien. Das wiederhergestellte System entspricht dem gesicherten Stand.",
        CaptureTier.System =>
            "Windows, installierte Programme und deren Einstellungen. Dokumente, Bilder, Musik, Videos, " +
            "Downloads, Desktop und OneDrive-Ordner der Benutzer bleiben aussen vor.",
        CaptureTier.Personal =>
            "Ausschliesslich die Benutzerprofile mit den persoenlichen Dateien. Ergibt ein Datenabbild zum " +
            "Zurueckspielen in ein bestehendes System - damit laesst sich kein Windows starten.",
        CaptureTier.Custom =>
            "Vollstaendige Sicherung, erweitert um die Ausschluesse aus einer selbst gepflegten Textdatei.",
        _ => string.Empty,
    };

    public static bool IsBootable(this CaptureTier tier) => tier != CaptureTier.Personal;

    public static string ShortName(this CaptureTier tier) => tier switch
    {
        CaptureTier.Full => "full",
        CaptureTier.System => "system",
        CaptureTier.Personal => "personal",
        CaptureTier.Custom => "custom",
        _ => tier.ToString().ToLowerInvariant(),
    };

    public static bool TryParse(string value, out CaptureTier tier)
    {
        tier = value.Trim().ToLowerInvariant() switch
        {
            "full" or "voll" or "1:1" => CaptureTier.Full,
            "system" or "os" => CaptureTier.System,
            "personal" or "daten" or "data" => CaptureTier.Personal,
            "custom" or "eigen" => CaptureTier.Custom,
            _ => (CaptureTier)(-1),
        };

        return Enum.IsDefined(tier);
    }
}

/// <summary>
/// Erzeugt die Ausschlussdatei fuer DISM. DISM erlaubt Platzhalter nur im Dateinamen, nicht im Pfad -
/// Benutzerprofile werden deshalb einzeln aufgezaehlt und wortwoertlich eingetragen.
/// </summary>
public sealed class CaptureProfile
{
    private static readonly string[] AlwaysExcluded =
    {
        @"\pagefile.sys",
        @"\swapfile.sys",
        @"\hiberfil.sys",
        @"\$ntfs.log",
        @"\System Volume Information",
        @"\$Recycle.Bin",
        @"\RECYCLER",
        @"\Recovery",
        @"\$WinREAgent",
        @"\$Windows.~BT",
        @"\$Windows.~WS",
        @"\$SysReset",
        @"\PerfLogs",
        @"\Windows\CSC",
        @"\Windows\Temp",
        @"\Windows\Prefetch",
        @"\Windows\MEMORY.DMP",
        @"\Windows\Minidump",
        @"\Windows\LiveKernelReports",
        @"\Windows\SoftwareDistribution\Download",
    };

    private static readonly string[] SystemOnlyProfileExclusions =
    {
        "Desktop",
        "Documents",
        "Downloads",
        "Music",
        "Pictures",
        "Videos",
        "Favorites",
        "OneDrive",
        "Dropbox",
        "3D Objects",
    };

    private static readonly string[] PersonalOnlyRootExclusions =
    {
        @"\Windows",
        @"\Program Files",
        @"\Program Files (x86)",
        @"\ProgramData",
        @"\Boot",
        @"\EFI",
        @"\bootmgr",
        @"\Users\Default",
        @"\Users\All Users",
        @"\Users\Default User",
    };

    private static readonly string[] PerProfileNoise =
    {
        @"AppData\Local\Temp",
        @"AppData\Local\Microsoft\Windows\INetCache",
        @"AppData\Local\Microsoft\Windows\Explorer",
        @"AppData\Local\Microsoft\Windows\WebCache",
        @"AppData\Local\CrashDumps",
        @"AppData\Local\Packages\Microsoft.Windows.Search_cw5n1h2txyewy\LocalState",
        @"NTUSER.DAT.LOG1",
        @"NTUSER.DAT.LOG2",
    };

    public CaptureProfile(CaptureTier tier, string? customExclusionFile = null)
    {
        Tier = tier;
        CustomExclusionFile = customExclusionFile;
    }

    public CaptureTier Tier { get; }

    public string? CustomExclusionFile { get; }

    /// <summary>Schreibt die Konfigurationsdatei und liefert ihren Pfad zurueck.</summary>
    public string WriteConfigFile(string snapshotRoot, string targetPath)
    {
        List<string> exclusions = new(AlwaysExcluded);
        List<string> skipped = new();

        IReadOnlyList<string> profiles = EnumerateUserProfiles(snapshotRoot);

        foreach (string profile in profiles)
        {
            foreach (string noise in PerProfileNoise)
            {
                AddPath(exclusions, skipped, snapshotRoot, Path.Combine("Users", profile, noise));
            }
        }

        switch (Tier)
        {
            case CaptureTier.System:
                foreach (string profile in profiles)
                {
                    foreach (string folder in SystemOnlyProfileExclusions)
                    {
                        AddPath(exclusions, skipped, snapshotRoot, Path.Combine("Users", profile, folder));
                    }
                }

                break;

            case CaptureTier.Personal:
                exclusions.AddRange(PersonalOnlyRootExclusions);
                foreach (string profile in profiles)
                {
                    AddPath(exclusions, skipped, snapshotRoot, Path.Combine("Users", profile, "AppData", "Local", "Packages"));
                }

                break;

            case CaptureTier.Custom when CustomExclusionFile is not null:
                if (!File.Exists(CustomExclusionFile))
                {
                    throw new FileNotFoundException($"Die Ausschlussliste '{CustomExclusionFile}' existiert nicht.");
                }

                foreach (string line in File.ReadAllLines(CustomExclusionFile))
                {
                    string trimmed = line.Trim();
                    if (trimmed.Length > 0 && !trimmed.StartsWith('#') && !trimmed.StartsWith(';'))
                    {
                        exclusions.Add(trimmed.StartsWith('\\') ? trimmed : "\\" + trimmed);
                    }
                }

                break;
        }

        if (skipped.Count > 0)
        {
            Log.Warn($"{skipped.Count} Ordner konnten nicht in die Ausschlussliste aufgenommen werden, weil ihr Pfad " +
                     "Sonderzeichen enthaelt und kein Kurzname verfuegbar ist. Sie werden mitgesichert:");
            foreach (string entry in skipped.Take(10))
            {
                Log.Warn($"    {entry}");
            }
        }

        StringBuilder builder = new();
        builder.AppendLine("; Von IsoForge erzeugt - Ausschluesse fuer DISM /Capture-Image");
        builder.AppendLine($"; Abstufung: {Tier.ShortName()}");
        builder.AppendLine();
        builder.AppendLine("[ExclusionList]");
        foreach (string entry in exclusions.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(e => e, StringComparer.OrdinalIgnoreCase))
        {
            builder.AppendLine(Quote(entry));
        }

        builder.AppendLine();
        builder.AppendLine("[CompressionExclusionList]");
        foreach (string entry in new[] { "*.mp3", "*.zip", "*.cab", "*.wim", "*.esd", "*.7z", "*.rar", "*.jpg", "*.mp4", "*.mkv", @"\WINDOWS\inf\*.pnf" })
        {
            builder.AppendLine(entry);
        }

        Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);

        // Bewusst reines ASCII: die Kodierung von DISM-Konfigurationsdateien ist nicht durchgaengig
        // dokumentiert, ASCII wird von jeder Fassung gelesen.
        File.WriteAllText(targetPath, builder.ToString(), Encoding.ASCII);
        Log.Info($"{exclusions.Count} Ausschlussregeln geschrieben nach {targetPath}");
        return targetPath;
    }

    private static string Quote(string entry) => entry.Contains(' ') ? $"\"{entry}\"" : entry;

    private static IReadOnlyList<string> EnumerateUserProfiles(string snapshotRoot)
    {
        string users = Path.Combine(snapshotRoot, "Users");
        if (!Directory.Exists(users))
        {
            return Array.Empty<string>();
        }

        try
        {
            return Directory.GetDirectories(users)
                .Select(Path.GetFileName)
                .Where(name => !string.IsNullOrEmpty(name))
                .Where(name => !string.Equals(name, "Default", StringComparison.OrdinalIgnoreCase))
                .Where(name => !string.Equals(name, "All Users", StringComparison.OrdinalIgnoreCase))
                .Where(name => !string.Equals(name, "Default User", StringComparison.OrdinalIgnoreCase))
                .Select(name => name!)
                .ToList();
        }
        catch (Exception ex)
        {
            Log.Warn($"Die Benutzerprofile liessen sich nicht auflisten: {ex.Message}");
            return Array.Empty<string>();
        }
    }

    /// <summary>
    /// Nimmt einen Pfad in die Liste auf. Enthaelt er Zeichen ausserhalb von ASCII, wird der 8.3-Kurzname
    /// benutzt; gibt es keinen, bleibt der Ordner in der Sicherung und wird gemeldet.
    /// </summary>
    private static void AddPath(List<string> exclusions, List<string> skipped, string snapshotRoot, string relative)
    {
        string full = Path.Combine(snapshotRoot, relative);

        if (IsAscii(relative))
        {
            exclusions.Add("\\" + relative);
            return;
        }

        string? shortPath = TryGetShortPath(full);
        if (shortPath is not null && shortPath.StartsWith(snapshotRoot, StringComparison.OrdinalIgnoreCase))
        {
            string shortRelative = shortPath[snapshotRoot.Length..].TrimStart('\\');
            if (IsAscii(shortRelative))
            {
                exclusions.Add("\\" + shortRelative);
                return;
            }
        }

        skipped.Add(relative);
    }

    private static bool IsAscii(string value) => value.All(c => c is >= ' ' and <= '~');

    private static string? TryGetShortPath(string path)
    {
        uint length = NativeMethods.GetShortPathName(path, null, 0);
        if (length == 0)
        {
            return null;
        }

        char[] buffer = new char[length];
        uint written = NativeMethods.GetShortPathName(path, buffer, length);
        return written == 0 ? null : new string(buffer, 0, (int)written);
    }
}
