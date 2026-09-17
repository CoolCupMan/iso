using Microsoft.Win32;
using IsoForge.Core;

namespace IsoForge;

/// <summary>
/// Installiert genau diesen Stand in ein eigenes, nach der Build-Kennung benanntes Verzeichnis und
/// traegt ihn als eigenen Eintrag in die Softwareliste ein. Spaetere Fassungen legen sich daneben,
/// statt die vorhandene zu ersetzen.
/// </summary>
public static class Installer
{
    private const string UninstallRoot = @"Software\Microsoft\Windows\CurrentVersion\Uninstall";

    private static string RegistryKeyName => $"IsoForge_{BuildIdentity.ShortId}";

    public static int Install(string? directoryOverride)
    {
        Program.PrintBanner();

        if (!Preflight.IsElevated())
        {
            throw new UnauthorizedAccessException("Fuer die Installation werden Administratorrechte gebraucht.");
        }

        string source = Environment.ProcessPath
                        ?? throw new InvalidOperationException("Der Pfad der laufenden Programmdatei ist unbekannt.");

        string target = directoryOverride ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "IsoForge", BuildIdentity.ShortId);

        Directory.CreateDirectory(target);
        string targetExe = Path.Combine(target, "IsoForge.exe");

        if (string.Equals(Path.GetFullPath(source), Path.GetFullPath(targetExe), StringComparison.OrdinalIgnoreCase))
        {
            Log.Info("Dieser Stand ist bereits an dieser Stelle installiert.");
            return 0;
        }

        File.Copy(source, targetExe, overwrite: true);
        Log.Info($"Programmdatei kopiert nach {targetExe}");

        using RegistryKey key = Registry.LocalMachine.CreateSubKey($@"{UninstallRoot}\{RegistryKeyName}");
        key.SetValue("DisplayName", $"IsoForge {BuildIdentity.Version} ({BuildIdentity.ShortId})");
        key.SetValue("DisplayVersion", BuildIdentity.Version);
        key.SetValue("Publisher", "IsoForge");
        key.SetValue("InstallLocation", target);
        key.SetValue("DisplayIcon", targetExe);
        key.SetValue("UninstallString", $"\"{targetExe}\" uninstall");
        key.SetValue("NoModify", 1, RegistryValueKind.DWord);
        key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
        key.SetValue("InstallDate", DateTime.Now.ToString("yyyyMMdd"));
        key.SetValue("IsoForgeBuildId", BuildIdentity.BuildId.ToString());

        Log.Info($"In der Softwareliste eingetragen als '{BuildIdentity.ProductInstance}'.");
        Console.WriteLine();
        Console.WriteLine($"  Installiert unter : {target}");
        Console.WriteLine($"  Aufruf            : \"{targetExe}\"");
        Console.WriteLine();
        Console.WriteLine("  Weitere Staende mit anderer Build-Kennung landen in einem eigenen Verzeichnis");
        Console.WriteLine("  und stoeren diese Installation nicht.");
        Console.WriteLine();

        ListInstalled();
        return 0;
    }

    public static int Uninstall()
    {
        Program.PrintBanner();

        if (!Preflight.IsElevated())
        {
            throw new UnauthorizedAccessException("Fuer die Deinstallation werden Administratorrechte gebraucht.");
        }

        using (RegistryKey? root = Registry.LocalMachine.OpenSubKey(UninstallRoot, writable: true))
        {
            if (root?.OpenSubKey(RegistryKeyName) is not null)
            {
                root.DeleteSubKeyTree(RegistryKeyName);
                Log.Info("Eintrag aus der Softwareliste entfernt.");
            }
            else
            {
                Log.Warn("Es gab keinen Eintrag in der Softwareliste.");
            }
        }

        string? processPath = Environment.ProcessPath;
        if (processPath is not null)
        {
            Console.WriteLine();
            Console.WriteLine($"  Die Programmdatei liegt noch unter {processPath}.");
            Console.WriteLine("  Eine laufende Programmdatei kann sich nicht selbst loeschen - bitte das");
            Console.WriteLine($"  Verzeichnis {Path.GetDirectoryName(processPath)} von Hand entfernen.");
            Console.WriteLine();
        }

        return 0;
    }

    /// <summary>Zeigt alle parallel installierten Staende.</summary>
    public static void ListInstalled()
    {
        using RegistryKey? root = Registry.LocalMachine.OpenSubKey(UninstallRoot);
        if (root is null)
        {
            return;
        }

        List<string> entries = new();
        foreach (string name in root.GetSubKeyNames().Where(n => n.StartsWith("IsoForge_", StringComparison.OrdinalIgnoreCase)))
        {
            using RegistryKey? key = root.OpenSubKey(name);
            if (key?.GetValue("DisplayName") is string displayName)
            {
                entries.Add($"   {displayName,-40} {key.GetValue("InstallLocation")}");
            }
        }

        if (entries.Count <= 1)
        {
            return;
        }

        Console.WriteLine("  Installierte Staende");
        Console.WriteLine("  ---------------------------------------------------------------------------");
        entries.ForEach(Console.WriteLine);
        Console.WriteLine();
    }
}
