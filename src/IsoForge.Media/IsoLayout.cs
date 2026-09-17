namespace IsoForge.Media;

/// <summary>Gemeinsame Konstanten und Low-Level-Helfer fuer das ISO-9660-Format.</summary>
public static class IsoLayout
{
    public const int SectorSize = 2048;

    /// <summary>Die ersten 16 Sektoren sind der "System Area" vorbehalten und bleiben leer.</summary>
    public const int SystemAreaSectors = 16;

    /// <summary>ISO 9660 speichert 32-Bit-Groessen, damit liegt die Obergrenze je Datei bei 4 GiB - 1.</summary>
    public const long MaxFileSize = 0xFFFFFFFFL;

    public static long SectorsFor(long byteCount) => (byteCount + SectorSize - 1) / SectorSize;

    public static void WriteUInt16Both(Span<byte> target, ushort value)
    {
        target[0] = (byte)(value & 0xFF);
        target[1] = (byte)(value >> 8);
        target[2] = (byte)(value >> 8);
        target[3] = (byte)(value & 0xFF);
    }

    public static void WriteUInt32Both(Span<byte> target, uint value)
    {
        WriteUInt32Le(target, value);
        WriteUInt32Be(target[4..], value);
    }

    public static void WriteUInt32Le(Span<byte> target, uint value)
    {
        target[0] = (byte)(value & 0xFF);
        target[1] = (byte)((value >> 8) & 0xFF);
        target[2] = (byte)((value >> 16) & 0xFF);
        target[3] = (byte)((value >> 24) & 0xFF);
    }

    public static void WriteUInt32Be(Span<byte> target, uint value)
    {
        target[0] = (byte)((value >> 24) & 0xFF);
        target[1] = (byte)((value >> 16) & 0xFF);
        target[2] = (byte)((value >> 8) & 0xFF);
        target[3] = (byte)(value & 0xFF);
    }

    public static void WriteUInt16Le(Span<byte> target, ushort value)
    {
        target[0] = (byte)(value & 0xFF);
        target[1] = (byte)(value >> 8);
    }

    public static void WriteUInt16Be(Span<byte> target, ushort value)
    {
        target[0] = (byte)(value >> 8);
        target[1] = (byte)(value & 0xFF);
    }

    public static uint ReadUInt32Le(ReadOnlySpan<byte> source) =>
        (uint)(source[0] | (source[1] << 8) | (source[2] << 16) | (source[3] << 24));

    public static ushort ReadUInt16Le(ReadOnlySpan<byte> source) => (ushort)(source[0] | (source[1] << 8));

    /// <summary>ASCII, rechts mit Leerzeichen aufgefuellt - das Standard-Padding fuer ISO-Textfelder.</summary>
    public static void WriteAscii(Span<byte> target, string value)
    {
        target.Fill(0x20);
        for (int i = 0; i < value.Length && i < target.Length; i++)
        {
            char c = value[i];
            target[i] = c is >= ' ' and <= '~' ? (byte)c : (byte)'_';
        }
    }

    /// <summary>UCS-2 Big Endian, rechts mit U+0020 aufgefuellt (Joliet-Textfelder).</summary>
    public static void WriteUcs2(Span<byte> target, string value)
    {
        for (int i = 0; i < target.Length; i += 2)
        {
            target[i] = 0x00;
            target[i + 1] = 0x20;
        }

        int max = target.Length / 2;
        for (int i = 0; i < value.Length && i < max; i++)
        {
            WriteUInt16Be(target[(i * 2)..], value[i]);
        }
    }

    /// <summary>7-Byte-Zeitstempel eines Directory Records.</summary>
    public static void WriteDirectoryTimestamp(Span<byte> target, DateTimeOffset when)
    {
        target[0] = (byte)Math.Clamp(when.Year - 1900, 0, 255);
        target[1] = (byte)when.Month;
        target[2] = (byte)when.Day;
        target[3] = (byte)when.Hour;
        target[4] = (byte)when.Minute;
        target[5] = (byte)when.Second;
        target[6] = unchecked((byte)(sbyte)(when.Offset.TotalMinutes / 15));
    }

    /// <summary>17-Byte-Zeitstempel eines Volume Descriptors ("YYYYMMDDHHMMSSCC" + Offset).</summary>
    public static void WriteVolumeTimestamp(Span<byte> target, DateTimeOffset when)
    {
        string text = when.ToString("yyyyMMddHHmmss") + (when.Millisecond / 10).ToString("00");
        for (int i = 0; i < 16; i++)
        {
            target[i] = (byte)text[i];
        }

        target[16] = unchecked((byte)(sbyte)(when.Offset.TotalMinutes / 15));
    }

    public static void WriteEmptyVolumeTimestamp(Span<byte> target)
    {
        for (int i = 0; i < 16; i++)
        {
            target[i] = (byte)'0';
        }

        target[16] = 0;
    }
}
