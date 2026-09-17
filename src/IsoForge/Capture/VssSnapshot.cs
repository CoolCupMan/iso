using System.Management;
using IsoForge.Core;

namespace IsoForge.Capture;

/// <summary>
/// Eine Schattenkopie des Volume Shadow Copy Service. Sie friert den Zustand eines Laufwerks zu einem
/// Zeitpunkt ein, sodass das laufende Windows waehrend der Sicherung ungestoert weiterarbeiten kann und
/// offene Dateien - Registrierung, Datenbanken, Postfaecher - trotzdem in sich stimmig gesichert werden.
/// </summary>
public sealed class VssSnapshot : IDisposable
{
    private readonly string _shadowId;
    private bool _disposed;

    private VssSnapshot(string shadowId, string deviceObject, string mountPath, string volumeRoot)
    {
        _shadowId = shadowId;
        DeviceObject = deviceObject;
        MountPath = mountPath;
        VolumeRoot = volumeRoot;
    }

    /// <summary>Der Geraetepfad der Schattenkopie, z. B. \\?\GLOBALROOT\Device\HarddiskVolumeShadowCopy7.</summary>
    public string DeviceObject { get; }

    /// <summary>Ein Verzeichnis-Symlink auf die Schattenkopie - erst darueber kommt DISM an die Daten.</summary>
    public string MountPath { get; }

    public string VolumeRoot { get; }

    public static VssSnapshot Create(string volumeRoot, string mountDirectory)
    {
        if (!volumeRoot.EndsWith('\\'))
        {
            volumeRoot += "\\";
        }

        Log.Info($"Schattenkopie fuer {volumeRoot} wird angelegt ...");

        using ManagementClass shadowClass = new(@"root\cimv2", "Win32_ShadowCopy", null);
        using ManagementBaseObject input = shadowClass.GetMethodParameters("Create");
        input["Volume"] = volumeRoot;
        input["Context"] = "ClientAccessible";

        using ManagementBaseObject output = shadowClass.InvokeMethod("Create", input, null)
            ?? throw new InvalidOperationException("Win32_ShadowCopy.Create lieferte keine Antwort.");

        uint returnValue = Convert.ToUInt32(output["ReturnValue"]);
        if (returnValue != 0)
        {
            throw new InvalidOperationException(
                $"Die Schattenkopie konnte nicht angelegt werden (Code {returnValue}: {DescribeError(returnValue)}). " +
                "Pruefen Sie, ob der Dienst 'Volume Shadow Copy' laeuft und auf dem Laufwerk Schattenspeicher eingerichtet ist.");
        }

        string shadowId = (string)output["ShadowID"];
        string deviceObject = QueryDeviceObject(shadowId);
        Log.Debug($"Schattenkopie {shadowId} -> {deviceObject}");

        string mountPath = Path.Combine(mountDirectory, "vss_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(mountDirectory);

        // Der Zielpfad muss mit einem Backslash enden, sonst legt Windows den Link nicht an.
        if (!NativeMethods.CreateSymbolicLink(mountPath, deviceObject + "\\", NativeMethods.SymbolicLinkFlagDirectory))
        {
            DeleteShadow(shadowId);
            throw new InvalidOperationException(
                $"Der Verzeichnis-Verweis auf die Schattenkopie liess sich nicht anlegen ({mountPath}). " +
                "Dafuer ist das Recht 'Symbolische Verknuepfungen erstellen' noetig.");
        }

        Log.Info($"Schattenkopie eingehaengt unter {mountPath}");
        return new VssSnapshot(shadowId, deviceObject, mountPath, volumeRoot);
    }

    private static string QueryDeviceObject(string shadowId)
    {
        using ManagementObjectSearcher searcher = new(
            @"root\cimv2",
            $"SELECT DeviceObject FROM Win32_ShadowCopy WHERE ID = '{shadowId}'");

        foreach (ManagementBaseObject item in searcher.Get())
        {
            using (item)
            {
                return (string)item["DeviceObject"];
            }
        }

        throw new InvalidOperationException($"Die angelegte Schattenkopie {shadowId} wurde nicht wiedergefunden.");
    }

    private static void DeleteShadow(string shadowId)
    {
        try
        {
            using ManagementObject shadow = new(@"root\cimv2", $"Win32_ShadowCopy.ID=\"{shadowId}\"", null);
            shadow.Delete();
            Log.Debug($"Schattenkopie {shadowId} entfernt.");
        }
        catch (Exception ex)
        {
            Log.Warn($"Die Schattenkopie {shadowId} blieb zurueck und sollte von Hand entfernt werden " +
                     $"(vssadmin delete shadows /shadow={shadowId}): {ex.Message}");
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        try
        {
            // Directory.Delete entfernt bei einem Symlink nur den Verweis, nicht das Ziel.
            if (Directory.Exists(MountPath))
            {
                Directory.Delete(MountPath);
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"Der Verweis {MountPath} liess sich nicht entfernen: {ex.Message}");
        }

        DeleteShadow(_shadowId);
    }

    private static string DescribeError(uint code) => code switch
    {
        1 => "Zugriff verweigert",
        2 => "Ungueltiges Argument",
        3 => "Angegebenes Volume nicht gefunden",
        4 => "Angegebenes Volume wird nicht unterstuetzt",
        5 => "Nicht unterstuetzter Schattenkopie-Kontext",
        6 => "Unzureichender Speicher",
        7 => "Volume ist in Benutzung",
        8 => "Maximale Anzahl Schattenkopien erreicht",
        9 => "Anderer Schattenkopie-Vorgang laeuft bereits",
        10 => "Provider-Fehler beim Anlegen der Schattenkopie",
        11 => "Unbekannter Fehler",
        _ => "nicht naeher beschrieben",
    };
}
