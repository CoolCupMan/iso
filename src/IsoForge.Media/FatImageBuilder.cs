using System.Text;

namespace IsoForge.Media;

/// <summary>
/// Baut ein kleines FAT16-Abbild im Speicher. Das UEFI-Startabbild eines El-Torito-Mediums ist genau das:
/// ein FAT-Dateisystem, das die Firmware einhaengt, um daraus \EFI\BOOT\BOOTX64.EFI zu starten. Wir erzeugen
/// es selbst, damit kein efisys.bin aus dem Windows-ADK noetig ist.
/// </summary>
public static class FatImageBuilder
{
    private const int BytesPerSector = 512;
    private const int SectorsPerCluster = 1;
    private const int ClusterBytes = BytesPerSector * SectorsPerCluster;
    private const int ReservedSectors = 1;
    private const int FatCount = 2;
    private const int RootEntryCount = 512;
    private const int RootDirSectors = RootEntryCount * 32 / BytesPerSector;

    // FAT16 ist nur zwischen diesen Clusterzahlen definiert.
    private const int MinFat16Clusters = 4200;
    private const int MaxFat16Clusters = 65524;

    public static byte[] CreateEfiBootImage(byte[] bootx64, byte[]? bootia32 = null)
    {
        List<(string Path, byte[] Content)> files = new() { ("EFI/BOOT/BOOTX64.EFI", bootx64) };
        if (bootia32 is not null)
        {
            files.Add(("EFI/BOOT/BOOTIA32.EFI", bootia32));
        }

        return Create(files, "EFIBOOT");
    }

    public static byte[] Create(IEnumerable<(string Path, byte[] Content)> files, string volumeLabel)
    {
        Node root = new(string.Empty, isDirectory: true);
        foreach ((string path, byte[] content) in files)
        {
            string[] parts = path.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
            Node current = root;
            for (int i = 0; i < parts.Length - 1; i++)
            {
                current = current.GetOrAddDirectory(parts[i]);
            }

            current.Children.Add(new Node(parts[^1], isDirectory: false) { Content = content });
        }

        // --- Clusterbedarf ermitteln ------------------------------------------
        List<Node> ordered = root.EnumerateBreadthFirst().Skip(1).ToList(); // ohne Wurzel (fixes Root-Directory)
        int requiredClusters = 0;
        foreach (Node node in ordered)
        {
            node.ClusterCount = node.IsDirectory
                ? Math.Max(1, CeilDiv((node.Children.Count + 2) * 32, ClusterBytes))
                : CeilDiv(node.Content!.Length, ClusterBytes);
            requiredClusters += node.ClusterCount;
        }

        if (root.Children.Count > RootEntryCount - 1)
        {
            throw new NotSupportedException("Zu viele Eintraege im Stammverzeichnis des FAT-Abbilds.");
        }

        int clusterCount = Math.Max(requiredClusters + 16, MinFat16Clusters);
        if (clusterCount > MaxFat16Clusters)
        {
            throw new NotSupportedException(
                "Das UEFI-Startabbild waere groesser als 32 MB - das erlaubt der El-Torito-Katalog nicht.");
        }

        int fatSectors = CeilDiv((clusterCount + 2) * 2, BytesPerSector);
        int totalSectors = ReservedSectors + (FatCount * fatSectors) + RootDirSectors + (clusterCount * SectorsPerCluster);

        byte[] image = new byte[(long)totalSectors * BytesPerSector];

        int fat1Offset = ReservedSectors * BytesPerSector;
        int fat2Offset = fat1Offset + (fatSectors * BytesPerSector);
        int rootOffset = fat2Offset + (fatSectors * BytesPerSector);
        int dataOffset = rootOffset + (RootDirSectors * BytesPerSector);

        // --- Cluster vergeben ---------------------------------------------------
        ushort next = 2;
        foreach (Node node in ordered)
        {
            node.FirstCluster = node.ClusterCount == 0 ? (ushort)0 : next;
            next += (ushort)node.ClusterCount;
        }

        // --- FAT fuellen --------------------------------------------------------
        Span<byte> fat = image.AsSpan(fat1Offset, fatSectors * BytesPerSector);
        IsoLayout.WriteUInt16Le(fat, 0xFFF8);
        IsoLayout.WriteUInt16Le(fat[2..], 0xFFFF);
        foreach (Node node in ordered)
        {
            for (int i = 0; i < node.ClusterCount; i++)
            {
                ushort cluster = (ushort)(node.FirstCluster + i);
                ushort value = i == node.ClusterCount - 1 ? (ushort)0xFFFF : (ushort)(cluster + 1);
                IsoLayout.WriteUInt16Le(fat[(cluster * 2)..], value);
            }
        }

        image.AsSpan(fat1Offset, fatSectors * BytesPerSector).CopyTo(image.AsSpan(fat2Offset));

        // --- Verzeichnisse und Dateien -----------------------------------------
        WriteDirectory(image.AsSpan(rootOffset, RootDirSectors * BytesPerSector), root, parentCluster: 0, isRoot: true, volumeLabel);

        foreach (Node node in ordered)
        {
            int offset = dataOffset + (node.FirstCluster - 2) * ClusterBytes;
            if (node.IsDirectory)
            {
                ushort parentCluster = node.Parent is null || node.Parent.Parent is null ? (ushort)0 : node.Parent.FirstCluster;
                WriteDirectory(image.AsSpan(offset, node.ClusterCount * ClusterBytes), node, parentCluster, isRoot: false, null);
            }
            else if (node.Content!.Length > 0)
            {
                node.Content.CopyTo(image, offset);
            }
        }

        WriteBootSector(image, totalSectors, fatSectors, volumeLabel);
        return image;
    }

    private static void WriteBootSector(byte[] image, int totalSectors, int fatSectors, string volumeLabel)
    {
        Span<byte> s = image.AsSpan(0, BytesPerSector);
        s[0] = 0xEB;
        s[1] = 0x3C;
        s[2] = 0x90;
        WriteFixed(s.Slice(3, 8), "MSWIN4.1");
        IsoLayout.WriteUInt16Le(s[11..], BytesPerSector);
        s[13] = SectorsPerCluster;
        IsoLayout.WriteUInt16Le(s[14..], ReservedSectors);
        s[16] = FatCount;
        IsoLayout.WriteUInt16Le(s[17..], RootEntryCount);
        IsoLayout.WriteUInt16Le(s[19..], totalSectors <= 0xFFFF ? (ushort)totalSectors : (ushort)0);
        s[21] = 0xF8;
        IsoLayout.WriteUInt16Le(s[22..], (ushort)fatSectors);
        IsoLayout.WriteUInt16Le(s[24..], 63);
        IsoLayout.WriteUInt16Le(s[26..], 255);
        IsoLayout.WriteUInt32Le(s[28..], 0);
        IsoLayout.WriteUInt32Le(s[32..], totalSectors <= 0xFFFF ? 0u : (uint)totalSectors);
        s[36] = 0x80;
        s[37] = 0x00;
        s[38] = 0x29;
        IsoLayout.WriteUInt32Le(s[39..], 0x15F0_0D1E);
        WriteFixed(s.Slice(43, 11), volumeLabel);
        WriteFixed(s.Slice(54, 8), "FAT16");
        s[510] = 0x55;
        s[511] = 0xAA;
    }

    private static void WriteDirectory(Span<byte> target, Node dir, ushort parentCluster, bool isRoot, string? volumeLabel)
    {
        target.Clear();
        int offset = 0;

        if (isRoot && !string.IsNullOrEmpty(volumeLabel))
        {
            Span<byte> label = target.Slice(offset, 32);
            WriteFixed(label[..11], volumeLabel.ToUpperInvariant());
            label[11] = 0x08; // Datentraegerbezeichnung
            offset += 32;
        }

        if (!isRoot)
        {
            WriteEntry(target.Slice(offset, 32), ".", isDirectory: true, dir.FirstCluster, 0);
            offset += 32;
            WriteEntry(target.Slice(offset, 32), "..", isDirectory: true, parentCluster, 0);
            offset += 32;
        }

        foreach (Node child in dir.Children)
        {
            WriteEntry(target.Slice(offset, 32), child.Name, child.IsDirectory, child.FirstCluster,
                child.IsDirectory ? 0 : (uint)child.Content!.Length);
            offset += 32;
        }
    }

    private static void WriteEntry(Span<byte> entry, string name, bool isDirectory, ushort firstCluster, uint size)
    {
        entry.Clear();
        WriteShortName(entry[..11], name);
        entry[11] = isDirectory ? (byte)0x10 : (byte)0x20;
        IsoLayout.WriteUInt16Le(entry[14..], 0x0000); // Erstellungszeit
        IsoLayout.WriteUInt16Le(entry[16..], 0x0021); // 1980-01-01
        IsoLayout.WriteUInt16Le(entry[18..], 0x0021);
        IsoLayout.WriteUInt16Le(entry[20..], 0);      // FAT16: obere Clusterbits immer 0
        IsoLayout.WriteUInt16Le(entry[22..], 0x0000);
        IsoLayout.WriteUInt16Le(entry[24..], 0x0021);
        IsoLayout.WriteUInt16Le(entry[26..], firstCluster);
        IsoLayout.WriteUInt32Le(entry[28..], size);
    }

    private static void WriteShortName(Span<byte> target, string name)
    {
        target.Fill(0x20);

        if (name is "." or "..")
        {
            for (int i = 0; i < name.Length; i++)
            {
                target[i] = (byte)'.';
            }

            return;
        }

        int dot = name.LastIndexOf('.');
        string stem = (dot > 0 ? name[..dot] : name).ToUpperInvariant();
        string ext = (dot > 0 ? name[(dot + 1)..] : string.Empty).ToUpperInvariant();

        for (int i = 0; i < 8 && i < stem.Length; i++)
        {
            target[i] = (byte)stem[i];
        }

        for (int i = 0; i < 3 && i < ext.Length; i++)
        {
            target[8 + i] = (byte)ext[i];
        }
    }

    private static void WriteFixed(Span<byte> target, string value)
    {
        target.Fill(0x20);
        byte[] bytes = Encoding.ASCII.GetBytes(value);
        bytes.AsSpan(0, Math.Min(bytes.Length, target.Length)).CopyTo(target);
    }

    private static int CeilDiv(int value, int divisor) => (value + divisor - 1) / divisor;

    private sealed class Node
    {
        public Node(string name, bool isDirectory)
        {
            Name = name;
            IsDirectory = isDirectory;
        }

        public string Name { get; }

        public bool IsDirectory { get; }

        public byte[]? Content { get; init; }

        public List<Node> Children { get; } = new();

        public Node? Parent { get; private set; }

        public ushort FirstCluster { get; set; }

        public int ClusterCount { get; set; }

        public Node GetOrAddDirectory(string name)
        {
            Node? existing = Children.FirstOrDefault(c => c.IsDirectory && string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));
            if (existing is not null)
            {
                return existing;
            }

            Node created = new(name, isDirectory: true) { Parent = this };
            Children.Add(created);
            return created;
        }

        public IEnumerable<Node> EnumerateBreadthFirst()
        {
            Queue<Node> queue = new();
            queue.Enqueue(this);
            while (queue.Count > 0)
            {
                Node current = queue.Dequeue();
                yield return current;
                foreach (Node child in current.Children)
                {
                    child.Parent ??= current;
                    queue.Enqueue(child);
                }
            }
        }
    }
}
