using System.Text;

namespace IsoForge.Media;

/// <summary>Eine Datei im Abbild. Der Inhalt kommt entweder aus einer Datei auf der Platte oder aus dem Speicher.</summary>
public sealed class IsoFile
{
    public IsoFile(string name, string sourcePath)
    {
        Name = name;
        SourcePath = sourcePath;
        Length = new FileInfo(sourcePath).Length;
    }

    public IsoFile(string name, byte[] content)
    {
        Name = name;
        Content = content;
        Length = content.Length;
    }

    /// <summary>Inhalt, der erst beim Schreiben entsteht - die Laenge muss vorher feststehen.</summary>
    public IsoFile(string name, long length, Action<Stream> writer)
    {
        Name = name;
        Length = length;
        Writer = writer;
    }

    public string Name { get; }

    public string? SourcePath { get; }

    public byte[]? Content { get; }

    private Action<Stream>? Writer { get; }

    public long Length { get; }

    /// <summary>Wird waehrend des Layouts belegt.</summary>
    internal uint Extent { get; set; }

    internal string IsoName { get; set; } = string.Empty;

    internal string JolietName { get; set; } = string.Empty;

    public void CopyTo(Stream destination)
    {
        if (Writer is not null)
        {
            Writer(destination);
            return;
        }

        if (Content is not null)
        {
            destination.Write(Content, 0, Content.Length);
            return;
        }

        using FileStream source = new(SourcePath!, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, FileOptions.SequentialScan);
        source.CopyTo(destination, 1 << 20);
    }
}

/// <summary>Ein Verzeichnis im Abbild.</summary>
public sealed class IsoDirectory
{
    public IsoDirectory(string name) => Name = name;

    public string Name { get; }

    public List<IsoDirectory> Directories { get; } = new();

    public List<IsoFile> Files { get; } = new();

    internal IsoDirectory? Parent { get; set; }

    internal uint Extent { get; set; }

    internal uint Length { get; set; }

    internal uint JolietExtent { get; set; }

    internal uint JolietLength { get; set; }

    /// <summary>1-basierte Nummer in der Path Table.</summary>
    internal int PathTableIndex { get; set; }

    internal string IsoName { get; set; } = string.Empty;

    internal string JolietName { get; set; } = string.Empty;

    public IsoDirectory GetOrAddDirectory(string name)
    {
        IsoDirectory? existing = Directories.FirstOrDefault(d => string.Equals(d.Name, name, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            return existing;
        }

        IsoDirectory created = new(name) { Parent = this };
        Directories.Add(created);
        return created;
    }

    public IsoFile AddFile(IsoFile file)
    {
        Files.Add(file);
        return file;
    }

    /// <summary>Liest ein Verzeichnis der Platte rekursiv ein.</summary>
    public static IsoDirectory FromDisk(string path)
    {
        IsoDirectory root = new(string.Empty);
        Populate(root, new DirectoryInfo(path));
        return root;
    }

    private static void Populate(IsoDirectory node, DirectoryInfo source)
    {
        foreach (FileInfo file in source.GetFiles().OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase))
        {
            node.AddFile(new IsoFile(file.Name, file.FullName));
        }

        foreach (DirectoryInfo dir in source.GetDirectories().OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase))
        {
            IsoDirectory child = node.GetOrAddDirectory(dir.Name);
            Populate(child, dir);
        }
    }

    /// <summary>Breitensuche - genau die Reihenfolge, die die Path Table verlangt.</summary>
    internal IEnumerable<IsoDirectory> EnumerateBreadthFirst()
    {
        Queue<IsoDirectory> queue = new();
        queue.Enqueue(this);
        while (queue.Count > 0)
        {
            IsoDirectory current = queue.Dequeue();
            yield return current;
            foreach (IsoDirectory child in current.Directories)
            {
                queue.Enqueue(child);
            }
        }
    }

    internal IEnumerable<IsoFile> EnumerateFiles() => EnumerateBreadthFirst().SelectMany(d => d.Files);
}

/// <summary>Vergibt ISO-9660- (Level 2) und Joliet-Namen und haelt sie je Verzeichnis eindeutig.</summary>
internal static class IsoNaming
{
    private const int MaxIsoNameLength = 30;
    private const int MaxJolietNameLength = 64;

    public static void Assign(IsoDirectory root)
    {
        foreach (IsoDirectory dir in root.EnumerateBreadthFirst())
        {
            HashSet<string> isoUsed = new(StringComparer.Ordinal);
            HashSet<string> jolietUsed = new(StringComparer.OrdinalIgnoreCase);

            foreach (IsoDirectory child in dir.Directories)
            {
                child.IsoName = Unique(isoUsed, SanitizeIso(child.Name, isDirectory: true), MaxIsoNameLength);
                child.JolietName = Unique(jolietUsed, SanitizeJoliet(child.Name), MaxJolietNameLength);
            }

            foreach (IsoFile file in dir.Files)
            {
                // Dateikennungen tragen im primaeren Baum immer eine Versionsnummer.
                file.IsoName = Unique(isoUsed, SanitizeIso(file.Name, isDirectory: false), MaxIsoNameLength + 2) ;
                file.JolietName = Unique(jolietUsed, SanitizeJoliet(file.Name), MaxJolietNameLength);
            }
        }
    }

    private static string Unique(HashSet<string> used, string candidate, int maxLength)
    {
        if (used.Add(candidate))
        {
            return candidate;
        }

        // Bei Kollision einen numerischen Suffix vor der Erweiterung einziehen.
        string stem = candidate;
        string tail = string.Empty;
        int semi = candidate.LastIndexOf(';');
        if (semi >= 0)
        {
            stem = candidate[..semi];
            tail = candidate[semi..];
        }

        int dot = stem.LastIndexOf('.');
        string baseName = dot >= 0 ? stem[..dot] : stem;
        string extension = dot >= 0 ? stem[dot..] : string.Empty;

        for (int i = 1; i < 10000; i++)
        {
            string suffix = i.ToString();
            int room = maxLength - tail.Length - extension.Length - suffix.Length;
            string trimmed = baseName.Length > room ? baseName[..Math.Max(room, 0)] : baseName;
            string next = trimmed + suffix + extension + tail;
            if (used.Add(next))
            {
                return next;
            }
        }

        throw new InvalidOperationException($"Kein eindeutiger ISO-Name fuer '{candidate}' zu finden.");
    }

    private static string SanitizeIso(string name, bool isDirectory)
    {
        if (isDirectory)
        {
            string cleaned = Filter(name.Replace('.', '_'));
            if (cleaned.Length > MaxIsoNameLength)
            {
                cleaned = cleaned[..MaxIsoNameLength];
            }

            return cleaned.Length == 0 ? "_" : cleaned;
        }

        int dot = name.LastIndexOf('.');
        string stem = dot > 0 ? name[..dot] : name;
        string ext = dot > 0 ? name[(dot + 1)..] : string.Empty;

        stem = Filter(stem.Replace('.', '_'));
        ext = Filter(ext.Replace('.', '_'));

        if (ext.Length > 3)
        {
            ext = ext[..3];
        }

        int stemRoom = MaxIsoNameLength - (ext.Length > 0 ? ext.Length + 1 : 0);
        if (stem.Length > stemRoom)
        {
            stem = stem[..stemRoom];
        }

        if (stem.Length == 0)
        {
            stem = "_";
        }

        return ext.Length > 0 ? $"{stem}.{ext};1" : $"{stem};1";
    }

    private static string Filter(string value)
    {
        StringBuilder builder = new(value.Length);
        foreach (char c in value.ToUpperInvariant())
        {
            builder.Append(c is (>= 'A' and <= 'Z') or (>= '0' and <= '9') or '_' ? c : '_');
        }

        return builder.ToString();
    }

    private static string SanitizeJoliet(string name)
    {
        StringBuilder builder = new(name.Length);
        foreach (char c in name)
        {
            builder.Append(c is '*' or '/' or ':' or ';' or '?' or '\\' or '"' or '<' or '>' or '|' ? '_' : c);
        }

        string cleaned = builder.ToString();
        return cleaned.Length > MaxJolietNameLength ? cleaned[..MaxJolietNameLength] : cleaned;
    }
}
