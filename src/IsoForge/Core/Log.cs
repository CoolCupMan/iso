namespace IsoForge.Core;

public enum LogLevel
{
    Debug,
    Info,
    Warn,
    Error,
}

/// <summary>Einfaches Protokoll auf Konsole und Datei. Der Konsolenteil bleibt bewusst schlicht.</summary>
public static class Log
{
    private static readonly object Gate = new();
    private static StreamWriter? _file;

    public static LogLevel MinimumLevel { get; set; } = LogLevel.Info;

    public static string? FilePath { get; private set; }

    public static void OpenFile(string path)
    {
        lock (Gate)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            _file = new StreamWriter(new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read))
            {
                AutoFlush = true,
            };
            FilePath = path;
        }
    }

    public static void Close()
    {
        lock (Gate)
        {
            _file?.Dispose();
            _file = null;
        }
    }

    public static void Debug(string message) => Write(LogLevel.Debug, message);

    public static void Info(string message) => Write(LogLevel.Info, message);

    public static void Warn(string message) => Write(LogLevel.Warn, message);

    public static void Error(string message) => Write(LogLevel.Error, message);

    public static void Step(string message)
    {
        lock (Gate)
        {
            Console.WriteLine();
            ConsoleColor previous = Console.ForegroundColor;
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine($"==> {message}");
            Console.ForegroundColor = previous;
            _file?.WriteLine($"{DateTime.Now:HH:mm:ss} ==> {message}");
        }
    }

    private static void Write(LogLevel level, string message)
    {
        lock (Gate)
        {
            _file?.WriteLine($"{DateTime.Now:HH:mm:ss} [{level,-5}] {message}");

            if (level < MinimumLevel)
            {
                return;
            }

            ConsoleColor previous = Console.ForegroundColor;
            Console.ForegroundColor = level switch
            {
                LogLevel.Debug => ConsoleColor.DarkGray,
                LogLevel.Warn => ConsoleColor.Yellow,
                LogLevel.Error => ConsoleColor.Red,
                _ => previous,
            };

            string prefix = level switch
            {
                LogLevel.Warn => "  ! ",
                LogLevel.Error => "  X ",
                LogLevel.Debug => "    ",
                _ => "  - ",
            };

            Console.WriteLine(prefix + message);
            Console.ForegroundColor = previous;
        }
    }
}
