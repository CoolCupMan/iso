using System.Text.RegularExpressions;
using IsoForge.Core;

namespace IsoForge.Media;

/// <summary>
/// Erzeugt die Startkonfiguration (BCD) des Mediums mit bcdedit. Der Eintrag laedt boot.wim als
/// RAM-Datentraeger - genau so startet auch ein Windows-Installationsmedium.
/// </summary>
public static partial class BcdBuilder
{
    private static readonly string BcdEditPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.System), "bcdedit.exe");

    [GeneratedRegex(@"\{[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}\}")]
    private static partial Regex GuidPattern();

    public enum Firmware
    {
        Bios,
        Uefi,
    }

    public static void CreateStore(string storePath, Firmware firmware, string entryDescription, int timeoutSeconds = 5)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(storePath)!);
        if (File.Exists(storePath))
        {
            File.Delete(storePath);
        }

        string store = $"/store \"{storePath}\"";

        Bcd($"/createstore \"{storePath}\"");

        Bcd($"{store} /create {{ramdiskoptions}} /d \"IsoForge Ramdisk\"");
        Bcd($"{store} /set {{ramdiskoptions}} ramdisksdidevice boot");
        Bcd($"{store} /set {{ramdiskoptions}} ramdisksdipath \\boot\\boot.sdi");

        string entry = CreateEntry(store, entryDescription);

        string loader = firmware == Firmware.Uefi
            ? @"\windows\system32\boot\winload.efi"
            : @"\windows\system32\boot\winload.exe";

        Bcd($"{store} /set {entry} device ramdisk=[boot]\\sources\\boot.wim,{{ramdiskoptions}}");
        Bcd($"{store} /set {entry} osdevice ramdisk=[boot]\\sources\\boot.wim,{{ramdiskoptions}}");
        Bcd($"{store} /set {entry} path {loader}");
        Bcd($"{store} /set {entry} systemroot \\windows");
        Bcd($"{store} /set {entry} winpe yes");
        Bcd($"{store} /set {entry} detecthal yes");
        Bcd($"{store} /set {entry} nx optin");
        Bcd($"{store} /set {entry} bootmenupolicy standard");

        Bcd($"{store} /create {{bootmgr}} /d \"IsoForge Startmanager\"");
        Bcd($"{store} /set {{bootmgr}} device boot");
        Bcd($"{store} /set {{bootmgr}} timeout {timeoutSeconds}");
        Bcd($"{store} /set {{bootmgr}} displayorder {entry}");
        Bcd($"{store} /default {entry}");

        Log.Info($"Startkonfiguration fuer {firmware} geschrieben: {storePath}");
    }

    private static string CreateEntry(string store, string description)
    {
        ProcessResult result = Bcd($"{store} /create /d \"{description}\" /application osloader");
        Match match = GuidPattern().Match(result.StandardOutput);
        if (!match.Success)
        {
            throw new InvalidOperationException(
                $"bcdedit hat keine Eintragskennung zurueckgemeldet.{Environment.NewLine}{result.CombinedOutput}");
        }

        return match.Value;
    }

    private static ProcessResult Bcd(string arguments)
    {
        ProcessResult result = ProcessRunner.Run(BcdEditPath, arguments);
        if (!result.Success)
        {
            throw new InvalidOperationException(
                $"bcdedit {arguments} endete mit Code {result.ExitCode}.{Environment.NewLine}{result.CombinedOutput}");
        }

        return result;
    }
}
