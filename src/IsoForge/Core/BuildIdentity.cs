using System.Reflection;
using System.Security.Cryptography;
using System.Text;

namespace IsoForge.Core;

/// <summary>
/// Jede erzeugte IsoForge.exe traegt eine eigene Build-Kennung. Sie wandert in den Dateinamen, den
/// Installationspfad, die Datentraegerbezeichnung des ISO und in den Namen des spaeter erzeugten
/// Starteintrags. Dadurch lassen sich mehrere Staende nebeneinander installieren und im Bootmenue
/// auseinanderhalten.
/// </summary>
public static class BuildIdentity
{
    private const string CrockfordAlphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";

    private static readonly Lazy<(Guid Id, string Short)> Identity = new(Resolve);

    /// <summary>Die vollstaendige, eindeutige Kennung dieses Builds.</summary>
    public static Guid BuildId => Identity.Value.Id;

    /// <summary>Acht Zeichen aus dem Crockford-Base32-Alphabet - ohne I, L, O und U, also vorlesbar.</summary>
    public static string ShortId => Identity.Value.Short;

    public static string Version =>
        Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            is { Length: > 0 } informational
            ? informational.Split('+')[0]
            : Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.0.0";

    /// <summary>Name, unter dem dieser Stand installiert und im Bootmenue gefuehrt wird.</summary>
    public static string ProductInstance => $"IsoForge {Version} ({ShortId})";

    /// <summary>
    /// Die Datentraegerbezeichnung des Mediums. Sie darf hoechstens 16 Zeichen lang sein: Joliet legt
    /// dafuer 32 Byte in UCS-2 an, und Windows zeigt bevorzugt die Joliet-Bezeichnung an. Laenger
    /// gewaehlt, fiele ausgerechnet die Build-Kennung am Ende weg.
    /// </summary>
    public static string VolumeLabel(string tier) => Sanitize($"IF_{ShortTier(tier)}_{ShortId}", 16);

    private static string ShortTier(string tier)
    {
        switch (tier.ToLowerInvariant())
        {
            case "full": return "FULL";
            case "system": return "SYS";
            case "personal": return "PERS";
            case "custom": return "CUST";
            default:
                string upper = tier.ToUpperInvariant();
                return upper.Length > 4 ? upper[..4] : upper;
        }
    }

    private static (Guid, string) Resolve()
    {
        string? value = Assembly.GetExecutingAssembly()
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == "IsoForgeBuildId")?.Value;

        if (Guid.TryParse(value, out Guid parsed) && parsed != Guid.Empty)
        {
            return (parsed, Encode(parsed));
        }

        // Kein Build-Server im Spiel: aus dem Pfad der Programmdatei eine stabile Ersatzkennung ableiten,
        // damit auch lokale Testlaeufe eine reproduzierbare Kennung haben.
        string seed = Environment.ProcessPath ?? Assembly.GetExecutingAssembly().Location;
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes("IsoForge-dev|" + seed));
        Guid fallback = new(hash.AsSpan(0, 16));
        return (fallback, Encode(fallback));
    }

    private static string Encode(Guid id)
    {
        byte[] bytes = id.ToByteArray();
        ulong bits = 0;
        for (int i = 0; i < 5; i++)
        {
            bits = (bits << 8) | bytes[i];
        }

        char[] result = new char[8];
        for (int i = 7; i >= 0; i--)
        {
            result[i] = CrockfordAlphabet[(int)(bits & 0x1F)];
            bits >>= 5;
        }

        return new string(result);
    }

    private static string Sanitize(string value, int maxLength)
    {
        StringBuilder builder = new(value.Length);
        foreach (char c in value.ToUpperInvariant())
        {
            builder.Append(c is (>= 'A' and <= 'Z') or (>= '0' and <= '9') or '_' ? c : '_');
        }

        string cleaned = builder.ToString();
        return cleaned.Length > maxLength ? cleaned[..maxLength] : cleaned;
    }
}
