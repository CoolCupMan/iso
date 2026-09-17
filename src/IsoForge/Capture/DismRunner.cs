using System.Globalization;
using System.Text.RegularExpressions;
using IsoForge.Core;

namespace IsoForge.Capture;

public enum WimCompression
{
    None,
    Fast,
    Max,
}

/// <summary>Kapselt die Aufrufe von DISM, die IsoForge braucht, samt Fortschrittsanzeige.</summary>
public static partial class DismRunner
{
    private static readonly string DismPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.System), "dism.exe");

    [GeneratedRegex(@"(\d{1,3}[.,]\d)%")]
    private static partial Regex PercentPattern();

    public static string ExecutablePath => DismPath;

    public static void EnsureAvailable()
    {
        if (!File.Exists(DismPath))
        {
            throw new FileNotFoundException(
                $"dism.exe wurde unter {DismPath} nicht gefunden. IsoForge setzt ein regulaeres Windows voraus.");
        }
    }

    public static string Version()
    {
        ProcessResult result = ProcessRunner.Run(DismPath, "/English /?");
        Match match = Regex.Match(result.StandardOutput, @"Version:\s*([\d.]+)");
        return match.Success ? match.Groups[1].Value : "unbekannt";
    }

    public static void CaptureImage(
        string captureDirectory,
        string imageFile,
        string name,
        string description,
        WimCompression compression,
        string configFile,
        string scratchDirectory,
        string logPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(imageFile)!);
        Directory.CreateDirectory(scratchDirectory);

        string arguments =
            $"/English /Capture-Image /ImageFile:\"{imageFile}\" /CaptureDir:\"{captureDirectory}\" " +
            $"/Name:\"{name}\" /Description:\"{description}\" /Compress:{compression.ToString().ToLowerInvariant()} " +
            $"/ConfigFile:\"{configFile}\" /ScratchDir:\"{scratchDirectory}\" /LogPath:\"{logPath}\"";

        RunWithProgress(arguments, "Sicherung laeuft");
    }

    public static IReadOnlyList<string> SplitImage(string imageFile, string swmFile, int fileSizeMegabytes, string logPath)
    {
        string arguments =
            $"/English /Split-Image /ImageFile:\"{imageFile}\" /SWMFile:\"{swmFile}\" " +
            $"/FileSize:{fileSizeMegabytes} /LogPath:\"{logPath}\"";

        RunWithProgress(arguments, "Abbild wird aufgeteilt");

        string directory = Path.GetDirectoryName(swmFile)!;
        string stem = Path.GetFileNameWithoutExtension(swmFile);
        return Directory.GetFiles(directory, stem + "*.swm")
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public static void MountImage(string imageFile, int index, string mountDirectory, bool readOnly, string logPath)
    {
        Directory.CreateDirectory(mountDirectory);
        string arguments =
            $"/English /Mount-Wim /WimFile:\"{imageFile}\" /Index:{index} /MountDir:\"{mountDirectory}\" " +
            $"/LogPath:\"{logPath}\"" + (readOnly ? " /ReadOnly" : string.Empty);

        RunWithProgress(arguments, "Abbild wird eingehaengt");
    }

    public static void UnmountImage(string mountDirectory, bool commit, string logPath)
    {
        string arguments =
            $"/English /Unmount-Wim /MountDir:\"{mountDirectory}\" {(commit ? "/Commit" : "/Discard")} /LogPath:\"{logPath}\"";

        RunWithProgress(arguments, commit ? "Aenderungen werden uebernommen" : "Einhaengung wird verworfen");
    }

    /// <summary>Raeumt Einhaengungen auf, die ein abgebrochener Lauf hinterlassen hat.</summary>
    public static void CleanupMountPoints()
    {
        ProcessResult result = ProcessRunner.Run(DismPath, "/English /Cleanup-Wim");
        if (!result.Success)
        {
            Log.Debug($"Aufraeumen der Einhaengepunkte meldete Code {result.ExitCode}.");
        }
    }

    private static void RunWithProgress(string arguments, string caption)
    {
        int lastPercent = -1;
        bool wroteProgress = false;

        ProcessResult result = ProcessRunner.RunStreaming(DismPath, arguments, segment =>
        {
            Match match = PercentPattern().Match(segment);
            if (!match.Success)
            {
                string trimmed = segment.Trim();
                if (trimmed.Length > 0 && !trimmed.StartsWith('[') && !trimmed.StartsWith("Deployment Image", StringComparison.Ordinal))
                {
                    if (wroteProgress)
                    {
                        Console.WriteLine();
                        wroteProgress = false;
                    }

                    Log.Debug(trimmed);
                }

                return;
            }

            double percent = double.Parse(match.Groups[1].Value.Replace(',', '.'), CultureInfo.InvariantCulture);
            int rounded = (int)percent;
            if (rounded == lastPercent)
            {
                return;
            }

            lastPercent = rounded;
            wroteProgress = true;
            int filled = rounded * 40 / 100;
            Console.Write($"\r  {caption}: [{new string('#', filled)}{new string('.', 40 - filled)}] {rounded,3}%");
        });

        if (wroteProgress)
        {
            Console.WriteLine();
        }

        if (!result.Success)
        {
            throw new InvalidOperationException(
                $"DISM endete mit Code {result.ExitCode} (0x{result.ExitCode:X8}).{Environment.NewLine}{result.CombinedOutput}");
        }
    }
}
