using System.Text;

namespace IsoForge.Media;

public sealed class IsoImageOptions
{
    public string VolumeLabel { get; set; } = "ISOFORGE";

    public string SystemIdentifier { get; set; } = "WIN32";

    public string VolumeSetIdentifier { get; set; } = "ISOFORGE";

    public string PublisherIdentifier { get; set; } = "ISOFORGE";

    public string DataPreparerIdentifier { get; set; } = "ISOFORGE";

    public string ApplicationIdentifier { get; set; } = "ISOFORGE";

    public DateTimeOffset Timestamp { get; set; } = DateTimeOffset.Now;

    /// <summary>El-Torito-Abbild fuer BIOS-Start (etfsboot.com), "no emulation".</summary>
    public byte[]? BiosBootImage { get; set; }

    /// <summary>El-Torito-Abbild fuer UEFI-Start: ein FAT-Dateisystem mit \EFI\BOOT\BOOTX64.EFI.</summary>
    public byte[]? EfiBootImage { get; set; }

    public Action<long, long>? Progress { get; set; }
}

public sealed record IsoWriteResult(long TotalBytes, uint TotalSectors, uint BootCatalogLba);

/// <summary>
/// Schreibt ein ISO-9660-Abbild (Level 2) mit Joliet-Erweiterung und optional zwei El-Torito-Eintraegen
/// (BIOS und UEFI). Damit entsteht ein Medium, das auf klassischen wie modernen Systemen startet, ohne
/// dass das Windows-ADK mit oscdimg.exe vorhanden sein muss.
/// </summary>
public static class IsoImageWriter
{
    private const int BootCatalogSectors = 1;

    public static IsoWriteResult Write(IsoDirectory root, IsoImageOptions options, Stream output)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(output);

        foreach (IsoFile file in root.EnumerateFiles())
        {
            if (file.Length > IsoLayout.MaxFileSize)
            {
                throw new NotSupportedException(
                    $"'{file.Name}' ist {file.Length / (1024 * 1024)} MB gross. ISO 9660 kann hoechstens 4 GiB je Datei " +
                    "adressieren - das Abbild muss vorher aufgeteilt werden (z. B. DISM /Split-Image).");
            }
        }

        IsoNaming.Assign(root);

        List<IsoDirectory> directories = root.EnumerateBreadthFirst().ToList();
        for (int i = 0; i < directories.Count; i++)
        {
            directories[i].PathTableIndex = i + 1;
        }

        bool hasBoot = options.BiosBootImage is not null || options.EfiBootImage is not null;

        // --- Groessen berechnen -------------------------------------------------
        foreach (IsoDirectory dir in directories)
        {
            dir.Length = (uint)MeasureDirectoryExtent(dir, joliet: false);
            dir.JolietLength = (uint)MeasureDirectoryExtent(dir, joliet: true);
        }

        uint pathTableSize = (uint)MeasurePathTable(directories, joliet: false);
        uint jolietPathTableSize = (uint)MeasurePathTable(directories, joliet: true);

        // --- Extents vergeben ---------------------------------------------------
        uint lba = IsoLayout.SystemAreaSectors;
        uint pvdLba = lba++;
        uint bootRecordLba = 0;
        if (hasBoot)
        {
            bootRecordLba = lba++;
        }

        uint svdLba = lba++;
        uint terminatorLba = lba++;

        uint bootCatalogLba = 0;
        if (hasBoot)
        {
            bootCatalogLba = lba;
            lba += BootCatalogSectors;
        }

        uint biosBootLba = 0;
        if (options.BiosBootImage is not null)
        {
            biosBootLba = lba;
            lba += (uint)IsoLayout.SectorsFor(options.BiosBootImage.Length);
        }

        uint efiBootLba = 0;
        if (options.EfiBootImage is not null)
        {
            efiBootLba = lba;
            lba += (uint)IsoLayout.SectorsFor(options.EfiBootImage.Length);
        }

        uint pathTableLLba = lba;
        lba += (uint)IsoLayout.SectorsFor(pathTableSize);
        uint pathTableMLba = lba;
        lba += (uint)IsoLayout.SectorsFor(pathTableSize);
        uint jolietPathTableLLba = lba;
        lba += (uint)IsoLayout.SectorsFor(jolietPathTableSize);
        uint jolietPathTableMLba = lba;
        lba += (uint)IsoLayout.SectorsFor(jolietPathTableSize);

        foreach (IsoDirectory dir in directories)
        {
            dir.Extent = lba;
            lba += (uint)IsoLayout.SectorsFor(dir.Length);
        }

        foreach (IsoDirectory dir in directories)
        {
            dir.JolietExtent = lba;
            lba += (uint)IsoLayout.SectorsFor(dir.JolietLength);
        }

        List<IsoFile> files = directories.SelectMany(d => d.Files).ToList();
        foreach (IsoFile file in files)
        {
            file.Extent = lba;
            lba += (uint)IsoLayout.SectorsFor(file.Length);
        }

        uint totalSectors = lba;

        // --- Schreiben ----------------------------------------------------------
        byte[] sector = new byte[IsoLayout.SectorSize];

        WriteZeroSectors(output, IsoLayout.SystemAreaSectors);

        WriteVolumeDescriptor(output, sector, type: 1, options, root, totalSectors, pathTableSize,
            pathTableLLba, pathTableMLba, joliet: false);

        if (hasBoot)
        {
            WriteBootRecord(output, sector, bootCatalogLba);
        }

        WriteVolumeDescriptor(output, sector, type: 2, options, root, totalSectors, jolietPathTableSize,
            jolietPathTableLLba, jolietPathTableMLba, joliet: true);

        WriteTerminator(output, sector);

        if (hasBoot)
        {
            WriteBootCatalog(output, sector, options, biosBootLba, efiBootLba);
            WritePadded(output, options.BiosBootImage);
            WritePadded(output, options.EfiBootImage);
        }

        WritePathTable(output, directories, pathTableSize, joliet: false, bigEndian: false);
        WritePathTable(output, directories, pathTableSize, joliet: false, bigEndian: true);
        WritePathTable(output, directories, jolietPathTableSize, joliet: true, bigEndian: false);
        WritePathTable(output, directories, jolietPathTableSize, joliet: true, bigEndian: true);

        foreach (IsoDirectory dir in directories)
        {
            WriteDirectoryExtent(output, dir, options.Timestamp, joliet: false);
        }

        foreach (IsoDirectory dir in directories)
        {
            WriteDirectoryExtent(output, dir, options.Timestamp, joliet: true);
        }

        long totalFileBytes = files.Sum(f => f.Length);
        long writtenFileBytes = 0;
        foreach (IsoFile file in files)
        {
            file.CopyTo(output);
            PadToSector(output, file.Length);
            writtenFileBytes += file.Length;
            options.Progress?.Invoke(writtenFileBytes, totalFileBytes);
        }

        output.Flush();
        long totalBytes = (long)totalSectors * IsoLayout.SectorSize;

        // Die Datei muss exakt so lang sein, wie der PVD behauptet; das letzte Extent kann kuerzer sein.
        if (output.CanSeek && output.Length != totalBytes)
        {
            output.SetLength(totalBytes);
        }

        return new IsoWriteResult(totalBytes, totalSectors, bootCatalogLba);
    }

    // ---------------------------------------------------------------- Groessen

    private static int MeasureDirectoryExtent(IsoDirectory dir, bool joliet)
    {
        int used = 0;
        int sectorUsed = 0;

        // "." und ".."
        AddRecord(34);
        AddRecord(34);

        foreach (DirectoryEntry entry in OrderedEntries(dir, joliet))
        {
            AddRecord(RecordLength(entry.Identifier.Length));
        }

        // Die Datenlaenge eines Verzeichnisses muss ein Vielfaches der Blockgroesse sein.
        long total = Math.Max(used + sectorUsed, IsoLayout.SectorSize);
        return (int)(IsoLayout.SectorsFor(total) * IsoLayout.SectorSize);

        void AddRecord(int length)
        {
            if (sectorUsed + length > IsoLayout.SectorSize)
            {
                used += IsoLayout.SectorSize;
                sectorUsed = 0;
            }

            sectorUsed += length;
        }
    }

    private static int RecordLength(int identifierLength)
    {
        int length = 33 + identifierLength;
        return length + (length & 1);
    }

    private static int MeasurePathTable(List<IsoDirectory> directories, bool joliet)
    {
        int total = 0;
        foreach (IsoDirectory dir in directories)
        {
            int nameLength = dir.Parent is null ? 1 : PathTableName(dir, joliet).Length;
            total += 8 + nameLength + (nameLength & 1);
        }

        return total;
    }

    private static byte[] PathTableName(IsoDirectory dir, bool joliet)
    {
        if (dir.Parent is null)
        {
            return new byte[] { 0x00 };
        }

        return joliet ? Ucs2(dir.JolietName) : Encoding.ASCII.GetBytes(dir.IsoName);
    }

    private static byte[] Ucs2(string value)
    {
        byte[] bytes = new byte[value.Length * 2];
        for (int i = 0; i < value.Length; i++)
        {
            IsoLayout.WriteUInt16Be(bytes.AsSpan(i * 2), value[i]);
        }

        return bytes;
    }

    // ---------------------------------------------------------------- Eintraege

    private readonly record struct DirectoryEntry(byte[] Identifier, bool IsDirectory, uint Extent, uint Length);

    private static List<DirectoryEntry> OrderedEntries(IsoDirectory dir, bool joliet)
    {
        List<DirectoryEntry> entries = new(dir.Directories.Count + dir.Files.Count);

        foreach (IsoDirectory child in dir.Directories)
        {
            entries.Add(new DirectoryEntry(
                joliet ? Ucs2(child.JolietName) : Encoding.ASCII.GetBytes(child.IsoName),
                IsDirectory: true,
                joliet ? child.JolietExtent : child.Extent,
                joliet ? child.JolietLength : child.Length));
        }

        foreach (IsoFile file in dir.Files)
        {
            entries.Add(new DirectoryEntry(
                joliet ? Ucs2(file.JolietName) : Encoding.ASCII.GetBytes(file.IsoName),
                IsDirectory: false,
                file.Extent,
                (uint)file.Length));
        }

        byte padding = joliet ? (byte)0x00 : (byte)0x20;
        entries.Sort((a, b) => CompareIdentifiers(a.Identifier, b.Identifier, padding));
        return entries;
    }

    /// <summary>ISO 9660 sortiert Kennungen byteweise, wobei die kuerzere rechts aufgefuellt wird.</summary>
    private static int CompareIdentifiers(byte[] left, byte[] right, byte padding)
    {
        int length = Math.Max(left.Length, right.Length);
        for (int i = 0; i < length; i++)
        {
            byte a = i < left.Length ? left[i] : padding;
            byte b = i < right.Length ? right[i] : padding;
            if (a != b)
            {
                return a < b ? -1 : 1;
            }
        }

        return 0;
    }

    // ---------------------------------------------------------------- Schreiben

    private static void WriteZeroSectors(Stream output, int count)
    {
        byte[] sector = new byte[IsoLayout.SectorSize];
        for (int i = 0; i < count; i++)
        {
            output.Write(sector, 0, sector.Length);
        }
    }

    private static void WritePadded(Stream output, byte[]? payload)
    {
        if (payload is null)
        {
            return;
        }

        output.Write(payload, 0, payload.Length);
        PadToSector(output, payload.Length);
    }

    private static void PadToSector(Stream output, long written)
    {
        int remainder = (int)(written % IsoLayout.SectorSize);
        if (remainder == 0)
        {
            return;
        }

        output.Write(new byte[IsoLayout.SectorSize - remainder]);
    }

    private static void WriteVolumeDescriptor(
        Stream output,
        byte[] sector,
        byte type,
        IsoImageOptions options,
        IsoDirectory root,
        uint totalSectors,
        uint pathTableSize,
        uint pathTableL,
        uint pathTableM,
        bool joliet)
    {
        Array.Clear(sector);
        Span<byte> s = sector;

        s[0] = type;
        "CD001"u8.CopyTo(s[1..6]);
        s[6] = 1;
        s[7] = 0;

        if (joliet)
        {
            IsoLayout.WriteUcs2(s[8..40], options.SystemIdentifier);
            IsoLayout.WriteUcs2(s[40..72], options.VolumeLabel);
            // Escape-Sequenz "%/E" = UCS-2 Level 3.
            s[88] = 0x25;
            s[89] = 0x2F;
            s[90] = 0x45;
        }
        else
        {
            IsoLayout.WriteAscii(s[8..40], options.SystemIdentifier);
            IsoLayout.WriteAscii(s[40..72], options.VolumeLabel);
        }

        IsoLayout.WriteUInt32Both(s[80..88], totalSectors);
        IsoLayout.WriteUInt16Both(s[120..124], 1);
        IsoLayout.WriteUInt16Both(s[124..128], 1);
        IsoLayout.WriteUInt16Both(s[128..132], IsoLayout.SectorSize);
        IsoLayout.WriteUInt32Both(s[132..140], pathTableSize);
        IsoLayout.WriteUInt32Le(s[140..144], pathTableL);
        IsoLayout.WriteUInt32Le(s[144..148], 0);
        IsoLayout.WriteUInt32Be(s[148..152], pathTableM);
        IsoLayout.WriteUInt32Be(s[152..156], 0);

        WriteRootRecord(s.Slice(156, 34), root, options.Timestamp, joliet);

        if (joliet)
        {
            IsoLayout.WriteUcs2(s[190..318], options.VolumeSetIdentifier);
            IsoLayout.WriteUcs2(s[318..446], options.PublisherIdentifier);
            IsoLayout.WriteUcs2(s[446..574], options.DataPreparerIdentifier);
            IsoLayout.WriteUcs2(s[574..702], options.ApplicationIdentifier);
            IsoLayout.WriteUcs2(s[702..739], string.Empty);
            IsoLayout.WriteUcs2(s[739..776], string.Empty);
            IsoLayout.WriteUcs2(s[776..813], string.Empty);
        }
        else
        {
            IsoLayout.WriteAscii(s[190..318], options.VolumeSetIdentifier);
            IsoLayout.WriteAscii(s[318..446], options.PublisherIdentifier);
            IsoLayout.WriteAscii(s[446..574], options.DataPreparerIdentifier);
            IsoLayout.WriteAscii(s[574..702], options.ApplicationIdentifier);
            IsoLayout.WriteAscii(s[702..739], string.Empty);
            IsoLayout.WriteAscii(s[739..776], string.Empty);
            IsoLayout.WriteAscii(s[776..813], string.Empty);
        }

        IsoLayout.WriteVolumeTimestamp(s[813..830], options.Timestamp);
        IsoLayout.WriteVolumeTimestamp(s[830..847], options.Timestamp);
        IsoLayout.WriteEmptyVolumeTimestamp(s[847..864]);
        IsoLayout.WriteEmptyVolumeTimestamp(s[864..881]);
        s[881] = 1;

        output.Write(sector, 0, sector.Length);
    }

    private static void WriteRootRecord(Span<byte> target, IsoDirectory root, DateTimeOffset timestamp, bool joliet)
    {
        target.Clear();
        target[0] = 34;
        target[1] = 0;
        IsoLayout.WriteUInt32Both(target[2..10], joliet ? root.JolietExtent : root.Extent);
        IsoLayout.WriteUInt32Both(target[10..18], joliet ? root.JolietLength : root.Length);
        IsoLayout.WriteDirectoryTimestamp(target[18..25], timestamp);
        target[25] = 0x02; // Verzeichnis
        target[26] = 0;
        target[27] = 0;
        IsoLayout.WriteUInt16Both(target[28..32], 1);
        target[32] = 1;
        target[33] = 0x00;
    }

    private static void WriteBootRecord(Stream output, byte[] sector, uint bootCatalogLba)
    {
        Array.Clear(sector);
        Span<byte> s = sector;
        s[0] = 0;
        "CD001"u8.CopyTo(s[1..6]);
        s[6] = 1;
        IsoLayout.WriteAscii(s.Slice(7, 32), "EL TORITO SPECIFICATION");
        // Das Feld ist eigentlich nullterminiert, nicht leerzeichengepolstert.
        for (int i = 7 + "EL TORITO SPECIFICATION".Length; i < 39; i++)
        {
            s[i] = 0;
        }

        s.Slice(39, 32).Clear();
        IsoLayout.WriteUInt32Le(s.Slice(71, 4), bootCatalogLba);
        output.Write(sector, 0, sector.Length);
    }

    private static void WriteTerminator(Stream output, byte[] sector)
    {
        Array.Clear(sector);
        sector[0] = 0xFF;
        "CD001"u8.CopyTo(sector.AsSpan(1, 5));
        sector[6] = 1;
        output.Write(sector, 0, sector.Length);
    }

    private static void WriteBootCatalog(Stream output, byte[] sector, IsoImageOptions options, uint biosLba, uint efiLba)
    {
        Array.Clear(sector);
        Span<byte> s = sector;

        // --- Validation Entry ---
        s[0] = 0x01;                        // Header-ID
        s[1] = 0x00;                        // Plattform: 80x86
        IsoLayout.WriteAscii(s.Slice(4, 24), "ISOFORGE");
        for (int i = 4 + "ISOFORGE".Length; i < 28; i++)
        {
            s[i] = 0;
        }

        s[30] = 0x55;
        s[31] = 0xAA;
        IsoLayout.WriteUInt16Le(s.Slice(28, 2), Checksum(s[..32]));

        int offset = 32;

        // --- Default Entry: BIOS ---
        if (options.BiosBootImage is not null)
        {
            WriteBootEntry(s.Slice(offset, 32), biosLba, options.BiosBootImage.Length);
        }
        else
        {
            // El Torito verlangt einen Default Entry; ohne BIOS-Abbild bleibt er als "nicht startbar" stehen.
            s[offset] = 0x00;
            s[offset + 1] = 0x00;
        }

        offset += 32;

        // --- Section fuer UEFI ---
        if (options.EfiBootImage is not null)
        {
            s[offset] = 0x91;               // letzter Section Header
            s[offset + 1] = 0xEF;           // Plattform: EFI
            IsoLayout.WriteUInt16Le(s.Slice(offset + 2, 2), 1);
            offset += 32;
            WriteBootEntry(s.Slice(offset, 32), efiLba, options.EfiBootImage.Length);
        }

        output.Write(sector, 0, sector.Length);

        static void WriteBootEntry(Span<byte> entry, uint lba, int imageLength)
        {
            entry.Clear();
            entry[0] = 0x88;                // startbar
            entry[1] = 0x00;                // keine Emulation
            IsoLayout.WriteUInt16Le(entry.Slice(2, 2), 0);
            entry[4] = 0x00;

            long virtualSectors = (imageLength + 511) / 512;
            ushort sectorCount = virtualSectors is > 0 and <= 0xFFFF ? (ushort)virtualSectors : (ushort)0;
            if (sectorCount is > 0 and < 4)
            {
                sectorCount = 4;
            }

            IsoLayout.WriteUInt16Le(entry.Slice(6, 2), sectorCount);
            IsoLayout.WriteUInt32Le(entry.Slice(8, 4), lba);
        }

        static ushort Checksum(ReadOnlySpan<byte> entry)
        {
            ushort sum = 0;
            for (int i = 0; i < entry.Length; i += 2)
            {
                sum += IsoLayout.ReadUInt16Le(entry[i..]);
            }

            return (ushort)(0x10000 - sum);
        }
    }

    private static void WritePathTable(Stream output, List<IsoDirectory> directories, uint size, bool joliet, bool bigEndian)
    {
        byte[] buffer = new byte[IsoLayout.SectorsFor(size) * IsoLayout.SectorSize];
        int offset = 0;

        foreach (IsoDirectory dir in directories)
        {
            byte[] name = PathTableName(dir, joliet);
            uint extent = joliet ? dir.JolietExtent : dir.Extent;
            ushort parent = (ushort)(dir.Parent?.PathTableIndex ?? 1);

            buffer[offset] = (byte)name.Length;
            buffer[offset + 1] = 0;
            if (bigEndian)
            {
                IsoLayout.WriteUInt32Be(buffer.AsSpan(offset + 2), extent);
                IsoLayout.WriteUInt16Be(buffer.AsSpan(offset + 6), parent);
            }
            else
            {
                IsoLayout.WriteUInt32Le(buffer.AsSpan(offset + 2), extent);
                IsoLayout.WriteUInt16Le(buffer.AsSpan(offset + 6), parent);
            }

            name.CopyTo(buffer, offset + 8);
            offset += 8 + name.Length + (name.Length & 1);
        }

        output.Write(buffer, 0, buffer.Length);
    }

    private static void WriteDirectoryExtent(Stream output, IsoDirectory dir, DateTimeOffset timestamp, bool joliet)
    {
        uint length = joliet ? dir.JolietLength : dir.Length;
        byte[] buffer = new byte[IsoLayout.SectorsFor(length) * IsoLayout.SectorSize];
        int offset = 0;

        uint selfExtent = joliet ? dir.JolietExtent : dir.Extent;
        uint selfLength = joliet ? dir.JolietLength : dir.Length;
        IsoDirectory parent = dir.Parent ?? dir;
        uint parentExtent = joliet ? parent.JolietExtent : parent.Extent;
        uint parentLength = joliet ? parent.JolietLength : parent.Length;

        WriteRecord(new byte[] { 0x00 }, true, selfExtent, selfLength);
        WriteRecord(new byte[] { 0x01 }, true, parentExtent, parentLength);

        foreach (DirectoryEntry entry in OrderedEntries(dir, joliet))
        {
            WriteRecord(entry.Identifier, entry.IsDirectory, entry.Extent, entry.Length);
        }

        output.Write(buffer, 0, buffer.Length);

        void WriteRecord(byte[] identifier, bool isDirectory, uint extent, uint dataLength)
        {
            int recordLength = RecordLength(identifier.Length);

            // Ein Directory Record darf keine Sektorgrenze ueberschreiten.
            int inSector = offset % IsoLayout.SectorSize;
            if (inSector + recordLength > IsoLayout.SectorSize)
            {
                offset += IsoLayout.SectorSize - inSector;
            }

            Span<byte> record = buffer.AsSpan(offset, recordLength);
            record.Clear();
            record[0] = (byte)recordLength;
            record[1] = 0;
            IsoLayout.WriteUInt32Both(record[2..10], extent);
            IsoLayout.WriteUInt32Both(record[10..18], dataLength);
            IsoLayout.WriteDirectoryTimestamp(record[18..25], timestamp);
            record[25] = isDirectory ? (byte)0x02 : (byte)0x00;
            record[26] = 0;
            record[27] = 0;
            IsoLayout.WriteUInt16Both(record[28..32], 1);
            record[32] = (byte)identifier.Length;
            identifier.CopyTo(record[33..]);

            offset += recordLength;
        }
    }
}
