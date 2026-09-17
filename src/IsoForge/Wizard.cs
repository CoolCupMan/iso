using IsoForge.Capture;
using IsoForge.Core;

namespace IsoForge;

/// <summary>Fuehrt Schritt fuer Schritt durch eine Sicherung, ohne dass man Argumente kennen muss.</summary>
public static class Wizard
{
    public static int Run(CommandLine commandLine)
    {
        Program.PrintBanner();

        if (!Preflight.IsElevated())
        {
            Log.Error("IsoForge laeuft ohne Administratorrechte. Bitte ueber 'Als Administrator ausfuehren' starten.");
            Pause();
            return 1;
        }

        Preflight.CheckEnvironment();

        VolumeInfo system = VolumeScanner.SystemVolume();
        Console.WriteLine($"  Systemlaufwerk: {system}");
        Console.WriteLine();

        CaptureOptions options = Program.BuildOptions(commandLine);
        options.Tier = AskTier(system);
        options.ExtraVolumes.Clear();
        options.ExtraVolumes.AddRange(AskExtraVolumes(system));
        options.OutputDirectory = AskOutputDirectory(system);
        options.Compression = AskCompression();

        Console.WriteLine();
        Console.WriteLine("  Zusammenfassung");
        Console.WriteLine("  ---------------------------------------------------------------------------");
        Console.WriteLine($"   Abstufung   : {options.Tier.DisplayName()}");
        Console.WriteLine($"   Quelle      : {system.DriveLetter}:" +
                          (options.ExtraVolumes.Count > 0
                              ? " sowie " + string.Join(", ", options.ExtraVolumes.Select(v => v + ":"))
                              : string.Empty));
        Console.WriteLine($"   Ziel        : {options.OutputDirectory}");
        Console.WriteLine($"   Kompression : {options.Compression}");
        Console.WriteLine($"   Platzbedarf : etwa {Format.Bytes(Preflight.EstimateRequiredBytes(system, options.ExtraVolumes))}");
        Console.WriteLine();
        Console.WriteLine("   Der Rechner kann waehrend der Sicherung normal weiterbenutzt werden. Je nach");
        Console.WriteLine("   Datenmenge dauert der Vorgang von einer halben Stunde bis zu mehreren Stunden.");
        Console.WriteLine();

        if (!Confirm("  Sicherung jetzt starten? (j/n): "))
        {
            Console.WriteLine("  Abgebrochen.");
            return 0;
        }

        int exitCode = Program.RunCapture(options);
        Pause();
        return exitCode;
    }

    private static CaptureTier AskTier(VolumeInfo system)
    {
        Console.WriteLine("  Was soll gesichert werden?");
        Console.WriteLine("  ---------------------------------------------------------------------------");

        CaptureTier[] tiers = { CaptureTier.Full, CaptureTier.System, CaptureTier.Personal };
        for (int i = 0; i < tiers.Length; i++)
        {
            double share = tiers[i] switch
            {
                CaptureTier.Full => 1.0,
                CaptureTier.System => 0.7,
                _ => 0.35,
            };

            Console.WriteLine();
            Console.WriteLine($"   [{i + 1}] {tiers[i].DisplayName()}   (etwa {Format.Bytes((long)(system.UsedBytes * share * 0.55))})");
            foreach (string line in Wrap(tiers[i].Description(), 68))
            {
                Console.WriteLine($"       {line}");
            }
        }

        Console.WriteLine();
        while (true)
        {
            Console.Write("  Auswahl [1-3]: ");
            string? input = Console.ReadLine()?.Trim();
            if (int.TryParse(input, out int index) && index >= 1 && index <= tiers.Length)
            {
                return tiers[index - 1];
            }

            Console.WriteLine("  Bitte 1, 2 oder 3 eingeben.");
        }
    }

    private static IEnumerable<string> AskExtraVolumes(VolumeInfo system)
    {
        List<VolumeInfo> candidates = VolumeScanner.LocalVolumes()
            .Where(v => !v.IsSystemVolume)
            .ToList();

        if (candidates.Count == 0)
        {
            return Array.Empty<string>();
        }

        Console.WriteLine();
        Console.WriteLine("  Weitere Datentraeger mitsichern?");
        Console.WriteLine("  ---------------------------------------------------------------------------");
        foreach (VolumeInfo volume in candidates)
        {
            Console.WriteLine($"   {volume}");
        }

        Console.WriteLine();
        Console.Write("  Laufwerksbuchstaben durch Komma getrennt (leer = keine): ");
        string? input = Console.ReadLine();

        if (string.IsNullOrWhiteSpace(input))
        {
            return Array.Empty<string>();
        }

        List<string> selected = new();
        foreach (string part in input.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string letter = part.TrimEnd(':', '\\').ToUpperInvariant();
            if (candidates.Any(v => string.Equals(v.DriveLetter, letter, StringComparison.OrdinalIgnoreCase)))
            {
                selected.Add(letter);
            }
            else
            {
                Log.Warn($"Laufwerk {letter}: wurde uebersprungen - es steht nicht zur Auswahl.");
            }
        }

        return selected;
    }

    private static string AskOutputDirectory(VolumeInfo system)
    {
        VolumeInfo? suggestion = VolumeScanner.LocalVolumes()
            .Where(v => !v.IsSystemVolume)
            .OrderByDescending(v => v.FreeBytes)
            .FirstOrDefault();

        string fallback = Path.Combine(suggestion?.Root ?? system.Root, "IsoForge");

        Console.WriteLine();
        Console.WriteLine("  Wohin soll das Abbild geschrieben werden?");
        Console.WriteLine("  ---------------------------------------------------------------------------");
        if (suggestion is not null)
        {
            Console.WriteLine($"   Vorschlag: {fallback} ({Format.Bytes(suggestion.FreeBytes)} frei)");
        }
        else
        {
            Console.WriteLine("   Es wurde kein zweiter Datentraeger gefunden. Eine externe Platte ist die");
            Console.WriteLine("   sicherere Wahl - auf dem Systemlaufwerk wird der Platz schnell knapp.");
        }

        Console.WriteLine();
        Console.Write($"  Verzeichnis (leer = {fallback}): ");
        string? input = Console.ReadLine();
        return string.IsNullOrWhiteSpace(input) ? fallback : Path.GetFullPath(input.Trim().Trim('"'));
    }

    private static WimCompression AskCompression()
    {
        Console.WriteLine();
        Console.WriteLine("  Wie stark soll verdichtet werden?");
        Console.WriteLine("  ---------------------------------------------------------------------------");
        Console.WriteLine("   [1] maximal   kleinstes Abbild, deutlich laengere Laufzeit");
        Console.WriteLine("   [2] schnell   groesseres Abbild, spuerbar schneller");
        Console.WriteLine("   [3] keine     am schnellsten, belegt den vollen Platz");
        Console.WriteLine();
        Console.Write("  Auswahl [1-3, leer = 1]: ");

        return Console.ReadLine()?.Trim() switch
        {
            "2" => WimCompression.Fast,
            "3" => WimCompression.None,
            _ => WimCompression.Max,
        };
    }

    private static bool Confirm(string prompt)
    {
        Console.Write(prompt);
        string? answer = Console.ReadLine()?.Trim().ToLowerInvariant();
        return answer is "j" or "ja" or "y" or "yes";
    }

    private static void Pause()
    {
        Console.WriteLine();
        Console.Write("  Zum Beenden die Eingabetaste druecken ...");
        Console.ReadLine();
    }

    private static IEnumerable<string> Wrap(string text, int width)
    {
        string[] words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        System.Text.StringBuilder line = new();

        foreach (string word in words)
        {
            if (line.Length > 0 && line.Length + word.Length + 1 > width)
            {
                yield return line.ToString();
                line.Clear();
            }

            if (line.Length > 0)
            {
                line.Append(' ');
            }

            line.Append(word);
        }

        if (line.Length > 0)
        {
            yield return line.ToString();
        }
    }
}
