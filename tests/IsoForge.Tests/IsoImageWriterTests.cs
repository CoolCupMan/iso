using System.Security.Cryptography;
using System.Text;
using IsoForge.Media;
using Xunit;

namespace IsoForge.Tests;

public sealed class IsoImageWriterTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "isoforge-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private string Staging
    {
        get
        {
            string path = Path.Combine(_root, "staging");
            Directory.CreateDirectory(path);
            return path;
        }
    }

    private static void Write(string staging, string relativePath, byte[] content)
    {
        string full = Path.Combine(staging, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllBytes(full, content);
    }

    private static MemoryStream Build(string staging, IsoImageOptions options)
    {
        IsoDirectory root = IsoDirectory.FromDisk(staging);
        MemoryStream stream = new();
        IsoImageWriter.Write(root, options, stream);
        stream.Position = 0;
        return stream;
    }

    [Fact]
    public void RoundTrip_PreservesFileContents()
    {
        string staging = Staging;
        Dictionary<string, byte[]> expected = new(StringComparer.Ordinal)
        {
            ["bootmgr"] = RandomNumberGenerator.GetBytes(40_000),
            ["boot/boot.sdi"] = RandomNumberGenerator.GetBytes(3_000_017),
            ["efi/microsoft/boot/bcd"] = RandomNumberGenerator.GetBytes(28_672),
            ["sources/install.swm"] = RandomNumberGenerator.GetBytes(1_048_576),
            ["IsoForge/media.cmd"] = Encoding.ASCII.GetBytes("set \"IF_BUILDID=TEST\"\r\n"),
        };

        foreach ((string path, byte[] content) in expected)
        {
            Write(staging, path, content);
        }

        using MemoryStream iso = Build(staging, new IsoImageOptions { VolumeLabel = "TESTLABEL" });
        IsoImageInfo info = IsoImageReader.Read(iso);

        Assert.Equal("TESTLABEL", info.VolumeLabel);
        Assert.True(info.HasJoliet);

        foreach ((string path, byte[] content) in expected)
        {
            IsoEntry entry = Assert.Single(info.Entries.Where(e => !e.IsDirectory && e.Path == path));
            Assert.Equal(content.Length, entry.Length);
            Assert.Equal(content, IsoImageReader.ReadFile(iso, entry));
        }
    }

    [Fact]
    public void TotalSectors_MatchesStreamLength()
    {
        string staging = Staging;
        Write(staging, "a.bin", RandomNumberGenerator.GetBytes(5000));
        Write(staging, "dir/b.bin", RandomNumberGenerator.GetBytes(1));

        using MemoryStream iso = Build(staging, new IsoImageOptions());
        IsoImageInfo info = IsoImageReader.Read(iso);

        Assert.Equal(iso.Length, (long)info.TotalSectors * IsoLayout.SectorSize);
    }

    [Fact]
    public void ElTorito_ContainsBiosAndUefiEntries()
    {
        string staging = Staging;
        Write(staging, "readme.txt", "x"u8.ToArray());

        byte[] bios = RandomNumberGenerator.GetBytes(2048);
        byte[] efi = FatImageBuilder.CreateEfiBootImage(RandomNumberGenerator.GetBytes(200_000));

        using MemoryStream iso = Build(staging, new IsoImageOptions { BiosBootImage = bios, EfiBootImage = efi });
        IsoImageInfo info = IsoImageReader.Read(iso);

        Assert.NotEqual(0u, info.BootCatalogLba);
        Assert.Equal(2, info.BootEntries.Count);

        IsoBootEntry biosEntry = Assert.Single(info.BootEntries.Where(e => e.PlatformId == 0x00));
        IsoBootEntry efiEntry = Assert.Single(info.BootEntries.Where(e => e.PlatformId == 0xEF));
        Assert.True(biosEntry.Bootable);
        Assert.True(efiEntry.Bootable);

        // Die Startabbilder muessen an den angegebenen Stellen unveraendert liegen.
        Assert.Equal(bios, ReadAt(iso, biosEntry.LoadRba, bios.Length));
        Assert.Equal(efi, ReadAt(iso, efiEntry.LoadRba, efi.Length));

        // Die Sektorzahl im Katalog zaehlt virtuelle 512-Byte-Sektoren.
        Assert.Equal((ushort)((efi.Length + 511) / 512), efiEntry.SectorCount);
    }

    [Fact]
    public void LargeDirectory_SpansMultipleSectorsAndStaysReadable()
    {
        string staging = Staging;
        for (int i = 0; i < 400; i++)
        {
            Write(staging, $"drivers/treiber-{i:0000}.inf", Encoding.ASCII.GetBytes($"; {i}"));
        }

        using MemoryStream iso = Build(staging, new IsoImageOptions());
        IsoImageInfo info = IsoImageReader.Read(iso);

        IsoEntry directory = Assert.Single(info.Entries.Where(e => e.IsDirectory && e.Path == "drivers"));
        Assert.True(directory.Length > IsoLayout.SectorSize, "Das Verzeichnis sollte mehr als einen Sektor belegen.");
        Assert.Equal(0, directory.Length % IsoLayout.SectorSize);
        Assert.Equal(400, info.Entries.Count(e => !e.IsDirectory && e.Path.StartsWith("drivers/", StringComparison.Ordinal)));
    }

    [Fact]
    public void DirectoryRecords_NeverCrossSectorBoundaries()
    {
        string staging = Staging;
        for (int i = 0; i < 90; i++)
        {
            // Lange Namen erzwingen Joliet-Records nahe der Sektorgrenze.
            Write(staging, $"lang/{new string('n', 50)}-{i:00}.dat", new byte[] { 1 });
        }

        using MemoryStream iso = Build(staging, new IsoImageOptions());
        IsoImageInfo info = IsoImageReader.Read(iso);

        IsoEntry directory = Assert.Single(info.Entries.Where(e => e.IsDirectory && e.Path == "lang"));

        byte[] extent = new byte[directory.Length];
        iso.Seek((long)directory.Extent * IsoLayout.SectorSize, SeekOrigin.Begin);
        iso.ReadExactly(extent);

        int offset = 0;
        while (offset < extent.Length)
        {
            byte length = extent[offset];
            if (length == 0)
            {
                offset = ((offset / IsoLayout.SectorSize) + 1) * IsoLayout.SectorSize;
                continue;
            }

            int withinSector = offset % IsoLayout.SectorSize;
            Assert.True(withinSector + length <= IsoLayout.SectorSize,
                $"Ein Directory Record bei Offset {offset} laeuft ueber die Sektorgrenze.");
            offset += length;
        }
    }

    [Fact]
    public void FilesLargerThanFourGigabytes_AreRejectedWithGuidance()
    {
        IsoDirectory root = new(string.Empty);
        root.AddFile(new IsoFile("riesig.wim", 5L * 1024 * 1024 * 1024, _ => { }));

        NotSupportedException error = Assert.Throws<NotSupportedException>(
            () => IsoImageWriter.Write(root, new IsoImageOptions(), new MemoryStream()));

        Assert.Contains("4 GiB", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void LongAndCollidingNames_StayUniqueInBothHierarchies()
    {
        string staging = Staging;
        Write(staging, "ein sehr langer dateiname mit leerzeichen.txt", "a"u8.ToArray());
        Write(staging, "ein sehr langer dateiname mit umlauten.txt", "b"u8.ToArray());
        Write(staging, "Grosse Datei.DAT", "c"u8.ToArray());

        using MemoryStream iso = Build(staging, new IsoImageOptions());
        IsoImageInfo info = IsoImageReader.Read(iso);

        // Der Joliet-Baum traegt die Originalnamen.
        Assert.Contains(info.Entries, e => e.Path == "ein sehr langer dateiname mit leerzeichen.txt");
        Assert.Contains(info.Entries, e => e.Path == "ein sehr langer dateiname mit umlauten.txt");
        Assert.Contains(info.Entries, e => e.Path == "Grosse Datei.DAT");

        List<string> names = info.Entries.Where(e => !e.IsDirectory).Select(e => e.Path).ToList();
        Assert.Equal(names.Count, names.Distinct(StringComparer.Ordinal).Count());
    }

    private static byte[] ReadAt(Stream stream, uint lba, int length)
    {
        stream.Seek((long)lba * IsoLayout.SectorSize, SeekOrigin.Begin);
        byte[] buffer = new byte[length];
        stream.ReadExactly(buffer);
        return buffer;
    }
}
