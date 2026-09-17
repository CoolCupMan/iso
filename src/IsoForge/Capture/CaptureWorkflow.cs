using System.Diagnostics;
using IsoForge.Core;
using IsoForge.Media;

namespace IsoForge.Capture;

public sealed class CaptureOptions
{
    public CaptureTier Tier { get; set; } = CaptureTier.Full;

    public string? CustomExclusionFile { get; set; }

    /// <summary>Zusaetzliche Datenlaufwerke neben dem Systemlaufwerk, z. B. "D", "E".</summary>
    public List<string> ExtraVolumes { get; } = new();

    public string OutputDirectory { get; set; } = Environment.CurrentDirectory;

    public string? OutputFileName { get; set; }

    public string? WorkDirectory { get; set; }

    public WimCompression Compression { get; set; } = WimCompression.Max;

    /// <summary>Ab dieser Groesse wird das Abbild aufgeteilt; ISO 9660 kann keine Datei ueber 4 GiB fassen.</summary>
    public int SplitSizeMegabytes { get; set; } = 3800;

    public string? OscdimgPath { get; set; }

    /// <summary>Welcher Weg das ISO schreibt; die Vorgabe waehlt selbst den besten verfuegbaren.</summary>
    public IsoEngine Engine { get; set; } = IsoEngine.Auto;

    public string? BootsectPath { get; set; }

    public string? WinPePath { get; set; }

    /// <summary>Verzeichnis mit Treibern (.inf), die bei der Wiederherstellung eingespielt werden.</summary>
    public string? DriversDirectory { get; set; }

    public bool KeepWorkFiles { get; set; }

    public bool AssumeYes { get; set; }
}

public sealed record CaptureSummary(
    string IsoPath,
    long IsoSizeBytes,
    string Sha256,
    string BuildId,
    CaptureTier Tier,
    TimeSpan Duration,
    string Method);

public sealed class CaptureWorkflow
{
    private readonly CaptureOptions _options;
    private readonly List<IDisposable> _cleanup = new();

    public CaptureWorkflow(CaptureOptions options) => _options = options;

    public CaptureSummary Run()
    {
        Stopwatch stopwatch = Stopwatch.StartNew();
        DateTimeOffset started = DateTimeOffset.Now;

        VolumeInfo systemVolume = VolumeScanner.SystemVolume();
        string work = PrepareWorkDirectory();
        string staging = Path.Combine(work, "media");
        string scratch = Path.Combine(work, "scratch");
        string mount = Path.Combine(work, "mount");
        string dismLog = Path.Combine(work, "dism.log");

        Directory.CreateDirectory(staging);
        Directory.CreateDirectory(scratch);

        string outputName = _options.OutputFileName ?? BuildOutputName(started);
        string outputPath = Path.Combine(_options.OutputDirectory, outputName);

        Preflight.CheckDiskSpace(_options.OutputDirectory, systemVolume, _options.ExtraVolumes);

        try
        {
            Privileges.EnableAll();

            // --- Systemlaufwerk sichern -----------------------------------------
            Log.Step($"Systemlaufwerk {systemVolume.DriveLetter}: wird gesichert");
            string imageName;
            bool split;
            List<string> capturedExtras = new();

            using (VssSnapshot snapshot = VssSnapshot.Create(systemVolume.Root, work))
            {
                _cleanup.Add(snapshot);

                CaptureProfile profile = new(_options.Tier, _options.CustomExclusionFile);
                string configFile = profile.WriteConfigFile(snapshot.MountPath, Path.Combine(work, "exclusions.ini"));

                string wimPath = Path.Combine(staging, "sources", "install.wim");
                DismRunner.CaptureImage(
                    captureDirectory: snapshot.MountPath + "\\",
                    imageFile: wimPath,
                    name: $"IsoForge {_options.Tier.ShortName()} {BuildIdentity.ShortId}",
                    description: $"{Environment.MachineName} {systemVolume.DriveLetter}: {started:yyyy-MM-dd HH:mm}",
                    compression: _options.Compression,
                    configFile: configFile,
                    scratchDirectory: scratch,
                    logPath: dismLog);

                long wimSize = new FileInfo(wimPath).Length;
                Log.Info($"Abbild fertig: {Format.Bytes(wimSize)}");

                // --- Bei Bedarf aufteilen ---------------------------------------
                if (wimSize > (long)_options.SplitSizeMegabytes * 1024 * 1024)
                {
                    Log.Step("Abbild wird in Teildateien zerlegt");
                    Log.Info($"ISO 9660 kann keine Datei ueber 4 GiB adressieren - es entstehen Teile zu " +
                             $"je hoechstens {_options.SplitSizeMegabytes} MB.");

                    string swmPath = Path.Combine(staging, "sources", "install.swm");
                    IReadOnlyList<string> parts = DismRunner.SplitImage(wimPath, swmPath, _options.SplitSizeMegabytes, dismLog);
                    File.Delete(wimPath);
                    Log.Info($"{parts.Count} Teildateien erzeugt.");
                    imageName = "install.swm";
                    split = true;
                }
                else
                {
                    imageName = "install.wim";
                    split = false;
                }

                // --- Startsystem aus der Wiederherstellungsumgebung ------------
                string? winre = _options.WinPePath ?? BootFileLocator.LocateWinRe(snapshot.MountPath);
                if (winre is null)
                {
                    throw new InvalidOperationException(
                        "Es wurde keine Windows-Wiederherstellungsumgebung gefunden, aus der sich das Startsystem " +
                        "bauen liesse. Aktivieren Sie sie mit 'reagentc /enable' oder geben Sie mit --winpe eine " +
                        "boot.wim an (z. B. \\sources\\boot.wim eines Windows-Installationsmediums).");
                }

                WinPeBuilder.Build(winre, Path.Combine(staging, "sources", "boot.wim"), mount, dismLog);
            }

            // --- Zusaetzliche Datenlaufwerke ------------------------------------
            foreach (string letter in _options.ExtraVolumes)
            {
                VolumeInfo volume = VolumeScanner.ByLetter(letter);
                Log.Step($"Datenlaufwerk {volume.DriveLetter}: wird gesichert");

                using VssSnapshot snapshot = VssSnapshot.Create(volume.Root, work);
                CaptureProfile profile = new(CaptureTier.Full);
                string configFile = profile.WriteConfigFile(
                    snapshot.MountPath, Path.Combine(work, $"exclusions_{volume.DriveLetter}.ini"));

                DismRunner.CaptureImage(
                    captureDirectory: snapshot.MountPath + "\\",
                    imageFile: Path.Combine(staging, "sources", $"data_{volume.DriveLetter}.wim"),
                    name: $"IsoForge data {volume.DriveLetter}",
                    description: $"{Environment.MachineName} {volume.DriveLetter}:",
                    compression: _options.Compression,
                    configFile: configFile,
                    scratchDirectory: scratch,
                    logPath: dismLog);

                capturedExtras.Add(volume.DriveLetter);
            }

            // --- Medium zusammenstellen ------------------------------------------
            BootFileSet bootFiles = BootFileLocator.Locate();
            MediaStager.StageBootFiles(staging, bootFiles, BuildIdentity.ShortId);
            bool hasBootsect = MediaStager.TryStageBootsect(staging, _options.BootsectPath);

            if (_options.DriversDirectory is not null && Directory.Exists(_options.DriversDirectory))
            {
                Log.Info($"Treiber aus {_options.DriversDirectory} werden auf das Medium gelegt.");
                CopyDirectory(_options.DriversDirectory, Path.Combine(staging, "drivers"));
            }

            MediaStager.WriteMetadata(staging, new MediaMetadata(
                BuildIdentity.BuildId,
                BuildIdentity.ShortId,
                _options.Tier,
                started,
                Environment.MachineName,
                systemVolume.DriveLetter + ":",
                imageName,
                split,
                capturedExtras,
                hasBootsect));

            // --- ISO schreiben -----------------------------------------------------
            IsoBuildResult iso = IsoBuilder.Build(
                staging,
                outputPath,
                BuildIdentity.VolumeLabel(_options.Tier.ShortName(), started),
                bootFiles,
                _options.OscdimgPath,
                _options.Engine,
                work);

            stopwatch.Stop();
            return new CaptureSummary(
                iso.Path, iso.SizeBytes, iso.Sha256, BuildIdentity.ShortId, _options.Tier, stopwatch.Elapsed, iso.Method);
        }
        finally
        {
            foreach (IDisposable disposable in Enumerable.Reverse(_cleanup))
            {
                try
                {
                    disposable.Dispose();
                }
                catch (Exception ex)
                {
                    Log.Warn($"Aufraeumen fehlgeschlagen: {ex.Message}");
                }
            }

            _cleanup.Clear();

            if (!_options.KeepWorkFiles)
            {
                RemoveWorkDirectory(work);
            }
            else
            {
                Log.Info($"Arbeitsdateien bleiben erhalten: {work}");
            }
        }
    }

    private string BuildOutputName(DateTimeOffset started) =>
        $"IsoForge-{_options.Tier.ShortName()}-{Sanitize(Environment.MachineName)}-" +
        $"{started:yyyyMMdd-HHmm}-{BuildIdentity.ShortId}.iso";

    private static string Sanitize(string value) =>
        new(value.Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray());

    private string PrepareWorkDirectory()
    {
        string work = _options.WorkDirectory
                      ?? Path.Combine(_options.OutputDirectory, $".isoforge-{BuildIdentity.ShortId}");

        if (Directory.Exists(work))
        {
            Log.Warn($"Das Arbeitsverzeichnis {work} existiert bereits und wird geleert.");
            RemoveWorkDirectory(work);
        }

        Directory.CreateDirectory(work);
        Log.Info($"Arbeitsverzeichnis: {work}");
        return work;
    }

    private static void RemoveWorkDirectory(string path)
    {
        if (!Directory.Exists(path))
        {
            return;
        }

        try
        {
            // Etwaige Verzeichnis-Verweise auf Schattenkopien zuerst entfernen, damit nicht versehentlich
            // in die Schattenkopie hinein geloescht wird.
            foreach (string entry in Directory.GetDirectories(path))
            {
                FileInfo info = new(entry);
                if (info.LinkTarget is not null)
                {
                    Directory.Delete(entry);
                }
            }

            Directory.Delete(path, recursive: true);
        }
        catch (Exception ex)
        {
            Log.Warn($"Das Arbeitsverzeichnis {path} liess sich nicht vollstaendig entfernen: {ex.Message}");
        }
    }

    private static void CopyDirectory(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (string file in Directory.GetFiles(source))
        {
            File.Copy(file, Path.Combine(target, Path.GetFileName(file)), overwrite: true);
        }

        foreach (string directory in Directory.GetDirectories(source))
        {
            CopyDirectory(directory, Path.Combine(target, Path.GetFileName(directory)));
        }
    }
}
