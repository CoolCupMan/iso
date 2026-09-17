using System.Security.Cryptography;
using System.Text;
using IsoForge.Core;
using IsoForge.Media;

namespace IsoForge;

/// <summary>
/// Prueft den eingebauten ISO-Schreiber ohne Windows-Umgebung: Es wird ein kleines Medium gebaut,
/// wieder eingelesen und Byte fuer Byte mit den Ausgangsdaten verglichen. Die Bauumgebung ruft das auf.
/// </summary>
public static class SelfTest
{
    public static int Run(string workDirectory)
    {
        Program.PrintBanner();
        Console.WriteLine($"  Arbeitsverzeichnis: {workDirectory}");
        Console.WriteLine();

        string staging = Path.Combine(workDirectory, "staging");
        string isoPath = Path.Combine(workDirectory, "selftest.iso");

        if (Directory.Exists(workDirectory))
        {
            Directory.Delete(workDirectory, recursive: true);
        }

        Directory.CreateDirectory(staging);

        Dictionary<string, byte[]> expected = CreateSampleTree(staging);

        byte[] biosBootImage = RandomNumberGenerator.GetBytes(2048);
        byte[] efiPayload = RandomNumberGenerator.GetBytes(180 * 1024);
        byte[] efiBootImage = FatImageBuilder.CreateEfiBootImage(efiPayload);

        IsoImageOptions options = new()
        {
            VolumeLabel = "ISOFORGE_SELFTEST",
            BiosBootImage = biosBootImage,
            EfiBootImage = efiBootImage,
            Timestamp = new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero),
        };

        IsoDirectory root = IsoDirectory.FromDisk(staging);

        using (FileStream output = new(isoPath, FileMode.Create, FileAccess.ReadWrite))
        {
            IsoImageWriter.Write(root, options, output);
        }

        int failures = 0;
        using FileStream stream = File.OpenRead(isoPath);
        IsoImageInfo info = IsoImageReader.Read(stream);

        failures += Check("Datentraegerbezeichnung", info.VolumeLabel == "ISOFORGE_SELFTEST", info.VolumeLabel);
        failures += Check("Joliet vorhanden", info.HasJoliet, info.HasJoliet.ToString());
        failures += Check("Groesse stimmt mit dem Volume Descriptor ueberein",
            stream.Length == (long)info.TotalSectors * IsoLayout.SectorSize,
            $"{stream.Length} vs {(long)info.TotalSectors * IsoLayout.SectorSize}");

        failures += Check("Zwei Starteintraege", info.BootEntries.Count == 2, info.BootEntries.Count.ToString());
        failures += Check("BIOS-Starteintrag",
            info.BootEntries.Any(e => e.PlatformId == 0x00 && e.Bootable), "-");
        failures += Check("UEFI-Starteintrag",
            info.BootEntries.Any(e => e.PlatformId == 0xEF && e.Bootable), "-");

        foreach (IsoBootEntry entry in info.BootEntries)
        {
            byte[] reference = entry.PlatformId == 0xEF ? efiBootImage : biosBootImage;
            stream.Seek((long)entry.LoadRba * IsoLayout.SectorSize, SeekOrigin.Begin);
            byte[] actual = new byte[reference.Length];
            stream.ReadExactly(actual);
            failures += Check($"Startabbild 0x{entry.PlatformId:X2} unveraendert",
                actual.AsSpan().SequenceEqual(reference), "-");
        }

        foreach ((string path, byte[] content) in expected)
        {
            IsoEntry? entry = info.Entries.FirstOrDefault(e =>
                !e.IsDirectory && string.Equals(e.Path, path, StringComparison.Ordinal));

            if (entry is null)
            {
                failures += Check($"Datei {path}", false, "fehlt im Abbild");
                continue;
            }

            byte[] actual = IsoImageReader.ReadFile(stream, entry);
            failures += Check($"Datei {path}", actual.AsSpan().SequenceEqual(content), $"{actual.Length} Bytes");
        }

        failures += Check("FAT-Abbild traegt eine gueltige Startsignatur",
            efiBootImage[510] == 0x55 && efiBootImage[511] == 0xAA, "-");

        Console.WriteLine();
        if (failures == 0)
        {
            Console.WriteLine("  Alle Pruefungen bestanden.");
            Console.WriteLine($"  Erzeugtes Abbild: {isoPath} ({Format(new FileInfo(isoPath).Length)})");
            Console.WriteLine();
            return 0;
        }

        Console.WriteLine($"  {failures} Pruefung(en) fehlgeschlagen.");
        Console.WriteLine();
        return 1;
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

    private static string Format(long value) => Capture.Format.Bytes(value);
}
