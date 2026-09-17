using System.Security.Cryptography;
using System.Text;
using IsoForge.Core;
using IsoForge.Media;

namespace IsoForge;

/// <summary>
/// Prueft die ISO-Erzeugung, ohne dass dafuer gesichert werden muss: Es wird ein kleines Medium gebaut,
/// wieder eingelesen und Byte fuer Byte mit den Ausgangsdaten verglichen. Geprueft werden alle auf
/// diesem Rechner verfuegbaren Schreibwege. Die Bauumgebung ruft das nach jedem Build auf.
/// </summary>
public static class SelfTest
{
    public static int Run(string workDirectory)
    {
        Program.PrintBanner();
        Console.WriteLine($"  Arbeitsverzeichnis: {workDirectory}");

        string staging = Path.Combine(workDirectory, "staging");

        if (Directory.Exists(workDirectory))
        {
            Directory.Delete(workDirectory, recursive: true);
        }

        Directory.CreateDirectory(staging);
        Dictionary<string, byte[]> expected = CreateSampleTree(staging);

        byte[] biosBootImage = RandomNumberGenerator.GetBytes(2048);
        byte[] efiPayload = RandomNumberGenerator.GetBytes(180 * 1024);
        byte[] efiBootImage = FatImageBuilder.CreateEfiBootImage(efiPayload);

        int failures = 0;

        failures += Check("FAT-Abbild traegt eine gueltige Startsignatur",
            efiBootImage[510] == 0x55 && efiBootImage[511] == 0xAA, "-");

        // Joliet legt fuer die Datentraegerbezeichnung 32 Byte in UCS-2 an - mehr als 16 Zeichen zeigt
        // Windows also nicht an, und die Build-Kennung am Ende wuerde abgeschnitten.
        foreach (string tier in new[] { "full", "system", "personal", "custom" })
        {
            string label = BuildIdentity.VolumeLabel(tier);
            failures += Check($"Datentraegerbezeichnung '{label}' passt in 16 Zeichen",
                label.Length <= 16 && label.Contains(BuildIdentity.ShortId, StringComparison.Ordinal),
                $"{label.Length} Zeichen");
        }

        // --- Eingebauter Schreiber ------------------------------------------
        string builtInPath = Path.Combine(workDirectory, "selftest-builtin.iso");
        Console.WriteLine();
        Console.WriteLine("  Eingebauter Schreiber (ISO 9660 + Joliet)");
        Console.WriteLine("  ---------------------------------------------------------------------------");

        IsoImageOptions options = new()
        {
            VolumeLabel = "ISOFORGE_SELFTEST",
            BiosBootImage = biosBootImage,
            EfiBootImage = efiBootImage,
            Timestamp = new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero),
        };

        using (FileStream output = new(builtInPath, FileMode.Create, FileAccess.ReadWrite))
        {
            IsoImageWriter.Write(IsoDirectory.FromDisk(staging), options, output);
        }

        failures += Verify(builtInPath, expected, biosBootImage, efiBootImage, checkStreamLength: true);

        // --- Image Mastering API ---------------------------------------------
        Console.WriteLine();
        Console.WriteLine("  Image Mastering API (ISO 9660 + Joliet + UDF)");
        Console.WriteLine("  ---------------------------------------------------------------------------");

        if (!ImapiIsoWriter.IsAvailable())
        {
            Console.WriteLine("   uebersprungen  IMAPI2FS steht auf diesem System nicht zur Verfuegung.");
        }
        else
        {
            string imapiPath = Path.Combine(workDirectory, "selftest-imapi.iso");
            try
            {
                ImapiIsoWriter.Write(
                    staging,
                    imapiPath,
                    "ISOFORGE_SELFTEST",
                    biosBootImage,
                    efiBootImage,
                    Path.Combine(workDirectory, "bootimages"));

                // IMAPI legt die Dateien im UDF-Baum ab; der Startkatalog liegt aber unabhaengig vom
                // Dateisystem im Abbild und laesst sich deshalb genauso pruefen.
                failures += Verify(imapiPath, expected: null, biosBootImage, efiBootImage, checkStreamLength: false);
            }
            catch (Exception ex)
            {
                failures += Check("IMAPI2FS schreibt ein Abbild", false, ex.Message);
            }
        }

        Console.WriteLine();
        if (failures == 0)
        {
            Console.WriteLine("  Alle Pruefungen bestanden.");
            foreach (string file in Directory.GetFiles(workDirectory, "*.iso"))
            {
                Console.WriteLine($"   {file}  ({Capture.Format.Bytes(new FileInfo(file).Length)})");
            }

            Console.WriteLine();
            return 0;
        }

        Console.WriteLine($"  {failures} Pruefung(en) fehlgeschlagen.");
        Console.WriteLine();
        return 1;
    }

    /// <summary>
    /// Prueft ein erzeugtes Abbild. <paramref name="expected"/> bleibt leer, wenn die Dateien in einem
    /// Dateisystem liegen, das der eingebaute Leser nicht kennt - dann werden nur die Startdaten geprueft.
    /// </summary>
    private static int Verify(
        string path,
        Dictionary<string, byte[]>? expected,
        byte[] biosBootImage,
        byte[] efiBootImage,
        bool checkStreamLength)
    {
        int failures = 0;

        using FileStream stream = File.OpenRead(path);
        IsoImageInfo info = IsoImageReader.Read(stream);

        failures += Check("Datentraegerbezeichnung", info.VolumeLabel.StartsWith("ISOFORGE", StringComparison.Ordinal), info.VolumeLabel);

        if (checkStreamLength)
        {
            failures += Check("Groesse stimmt mit dem Volume Descriptor ueberein",
                stream.Length == (long)info.TotalSectors * IsoLayout.SectorSize,
                $"{stream.Length} statt {(long)info.TotalSectors * IsoLayout.SectorSize}");
        }

        failures += Check("Startkatalog vorhanden", info.BootCatalogLba != 0, "-");
        failures += Check("Zwei Starteintraege", info.BootEntries.Count == 2, info.BootEntries.Count.ToString());
        failures += Check("BIOS-Starteintrag", info.BootEntries.Any(e => e.PlatformId == 0x00 && e.Bootable), "-");
        failures += Check("UEFI-Starteintrag", info.BootEntries.Any(e => e.PlatformId == 0xEF && e.Bootable), "-");

        foreach (IsoBootEntry entry in info.BootEntries)
        {
            byte[] reference = entry.PlatformId == 0xEF ? efiBootImage : biosBootImage;
            stream.Seek((long)entry.LoadRba * IsoLayout.SectorSize, SeekOrigin.Begin);
            byte[] actual = new byte[reference.Length];
            stream.ReadExactly(actual);
            failures += Check($"Startabbild 0x{entry.PlatformId:X2} unveraendert",
                actual.AsSpan().SequenceEqual(reference), "-");
        }

        if (expected is null)
        {
            Console.WriteLine("   ok     Dateipruefung uebernimmt die Bauumgebung ueber Mount-DiskImage");
            return failures;
        }

        int mismatches = 0;
        foreach ((string relativePath, byte[] content) in expected)
        {
            IsoEntry? entry = info.Entries.FirstOrDefault(e =>
                !e.IsDirectory && string.Equals(e.Path, relativePath, StringComparison.Ordinal));

            if (entry is null || !IsoImageReader.ReadFile(stream, entry).AsSpan().SequenceEqual(content))
            {
                Console.WriteLine($"   FEHLER {relativePath}");
                mismatches++;
            }
        }

        failures += Check($"{expected.Count} Dateien unveraendert zurueckgelesen", mismatches == 0, $"{mismatches} abweichend");
        return failures;
    }

    private static Dictionary<string, byte[]> CreateSampleTree(string staging)
    {
        Dictionary<string, byte[]> expected = new(StringComparer.Ordinal);

        void Add(string relativePath, byte[] content)
        {
            string full = Path.Combine(staging, relativePath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllBytes(full, content);
            expected[relativePath] = content;
        }

        Add("bootmgr", RandomNumberGenerator.GetBytes(64 * 1024));
        Add("boot/boot.sdi", RandomNumberGenerator.GetBytes(3 * 1024 * 1024 + 17));
        Add("boot/bcd", RandomNumberGenerator.GetBytes(28 * 1024));
        Add("efi/boot/bootx64.efi", RandomNumberGenerator.GetBytes(1_500_000));
        Add("efi/microsoft/boot/bcd", RandomNumberGenerator.GetBytes(28 * 1024));
        Add("sources/boot.wim", RandomNumberGenerator.GetBytes(5 * 1024 * 1024));
        Add("sources/install.swm", RandomNumberGenerator.GetBytes(2 * 1024 * 1024));
        Add("sources/install2.swm", RandomNumberGenerator.GetBytes(2_048));
        Add("IsoForge/media.cmd", Encoding.ASCII.GetBytes("set \"IF_BUILDID=SELFTEST\"\r\n"));
        Add("IsoForge/LIESMICH.txt", Encoding.UTF8.GetBytes("Selbsttest"));

        // Ein Verzeichnis mit vielen Eintraegen: dabei laeuft ein Verzeichnis-Extent ueber mehrere Sektoren.
        for (int i = 0; i < 120; i++)
        {
            Add($"drivers/treiber-{i:000}.inf", Encoding.ASCII.GetBytes($"; Treiber {i}\r\n"));
        }

        return expected;
    }

    private static int Check(string description, bool condition, string detail)
    {
        Console.WriteLine($"   {(condition ? "ok    " : "FEHLER")} {description}{(condition ? string.Empty : $"  ({detail})")}");
        return condition ? 0 : 1;
    }
}
