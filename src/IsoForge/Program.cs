using System.Text;
using IsoForge.Capture;
using IsoForge.Core;
using IsoForge.Media;

namespace IsoForge;

public static class Program
{
    public static int Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        Console.Title = $"IsoForge {BuildIdentity.Version} ({BuildIdentity.ShortId})";

        CommandLine commandLine = CommandLine.Parse(args);

        if (commandLine.Has("debug"))
        {
            Log.MinimumLevel = LogLevel.Debug;
        }

        try
        {
            return commandLine.Command switch
            {
                null or "wizard" => Wizard.Run(commandLine),
                "capture" => RunCapture(BuildOptions(commandLine)),
                "analyze" => Analyze(),
                "verify" => Verify(commandLine),
                "selftest" => SelfTest.Run(commandLine.Value("out", Path.Combine(Path.GetTempPath(), "isoforge-selftest"))),
                "install" => Installer.Install(commandLine.Value("dir")),
                "uninstall" => Installer.Uninstall(),
                "info" => Info(),
                "help" or "--help" or "-h" or "/?" => Help(),
                _ => Unknown(commandLine.Command),
            };
        }
        catch (Exception ex)
        {
            Console.WriteLine();
            Log.Error(ex.Message);
            if (Log.MinimumLevel == LogLevel.Debug)
            {
                Log.Debug(ex.ToString());
            }

            if (Log.FilePath is not null)
            {
                Log.Info($"Ausfuehrliches Protokoll: {Log.FilePath}");
            }

            return 1;
        }
        finally
        {
            Log.Close();
        }
    }

    internal static CaptureOptions BuildOptions(CommandLine commandLine)
    {
        CaptureOptions options = new()
        {
            OutputDirectory = Path.GetFullPath(commandLine.Value("output", Environment.CurrentDirectory)),
            OutputFileName = commandLine.Value("name"),
            WorkDirectory = commandLine.Value("work"),
            CustomExclusionFile = commandLine.Value("exclude-file"),
            OscdimgPath = commandLine.Value("oscdimg"),
            BootsectPath = commandLine.Value("bootsect"),
            WinPePath = commandLine.Value("winpe"),
            DriversDirectory = commandLine.Value("drivers"),
            SplitSizeMegabytes = commandLine.Value("split-size", 3800),
            KeepWorkFiles = commandLine.Has("keep-work"),
            AssumeYes = commandLine.Has("yes"),
        };

        string tierText = commandLine.Value("tier", "full");
        if (!CaptureTierInfo.TryParse(tierText, out CaptureTier tier))
        {
            throw new ArgumentException($"Unbekannte Abstufung '{tierText}'. Moeglich sind: full, system, personal, custom.");
        }

        options.Tier = tier;

        string compressionText = commandLine.Value("compression", "max").ToLowerInvariant();
        options.Compression = compressionText switch
        {
            "none" or "keine" => WimCompression.None,
            "fast" or "schnell" => WimCompression.Fast,
            "max" or "maximum" => WimCompression.Max,
            _ => throw new ArgumentException($"Unbekannte Kompression '{compressionText}'. Moeglich sind: none, fast, max."),
        };

        foreach (string volume in commandLine.List("extra-volumes"))
        {
            options.ExtraVolumes.Add(volume.TrimEnd(':', '\\').ToUpperInvariant());
        }

        if (options.Tier == CaptureTier.Custom && options.CustomExclusionFile is null)
        {
            throw new ArgumentException("Die Abstufung 'custom' braucht --exclude-file <Datei>.");
        }

        return options;
    }

    internal static int RunCapture(CaptureOptions options)
    {
        Preflight.CheckEnvironment();

        Directory.CreateDirectory(options.OutputDirectory);
        Log.OpenFile(Path.Combine(options.OutputDirectory, $"IsoForge-{BuildIdentity.ShortId}.log"));

        PrintBanner();
        Log.Info($"Abstufung   : {options.Tier.DisplayName()}");
        Log.Info($"Ziel        : {options.OutputDirectory}");
        Log.Info($"Kompression : {options.Compression}");
        if (options.ExtraVolumes.Count > 0)
        {
            Log.Info($"Zusaetzlich : {string.Join(", ", options.ExtraVolumes.Select(v => v + ":"))}");
        }

        CaptureSummary summary = new CaptureWorkflow(options).Run();

        Console.WriteLine();
        Console.WriteLine("===========================================================================");
        Console.WriteLine("  Fertig.");
        Console.WriteLine("===========================================================================");
        Console.WriteLine($"  Abbild        : {summary.IsoPath}");
        Console.WriteLine($"  Groesse       : {Format.Bytes(summary.IsoSizeBytes)}");
        Console.WriteLine($"  SHA-256       : {summary.Sha256}");
        Console.WriteLine($"  Build-Kennung : {summary.BuildId}");
        Console.WriteLine($"  Abstufung     : {summary.Tier.DisplayName()}");
        Console.WriteLine($"  Dauer         : {Format.Duration(summary.Duration)}");
        Console.WriteLine($"  Erzeugt mit   : {summary.Method}");
        Console.WriteLine();
        Console.WriteLine("  Das Abbild laesst sich unveraendert in einer virtuellen Maschine starten oder");
        Console.WriteLine("  auf einen USB-Datentraeger schreiben. Das Menue beim Start fuehrt durch die");
        Console.WriteLine("  Wiederherstellung.");
        Console.WriteLine();

        return 0;
    }

    private static int Analyze()
    {
        PrintBanner();
        Console.WriteLine("  Datentraeger dieses Rechners");
        Console.WriteLine("  ---------------------------------------------------------------------------");

        VolumeInfo system = VolumeScanner.SystemVolume();
        foreach (VolumeInfo volume in VolumeScanner.LocalVolumes())
        {
            Console.WriteLine($"   {volume}");
        }

        Console.WriteLine();
        Console.WriteLine("  Geschaetzter Platzbedarf je Abstufung");
        Console.WriteLine("  ---------------------------------------------------------------------------");

        foreach (CaptureTier tier in new[] { CaptureTier.Full, CaptureTier.System, CaptureTier.Personal })
        {
            double share = tier switch
            {
                CaptureTier.Full => 1.0,
                CaptureTier.System => 0.7,
                _ => 0.35,
            };

            long estimate = (long)(system.UsedBytes * share * 0.55);
            Console.WriteLine($"   {tier.DisplayName(),-52} ca. {Format.Bytes(estimate)}");
        }

        Console.WriteLine();
        Console.WriteLine("  Die Werte sind Schaetzungen. Wie stark sich die Daten verdichten lassen, haengt");
        Console.WriteLine("  stark vom Inhalt ab - bereits komprimierte Dateien schrumpfen kaum.");
        Console.WriteLine();

        BootFileSet bootFiles = BootFileLocator.Locate();
        Console.WriteLine("  Startdateien");
        Console.WriteLine("  ---------------------------------------------------------------------------");
        Console.WriteLine($"   BIOS-Start moeglich : {(bootFiles.CanBootBios ? "ja" : "nein")}");
        Console.WriteLine($"   UEFI-Start moeglich : {(bootFiles.CanBootUefi ? "ja" : "nein")}");
        Console.WriteLine($"   Wiederherstellungsumgebung: {BootFileLocator.LocateWinRe(null) ?? "nicht gefunden"}");
        Console.WriteLine($"   oscdimg aus dem ADK : {BootFileLocator.FindOscdimg() ?? "nicht installiert (nicht noetig)"}");
        Console.WriteLine();

        return 0;
    }

    private static int Verify(CommandLine commandLine)
    {
        string? path = commandLine.Value("iso") ?? commandLine.Positional.FirstOrDefault();
        if (path is null)
        {
            throw new ArgumentException("Verwendung: IsoForge verify --iso <Datei>");
        }

        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"'{path}' wurde nicht gefunden.");
        }

        PrintBanner();
        Console.WriteLine($"  Abbild: {path}");
        Console.WriteLine($"  Groesse: {Format.Bytes(new FileInfo(path).Length)}");
        Console.WriteLine();

        using FileStream stream = File.OpenRead(path);
        IsoImageInfo info = IsoImageReader.Read(stream);

        Console.WriteLine($"  Datentraegerbezeichnung : {info.VolumeLabel}");
        Console.WriteLine($"  Sektoren                : {info.TotalSectors}");
        Console.WriteLine($"  Joliet-Namen            : {(info.HasJoliet ? "vorhanden" : "keine")}");
        Console.WriteLine($"  Startkatalog            : {(info.BootCatalogLba == 0 ? "keiner" : "LBA " + info.BootCatalogLba)}");

        foreach (IsoBootEntry entry in info.BootEntries)
        {
            string platform = entry.PlatformId switch
            {
                0x00 => "BIOS (x86)",
                0xEF => "UEFI",
                _ => $"Plattform 0x{entry.PlatformId:X2}",
            };

            Console.WriteLine($"    - {platform,-12} startfaehig: {(entry.Bootable ? "ja" : "nein")}, " +
                              $"LBA {entry.LoadRba}, {entry.SectorCount} Sektoren");
        }

        Console.WriteLine();
        Console.WriteLine("  Wichtige Dateien");
        Console.WriteLine("  ---------------------------------------------------------------------------");

        string[] expected = { "sources/boot.wim", "boot/boot.sdi", "IsoForge/media.cmd" };
        bool complete = true;
        foreach (string name in expected)
        {
            IsoEntry? entry = info.Entries.FirstOrDefault(e =>
                string.Equals(e.Path, name, StringComparison.OrdinalIgnoreCase));

            if (entry is null)
            {
                Console.WriteLine($"   FEHLT  {name}");
                complete = false;
            }
            else
            {
                Console.WriteLine($"   ok     {name,-24} {Format.Bytes(entry.Length)}");
            }
        }

        IsoEntry? image = info.Entries.FirstOrDefault(e =>
            e.Path.StartsWith("sources/install", StringComparison.OrdinalIgnoreCase));

        if (image is null)
        {
            Console.WriteLine("   FEHLT  sources/install.wim bzw. install*.swm");
            complete = false;
        }
        else
        {
            long total = info.Entries
                .Where(e => e.Path.StartsWith("sources/install", StringComparison.OrdinalIgnoreCase))
                .Sum(e => e.Length);
            Console.WriteLine($"   ok     Systemabbild             {Format.Bytes(total)}");
        }

        string sidecar = path + ".sha256";
        if (File.Exists(sidecar))
        {
            Console.WriteLine();
            Console.WriteLine("  Pruefsumme wird geprueft ...");
            string expectedHash = File.ReadAllText(sidecar).Split(' ')[0].Trim();
            string actual = IsoBuilder.ComputeSha256(path);
            bool match = string.Equals(expectedHash, actual, StringComparison.OrdinalIgnoreCase);
            Console.WriteLine($"   {(match ? "ok" : "ABWEICHUNG")}     SHA-256 {actual}");
            complete &= match;
        }

        Console.WriteLine();
        Console.WriteLine(complete
            ? "  Das Abbild ist vollstaendig."
            : "  Das Abbild ist unvollstaendig oder beschaedigt.");
        Console.WriteLine();

        return complete ? 0 : 2;
    }

    private static int Info()
    {
        PrintBanner();
        Console.WriteLine($"  Version        : {BuildIdentity.Version}");
        Console.WriteLine($"  Build-Kennung  : {BuildIdentity.ShortId}");
        Console.WriteLine($"  Build-GUID     : {BuildIdentity.BuildId}");
        Console.WriteLine($"  Programmdatei  : {Environment.ProcessPath}");
        Console.WriteLine($"  Administrator  : {(Preflight.IsElevated() ? "ja" : "nein")}");
        Console.WriteLine();
        Console.WriteLine("  Jede erzeugte Programmdatei traegt eine eigene Build-Kennung. Sie steckt im");
        Console.WriteLine("  Installationspfad, in der Datentraegerbezeichnung des ISO und im Namen des");
        Console.WriteLine("  Starteintrags - dadurch lassen sich mehrere Staende nebeneinander betreiben.");
        Console.WriteLine();
        return 0;
    }

    private static int Unknown(string command)
    {
        Log.Error($"Unbekannter Befehl '{command}'.");
        Help();
        return 1;
    }

    private static int Help()
    {
        PrintBanner();
        Console.WriteLine("""
          Verwendung
            IsoForge.exe                        Assistent (ohne Argumente)
            IsoForge.exe capture [Optionen]     Sicherung erstellen
            IsoForge.exe analyze                Datentraeger und Platzbedarf anzeigen
            IsoForge.exe verify --iso <Datei>   Ein erzeugtes Abbild pruefen
            IsoForge.exe install [--dir <Pfad>] Diesen Stand fest installieren
            IsoForge.exe uninstall              Diesen Stand entfernen
            IsoForge.exe info                   Build-Kennung und Umgebung anzeigen

          Optionen fuer 'capture'
            --tier <full|system|personal|custom>  Abstufung (Vorgabe: full)
            --output <Verzeichnis>                Ablageort des ISO (Vorgabe: aktuelles Verzeichnis)
            --name <Dateiname>                    Abweichender Dateiname des ISO
            --compression <none|fast|max>         Verdichtung (Vorgabe: max)
            --extra-volumes D,E                   Zusaetzliche Datenlaufwerke mitsichern
            --exclude-file <Datei>                Eigene Ausschlussliste (fuer --tier custom)
            --drivers <Verzeichnis>               Treiber, die beim Zurueckspielen eingespielt werden
            --split-size <MB>                     Groesse der Teildateien (Vorgabe: 3800)
            --winpe <boot.wim>                    Eigenes Startsystem statt der Wiederherstellungsumgebung
            --bootsect <Pfad>                     bootsect.exe fuer BIOS/MBR-Ziele
            --oscdimg <Pfad>                      oscdimg.exe aus dem Windows-ADK erzwingen
            --work <Verzeichnis>                  Arbeitsverzeichnis
            --keep-work                           Arbeitsdateien nicht loeschen
            --yes                                 Rueckfragen ueberspringen
            --debug                               Ausfuehrliche Ausgabe

          Abstufungen
            full      Alles: Windows, Programme, Einstellungen und persoenliche Dateien - 1:1.
            system    Windows und Programme, ohne Dokumente, Bilder, Musik, Videos, Downloads.
            personal  Nur die persoenlichen Dateien der Benutzer; kein startfaehiges System.
            custom    Wie full, ergaenzt um eine eigene Ausschlussliste.
        """);
        Console.WriteLine();
        return 0;
    }

    internal static void PrintBanner()
    {
        Console.WriteLine();
        Console.WriteLine("===========================================================================");
        Console.WriteLine($"  IsoForge {BuildIdentity.Version}   Build {BuildIdentity.ShortId}");
        Console.WriteLine("  Live-Sicherung eines laufenden Windows als startfaehiges ISO-Abbild");
        Console.WriteLine("===========================================================================");
        Console.WriteLine();
    }
}
