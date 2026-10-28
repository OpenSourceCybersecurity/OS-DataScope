namespace DataScope.Setup;

/// <summary>"command --name value --flag" with repeatable options. Deliberately tiny: the installer script is the only caller.</summary>
internal sealed class CommandLine
{
    private readonly Dictionary<string, List<string>> _options = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _flags = new(StringComparer.OrdinalIgnoreCase);

    public string Command { get; }

    private CommandLine(string command) => Command = command;

    public static CommandLine Parse(IReadOnlyList<string> args, IReadOnlySet<string> flagNames)
    {
        if (args.Count == 0 || args[0].StartsWith("--", StringComparison.Ordinal))
        {
            throw new UsageException("Missing command.");
        }
        var line = new CommandLine(args[0].ToLowerInvariant());
        for (var i = 1; i < args.Count; i++)
        {
            var arg = args[i];
            if (!arg.StartsWith("--", StringComparison.Ordinal) || arg.Length < 3)
            {
                throw new UsageException($"Unexpected argument '{arg}'.");
            }
            var name = arg[2..];
            if (flagNames.Contains(name))
            {
                line._flags.Add(name);
                continue;
            }
            if (i + 1 >= args.Count)
            {
                throw new UsageException($"--{name} needs a value.");
            }
            if (!line._options.TryGetValue(name, out var values))
            {
                line._options[name] = values = [];
            }
            values.Add(args[++i]);
        }
        return line;
    }

    public bool Flag(string name) => _flags.Contains(name);

    public IReadOnlyList<string> All(string name) => _options.TryGetValue(name, out var v) ? v : [];

    public string? Optional(string name) =>
        All(name) switch
        {
            [] => null,
            [var one] => one,
            _ => throw new UsageException($"--{name} was given more than once."),
        };

    public string Get(string name, string fallback) => Optional(name) ?? fallback;

    public int GetInt(string name, int fallback, int min, int max)
    {
        var text = Optional(name);
        if (text is null)
        {
            return fallback;
        }
        return int.TryParse(text, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var n) && n >= min && n <= max
            ? n
            : throw new UsageException($"--{name} must be a whole number from {min} to {max}.");
    }

    public void RejectUnknown(params string[] known)
    {
        var unknown = _options.Keys.Concat(_flags).Where(k => !known.Contains(k, StringComparer.OrdinalIgnoreCase)).ToList();
        if (unknown.Count > 0)
        {
            throw new UsageException($"Unknown option --{unknown[0]} for '{Command}'.");
        }
    }
}

internal sealed class UsageException(string message) : Exception(message);
