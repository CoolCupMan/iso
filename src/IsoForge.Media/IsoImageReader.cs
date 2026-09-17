using System.Text;

namespace IsoForge.Media;

public sealed record IsoEntry(string Path, bool IsDirectory, uint Extent, long Length);

public sealed record IsoBootEntry(byte PlatformId, bool Bootable, ushort SectorCount, uint LoadRba);

public sealed record IsoImageInfo(
    string VolumeLabel,
    uint TotalSectors,
    bool HasJoliet,
    uint BootCatalogLba,
    IReadOnlyList<IsoBootEntry> BootEntries,
    IReadOnlyList<IsoEntry> Entries);

/// <summary>
/// Liest ein ISO-9660-Abbild so weit, wie es fuer Pruefungen noetig ist: Volume Descriptors, Verzeichnisbaum,
/// El-Torito-Katalog und Dateiinhalte.
/// </summary>
public static class IsoImageReader
{
    public static IsoImageInfo Read(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);

        byte[] pvd = ReadSector(stream, 16);
        if (pvd[0] != 1 || Encoding.ASCII.GetString(pvd, 1, 5) != "CD001")
        {
            throw new InvalidDataException("Kein gueltiger Primary Volume Descriptor an Sektor 16.");
        }

        string label = Encoding.ASCII.GetString(pvd, 40, 32).TrimEnd();
        uint totalSectors = IsoLayout.ReadUInt32Le(pvd.AsSpan(80));

        uint bootCatalogLba = 0;
        bool hasJoliet = false;
        uint jolietRootExtent = 0;
        uint jolietRootLength = 0;

        for (uint sectorIndex = 17; sectorIndex < 64; sectorIndex++)
        {
            byte[] descriptor = ReadSector(stream, sectorIndex);
            if (Encoding.ASCII.GetString(descriptor, 1, 5) != "CD001")
            {
                break;
            }

            switch (descriptor[0])
            {
                case 0xFF:
                    sectorIndex = 64;
                    break;
                case 0x00:
                    bootCatalogLba = IsoLayout.ReadUInt32Le(descriptor.AsSpan(71));
                    break;
                case 0x02 when descriptor[88] == 0x25 && descriptor[89] == 0x2F:
                    hasJoliet = true;
                    jolietRootExtent = IsoLayout.ReadUInt32Le(descriptor.AsSpan(156 + 2));
                    jolietRootLength = IsoLayout.ReadUInt32Le(descriptor.AsSpan(156 + 10));
                    break;
            }
        }

        List<IsoBootEntry> bootEntries = new();
        if (bootCatalogLba != 0)
        {
            byte[] catalog = ReadSector(stream, bootCatalogLba);
            if (catalog[0] == 0x01 && catalog[30] == 0x55 && catalog[31] == 0xAA)
            {
                byte platform = catalog[1];
                AddEntry(catalog.AsSpan(32, 32), platform);

                int offset = 64;
                while (offset + 64 <= IsoLayout.SectorSize && (catalog[offset] == 0x90 || catalog[offset] == 0x91))
                {
                    byte sectionPlatform = catalog[offset + 1];
                    int sectionEntryCount = IsoLayout.ReadUInt16Le(catalog.AsSpan(offset + 2));
                    bool final = catalog[offset] == 0x91;
                    offset += 32;
                    for (int i = 0; i < sectionEntryCount && offset + 32 <= IsoLayout.SectorSize; i++, offset += 32)
                    {
                        AddEntry(catalog.AsSpan(offset, 32), sectionPlatform);
                    }

                    if (final)
                    {
                        break;
                    }
                }

                void AddEntry(ReadOnlySpan<byte> entry, byte platformId) => bootEntries.Add(new IsoBootEntry(
                    platformId,
                    entry[0] == 0x88,
                    IsoLayout.ReadUInt16Le(entry[6..]),
                    IsoLayout.ReadUInt32Le(entry[8..])));
            }
        }

        uint rootExtent = IsoLayout.ReadUInt32Le(pvd.AsSpan(156 + 2));
        uint rootLength = IsoLayout.ReadUInt32Le(pvd.AsSpan(156 + 10));

        List<IsoEntry> entries = new();
        if (hasJoliet)
        {
            Walk(stream, jolietRootExtent, jolietRootLength, string.Empty, joliet: true, entries);
        }
        else
        {
            Walk(stream, rootExtent, rootLength, string.Empty, joliet: false, entries);
        }

        return new IsoImageInfo(label, totalSectors, hasJoliet, bootCatalogLba, bootEntries, entries);
    }

    /// <summary>Liest den Inhalt einer Datei anhand des Ergebnisses von <see cref="Read"/>.</summary>
    public static byte[] ReadFile(Stream stream, IsoEntry entry)
    {
        stream.Seek((long)entry.Extent * IsoLayout.SectorSize, SeekOrigin.Begin);
        byte[] buffer = new byte[entry.Length];
        ReadExactly(stream, buffer);
        return buffer;
    }

    private static void Walk(Stream stream, uint extent, uint length, string prefix, bool joliet, List<IsoEntry> sink)
    {
        byte[] data = new byte[length];
        stream.Seek((long)extent * IsoLayout.SectorSize, SeekOrigin.Begin);
        ReadExactly(stream, data);

        int offset = 0;
        while (offset < data.Length)
        {
            byte recordLength = data[offset];
            if (recordLength == 0)
            {
                // Rest des Sektors ist Fuellmaterial - beim naechsten Sektor weitermachen.
                offset = ((offset / IsoLayout.SectorSize) + 1) * IsoLayout.SectorSize;
                continue;
            }

            int nameLength = data[offset + 32];
            byte flags = data[offset + 25];
            uint childExtent = IsoLayout.ReadUInt32Le(data.AsSpan(offset + 2));
            uint childLength = IsoLayout.ReadUInt32Le(data.AsSpan(offset + 10));

            if (!(nameLength == 1 && (data[offset + 33] == 0x00 || data[offset + 33] == 0x01)))
            {
                string name = joliet
                    ? DecodeUcs2(data.AsSpan(offset + 33, nameLength))
                    : Encoding.ASCII.GetString(data, offset + 33, nameLength);

                bool isDirectory = (flags & 0x02) != 0;
                if (!isDirectory)
                {
                    int semicolon = name.LastIndexOf(';');
                    if (semicolon >= 0)
                    {
                        name = name[..semicolon];
                    }
                }

                string path = prefix.Length == 0 ? name : prefix + "/" + name;
                sink.Add(new IsoEntry(path, isDirectory, childExtent, childLength));

                if (isDirectory)
                {
                    Walk(stream, childExtent, childLength, path, joliet, sink);
                }
            }

            offset += recordLength;
        }
    }

    private static string DecodeUcs2(ReadOnlySpan<byte> bytes)
    {
        StringBuilder builder = new(bytes.Length / 2);
        for (int i = 0; i + 1 < bytes.Length; i += 2)
        {
            builder.Append((char)((bytes[i] << 8) | bytes[i + 1]));
        }

        return builder.ToString();
    }

    private static byte[] ReadSector(Stream stream, uint index)
    {
        stream.Seek((long)index * IsoLayout.SectorSize, SeekOrigin.Begin);
        byte[] buffer = new byte[IsoLayout.SectorSize];
        ReadExactly(stream, buffer);
        return buffer;
    }

    private static void ReadExactly(Stream stream, byte[] buffer)
    {
        int read = 0;
        while (read < buffer.Length)
        {
            int chunk = stream.Read(buffer, read, buffer.Length - read);
            if (chunk <= 0)
            {
                throw new EndOfStreamException("Abbild endet frueher als erwartet.");
            }

            read += chunk;
        }
    }
}
