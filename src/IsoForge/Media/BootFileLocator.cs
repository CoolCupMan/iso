using System.Text.RegularExpressions;
using IsoForge.Core;

namespace IsoForge.Media;

public sealed record BootFileSet(
    string? Bootmgr,
    string? BootmgrEfi,
    string? BootmgfwEfi,
    string? BootSdi,
    string? EtfsBoot,
    string? EfiSys)
{
    public bool CanBootBios => Bootmgr is not null && BootSdi is not null && EtfsBoot is not null;

    public bool CanBootUefi => BootmgfwEfi is not null && BootSdi is not null;
}

/// <summary>
/// Sucht die Startdateien, aus denen ein bootfaehiges Medium zusammengesetzt wird. Ein installiertes
/// Windows bringt sie alle mit - unter %SystemRoot%\Boot liegen sowohl der Startmanager als auch die
/// Vorlagen, die sonst das Windows-ADK liefern wuerde.
/// </summary>
public static partial class BootFileLocator
{
    [GeneratedRegex(@"[A-Za-z]:\\.*?Winre\.wim|\\\\\?\\GLOBALROOT\\device\\harddisk\d+\\partition\d+\\.*", RegexOptions.IgnoreCase)]
    private static partial Regex ReAgentLocationPattern();

    public static BootFileSet Locate(string? snapshotRoot = null)
    {
        string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        string boot = Path.Combine(windows, "Boot");

        BootFileSet set = new(
            Bootmgr: FindFirst(
                Path.Combine(boot, "PCAT", "bootmgr"),
                Path.Combine(boot, "PCAT", "bootmgr.exe")),
            BootmgrEfi: FindFirst(Path.Combine(boot, "EFI", "bootmgr.efi")),
            BootmgfwEfi: FindFirst(
                Path.Combine(boot, "EFI", "bootmgfw.efi"),
                Path.Combine(windows, "System32", "bootmgfw.efi")),
            BootSdi: FindFirst(
                Path.Combine(boot, "DVD", "PCAT", "boot.sdi"),
                Path.Combine(boot, "DVD", "PCAT", "boot", "boot.sdi"),
                Path.Combine(windows, "System32", "Recovery", "boot.sdi"))
                ?? SearchUnder(boot, "boot.sdi"),
            EtfsBoot: FindFirst(
                Path.Combine(boot, "DVD", "PCAT", "etfsboot.com"),
                Path.Combine(boot, "DVD", "PCAT", "en-US", "etfsboot.com"))
                ?? SearchUnder(boot, "etfsboot.com"),
            EfiSys: FindFirst(
                Path.Combine(boot, "DVD", "EFI", "en-US", "efisys.bin"),
                Path.Combine(boot, "DVD", "EFI", "efisys.bin"))
                ?? SearchUnder(boot, "efisys.bin"));

        // Manche Fundstellen liegen nur in der Wiederherstellungsumgebung; dort noch einmal nachsehen.
        if (set.BootSdi is null && snapshotRoot is not null)
        {
            set = set with { BootSdi = SearchUnder(Path.Combine(snapshotRoot, "Recovery"), "boot.sdi") };
        }

        Log.Debug($"bootmgr      : {set.Bootmgr ?? "nicht gefunden"}");
        Log.Debug($"bootmgfw.efi : {set.BootmgfwEfi ?? "nicht gefunden"}");
        Log.Debug($"boot.sdi     : {set.BootSdi ?? "nicht gefunden"}");
        Log.Debug($"etfsboot.com : {set.EtfsBoot ?? "nicht gefunden"}");
        Log.Debug($"efisys.bin   : {set.EfiSys ?? "nicht gefunden"}");

        return set;
    }

    /// <summary>Findet die Wiederherstellungsumgebung, die als Grundlage fuer das Startsystem dient.</summary>
    public static string? LocateWinRe(string? snapshotRoot)
    {
        List<string> candidates = new();

        string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        candidates.Add(Path.Combine(windows, "System32", "Recovery", "WinRE.wim"));

        string systemDrive = Path.GetPathRoot(windows)!;
        candidates.Add(Path.Combine(systemDrive, "Recovery", "WindowsRE", "Winre.wim"));

        if (snapshotRoot is not null)
        {
            candidates.Add(Path.Combine(snapshotRoot, "Recovery", "WindowsRE", "Winre.wim"));
            candidates.Add(Path.Combine(snapshotRoot, "Windows", "System32", "Recovery", "WinRE.wim"));
        }

        foreach (string path in candidates)
        {
            if (BackupFile.Exists(path))
            {
                Log.Info($"Wiederherstellungsumgebung gefunden: {path}");
                return path;
            }
        }

        // Liegt sie auf einer eigenen Partition ohne Laufwerksbuchstaben, verraet reagentc den Geraetepfad.
        string? fromReAgent = LocateViaReAgent();
        if (fromReAgent is not null)
        {
            return fromReAgent;
        }

        // Zuletzt: die WinPE-Fassung aus dem Windows-ADK, falls installiert.
        foreach (string adk in AdkWinPeCandidates())
        {
            if (File.Exists(adk))
            {
                Log.Info($"WinPE aus dem Windows-ADK wird verwendet: {adk}");
                return adk;
            }
        }

        return null;
    }

    private static string? LocateViaReAgent()
    {
        try
        {
            ProcessResult result = ProcessRunner.Run(
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "ReAgentc.exe"), "/info");

            Match match = ReAgentLocationPattern().Match(result.StandardOutput);
            if (!match.Success)
            {
                return null;
            }

            string location = match.Value.Trim();
            string candidate = location.EndsWith(".wim", StringComparison.OrdinalIgnoreCase)
                ? location
                : Path.Combine(location, "Winre.wim");

            if (BackupFile.Exists(candidate))
            {
                Log.Info($"Wiederherstellungsumgebung ueber reagentc gefunden: {candidate}");
                return candidate;
            }
        }
        catch (Exception ex)
        {
            Log.Debug($"reagentc lieferte keinen brauchbaren Pfad: {ex.Message}");
        }

        return null;
    }

    private static IEnumerable<string> AdkWinPeCandidates()
    {
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

            yield return Path.Combine(programFiles, "Windows Kits", "10", "Assessment and Deployment Kit",
                "Windows Preinstallation Environment", "amd64", "en-us", "winpe.wim");
        }
    }

    public static string? FindOscdimg()
    {
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

            string candidate = Path.Combine(programFiles, "Windows Kits", "10", "Assessment and Deployment Kit",
                "Deployment Tools", "amd64", "Oscdimg", "oscdimg.exe");

            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private static string? FindFirst(params string[] candidates) =>
        candidates.FirstOrDefault(BackupFile.Exists);

    /// <summary>Begrenzte rekursive Suche - die Ablageorte haben sich zwischen Windows-Fassungen verschoben.</summary>
    private static string? SearchUnder(string root, string fileName)
    {
        if (!Directory.Exists(root))
        {
            return null;
        }

        try
        {
            return Directory.EnumerateFiles(root, fileName, new EnumerationOptions
            {
                RecurseSubdirectories = true,
                MaxRecursionDepth = 4,
                IgnoreInaccessible = true,
            }).FirstOrDefault();
        }
        catch (Exception ex)
        {
            Log.Debug($"Suche nach {fileName} unter {root} fehlgeschlagen: {ex.Message}");
            return null;
        }
    }
}
