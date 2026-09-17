namespace IsoForge;

/// <summary>Sehr einfacher Parser fuer "--name wert" und "--schalter".</summary>
public sealed class CommandLine
{
    private readonly Dictionary<string, string?> _values = new(StringComparer.OrdinalIgnoreCase);

    private CommandLine(string? command, IReadOnlyList<string> positional)
    {
        Command = command;
        Positional = positional;
    }

    public string? Command { get; }

    public IReadOnlyList<string> Positional { get; }

    public static CommandLine Parse(string[] args)
    {
        string? command = args.Length > 0 && !args[0].StartsWith('-') ? args[0].ToLowerInvariant() : null;
        List<string> positional = new();
        Dictionary<string, string?> values = new(StringComparer.OrdinalIgnoreCase);

        for (int i = command is null ? 0 : 1; i < args.Length; i++)
        {
            string arg = args[i];
            if (!arg.StartsWith("--", StringComparison.Ordinal))
            {
                positional.Add(arg);
                continue;
            }

            string name = arg[2..];
            int equals = name.IndexOf('=');
            if (equals >= 0)
            {
                values[name[..equals]] = name[(equals + 1)..];
                continue;
            }

            bool hasValue = i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal);
            values[name] = hasValue ? args[++i] : null;
        }

        CommandLine result = new(command, positional);
        foreach ((string key, string? value) in values)
        {
            result._values[key] = value;
        }

        return result;
    }

    public bool Has(string name) => _values.ContainsKey(name);

    public string? Value(string name) => _values.TryGetValue(name, out string? value) ? value : null;

    public string Value(string name, string fallback) => Value(name) ?? fallback;

    public int Value(string name, int fallback) =>
        int.TryParse(Value(name), out int parsed) ? parsed : fallback;

    public IReadOnlyList<string> List(string name) =>
        Value(name)?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        ?? Array.Empty<string>();
}
