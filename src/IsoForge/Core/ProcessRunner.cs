using System.Diagnostics;
using System.Text;

namespace IsoForge.Core;

public sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError)
{
    public bool Success => ExitCode == 0;

    public string CombinedOutput => (StandardOutput + Environment.NewLine + StandardError).Trim();
}

public static class ProcessRunner
{
    /// <summary>Startet ein Programm, sammelt die Ausgabe und reicht sie optional zeilenweise weiter.</summary>
    public static ProcessResult Run(
        string fileName,
        string arguments,
        Action<string>? onOutputLine = null,
        string? workingDirectory = null,
        TimeSpan? timeout = null)
    {
        Log.Debug($"> {fileName} {arguments}");

        ProcessStartInfo info = new()
        {
            FileName = fileName,
            Arguments = arguments,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        if (workingDirectory is not null)
        {
            info.WorkingDirectory = workingDirectory;
        }

        using Process process = new() { StartInfo = info };

        StringBuilder standardOutput = new();
        StringBuilder standardError = new();
        using ManualResetEventSlim outputDone = new(false);
        using ManualResetEventSlim errorDone = new(false);

        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is null)
            {
                outputDone.Set();
                return;
            }

            standardOutput.AppendLine(e.Data);
            onOutputLine?.Invoke(e.Data);
        };

        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null)
            {
                errorDone.Set();
                return;
            }

            standardError.AppendLine(e.Data);
        };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        if (timeout is { } limit)
        {
            if (!process.WaitForExit((int)limit.TotalMilliseconds))
            {
                TryKill(process);
                throw new TimeoutException($"'{fileName}' hat das Zeitlimit von {limit} ueberschritten.");
            }
        }
        else
        {
            process.WaitForExit();
        }

        outputDone.Wait(TimeSpan.FromSeconds(5));
        errorDone.Wait(TimeSpan.FromSeconds(5));

        return new ProcessResult(process.ExitCode, standardOutput.ToString(), standardError.ToString());
    }

    /// <summary>
    /// Wie <see cref="Run"/>, liest die Standardausgabe aber zeichenweise. DISM aktualisiert seinen
    /// Fortschrittsbalken mit Wagenruecklaeufen statt Zeilenumbruechen - zeilenweises Lesen wuerde die
    /// Ausgabe bis zum Ende zurueckhalten.
    /// </summary>
    public static ProcessResult RunStreaming(string fileName, string arguments, Action<string> onSegment)
    {
        Log.Debug($"> {fileName} {arguments}");

        ProcessStartInfo info = new()
        {
            FileName = fileName,
            Arguments = arguments,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        using Process process = new() { StartInfo = info };
        StringBuilder standardOutput = new();
        StringBuilder standardError = new();

        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null)
            {
                standardError.AppendLine(e.Data);
            }
        };

        process.Start();
        process.BeginErrorReadLine();

        StringBuilder segment = new();
        StreamReader reader = process.StandardOutput;
        int value;
        while ((value = reader.Read()) >= 0)
        {
            char c = (char)value;
            if (c is '\r' or '\n')
            {
                if (segment.Length > 0)
                {
                    string text = segment.ToString();
                    standardOutput.AppendLine(text);
                    onSegment(text);
                    segment.Clear();
                }

                continue;
            }

            segment.Append(c);
        }

        if (segment.Length > 0)
        {
            standardOutput.AppendLine(segment.ToString());
            onSegment(segment.ToString());
        }

        process.WaitForExit();
        return new ProcessResult(process.ExitCode, standardOutput.ToString(), standardError.ToString());
    }

    public static ProcessResult RunOrThrow(string fileName, string arguments, Action<string>? onOutputLine = null)
    {
        ProcessResult result = Run(fileName, arguments, onOutputLine);
        if (!result.Success)
        {
            throw new InvalidOperationException(
                $"'{Path.GetFileName(fileName)}' endete mit Code {result.ExitCode}.{Environment.NewLine}{result.CombinedOutput}");
        }

        return result;
    }

    private static void TryKill(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (Exception ex)
        {
            Log.Debug($"Prozess liess sich nicht beenden: {ex.Message}");
        }
    }
}
