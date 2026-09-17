using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using IsoForge.Capture;
using IsoForge.Core;

namespace IsoForge.Media;

/// <summary>
/// Schreibt das Medium mit der Image Mastering API (IMAPI2FS), die in jedem Windows seit Vista steckt.
///
/// Das ist der wichtigste der drei Wege, denn IMAPI erzeugt neben ISO 9660 und Joliet auch ein
/// UDF-Dateisystem. Microsofts eigene Installationsmedien sind genau so aufgebaut, und das aus gutem
/// Grund: Die UEFI-Spezifikation schreibt nur FAT-Unterstuetzung vor. Viele Firmware-Fassungen - darunter
/// EDK2/OVMF und Hyper-V der zweiten Generation - stellen fuer ein reines ISO-9660-Medium ueberhaupt kein
/// Dateisystem bereit. bootmgfw.efi wuerde dann zwar starten, seine Startkonfiguration unter
/// \EFI\Microsoft\Boot\BCD aber nicht mehr finden und mit 0xc000000f stehen bleiben.
///
/// Angesprochen wird IMAPI ueber IDispatch. Das erspart die Interface-Kennungen und funktioniert mit
/// jeder Fassung der Bibliothek.
/// </summary>
public static class ImapiIsoWriter
{
    private const int FsiFileSystemIso9660 = 1;
    private const int FsiFileSystemJoliet = 2;
    private const int FsiFileSystemUdf = 4;

    private const int EmulationNone = 0;
    private const int PlatformX86 = 0;
    private const int PlatformEfi = 0xEF;

    private const uint StgmRead = 0x00000000;
    private const uint StgmShareDenyWrite = 0x00000020;

    public static bool IsAvailable() => ProgId("IMAPI2FS.MsftFileSystemImage") is not null;

    public static void Write(
        string staging,
        string outputPath,
        string volumeLabel,
        byte[]? biosBootImage,
        byte[]? efiBootImage,
        string workDirectory)
    {
        Type imageType = ProgId("IMAPI2FS.MsftFileSystemImage")
                         ?? throw new InvalidOperationException("IMAPI2FS ist auf diesem System nicht verfuegbar.");

        dynamic image = Activator.CreateInstance(imageType)
                        ?? throw new InvalidOperationException("IMAPI2FS liess sich nicht erzeugen.");

        List<object> comObjects = new() { image };
        List<string> temporaryFiles = new();

        try
        {
            image.FileSystemsToCreate = FsiFileSystemIso9660 | FsiFileSystemJoliet | FsiFileSystemUdf;
            image.UDFRevision = 0x102;
            image.VolumeName = Truncate(volumeLabel, 32);
            image.FreeMediaBlocks = -1; // keine Groessenbegrenzung durch ein angenommenes Medium

            AssignBootImages(image, comObjects, temporaryFiles, workDirectory, biosBootImage, efiBootImage);

            Log.Info("Dateien werden erfasst ...");
            image.Root.AddTree(staging, false);

            Log.Info("Abbild wird erzeugt (ISO 9660 + Joliet + UDF 1.02) ...");
            dynamic result = image.CreateResultImage();
            comObjects.Add(result);

            IStream stream = (IStream)result.ImageStream;
            comObjects.Add(stream);

            CopyStreamToFile(stream, outputPath);
        }
        finally
        {
            foreach (object com in Enumerable.Reverse(comObjects))
            {
                if (Marshal.IsComObject(com))
                {
                    Marshal.FinalReleaseComObject(com);
                }
            }

            foreach (string file in temporaryFiles)
            {
                TryDelete(file);
            }
        }
    }

    private static void AssignBootImages(
        dynamic image,
        List<object> comObjects,
        List<string> temporaryFiles,
        string workDirectory,
        byte[]? biosBootImage,
        byte[]? efiBootImage)
    {
        List<object> bootOptions = new();

        if (biosBootImage is not null)
        {
            bootOptions.Add(CreateBootOptions(biosBootImage, PlatformX86, "etfsboot.bin", workDirectory, comObjects, temporaryFiles));
        }

        if (efiBootImage is not null)
        {
            bootOptions.Add(CreateBootOptions(efiBootImage, PlatformEfi, "efisys.bin", workDirectory, comObjects, temporaryFiles));
        }

        if (bootOptions.Count == 0)
        {
            return;
        }

        if (bootOptions.Count > 1)
        {
            // Mehrere Startabbilder gibt es erst ueber IFileSystemImage3 (Windows 8 und neuer).
            try
            {
                image.BootImageOptionsArray = bootOptions.ToArray();
                Log.Info($"{bootOptions.Count} Startabbilder eingetragen (BIOS und UEFI).");
                return;
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    "IMAPI2FS hat den Eintrag zweier Startabbilder abgelehnt: " + ex.Message +
                    " Mit --iso-engine builtin laesst sich der eingebaute Schreiber erzwingen.", ex);
            }
        }

        image.BootImageOptions = bootOptions[0];
        Log.Info("Ein Startabbild eingetragen.");
    }

    private static object CreateBootOptions(
        byte[] content,
        int platformId,
        string fileName,
        string workDirectory,
        List<object> comObjects,
        List<string> temporaryFiles)
    {
        Directory.CreateDirectory(workDirectory);
        string path = Path.Combine(workDirectory, fileName);
        File.WriteAllBytes(path, content);
        temporaryFiles.Add(path);

        IStream stream = SHCreateStreamOnFileEx(path, StgmRead | StgmShareDenyWrite, 0, false, null);
        comObjects.Add(stream);

        dynamic options = Activator.CreateInstance(ProgId("IMAPI2FS.BootOptions")!)
                          ?? throw new InvalidOperationException("IMAPI2FS.BootOptions liess sich nicht erzeugen.");

        comObjects.Add(options);

        options.Manufacturer = "IsoForge";
        options.PlatformId = platformId;
        options.Emulation = EmulationNone;
        options.AssignBootImage(stream);

        Log.Debug($"Startabbild fuer Plattform 0x{platformId:X2}: {content.Length} Bytes");
        return options;
    }

    private static void CopyStreamToFile(IStream stream, string outputPath)
    {
        stream.Stat(out STATSTG statistics, 1);
        long total = statistics.cbSize;

        using FileStream output = new(outputPath, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20);

        byte[] buffer = new byte[1 << 20];
        nint readCount = Marshal.AllocHGlobal(sizeof(int));

        try
        {
            long written = 0;
            while (true)
            {
                stream.Read(buffer, buffer.Length, readCount);
                int read = Marshal.ReadInt32(readCount);
                if (read <= 0)
                {
                    break;
                }

                output.Write(buffer, 0, read);
                written += read;

                if (total > 0)
                {
                    int percent = (int)(written * 100 / total);
                    int filled = percent * 40 / 100;
                    Console.Write($"\r  Abbild wird geschrieben: [{new string('#', filled)}{new string('.', 40 - filled)}] {percent,3}%");
                }
            }

            Console.WriteLine();
            Log.Debug($"{Format.Bytes(written)} aus dem IMAPI-Datenstrom uebernommen.");
        }
        finally
        {
            Marshal.FreeHGlobal(readCount);
        }
    }

    private static Type? ProgId(string progId) => Type.GetTypeFromProgID(progId);

    private static string Truncate(string value, int maxLength) =>
        value.Length > maxLength ? value[..maxLength] : value;

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex)
        {
            Log.Debug($"Temporaere Datei {path} blieb liegen: {ex.Message}");
        }
    }

    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode, ExactSpelling = true, PreserveSig = false)]
    private static extern IStream SHCreateStreamOnFileEx(
        string fileName,
        uint mode,
        uint attributes,
        [MarshalAs(UnmanagedType.Bool)] bool create,
        IStream? template);
}
