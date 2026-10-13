namespace ProjectK.Catalog.Cli;

/// <summary>"--name value", "--flag" and positional arguments; enough for a handful of commands.</summary>
internal sealed class Options
{
    private readonly Dictionary<string, string?> _named = new(StringComparer.Ordinal);
    private readonly List<string> _positional = [];

    public static Options Parse(string[] args)
    {
        var options = new Options();
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i].StartsWith("--", StringComparison.Ordinal))
            {
                var name = args[i][2..];
                var hasValue = i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal);
                options._named[name] = hasValue ? args[++i] : null;
            }
            else
            {
                options._positional.Add(args[i]);
            }
        }

        return options;
    }

    public string Required(string name)
    {
        return _named.TryGetValue(name, out var value) && value is not null
            ? value
            : throw new ArgumentException($"--{name} is required.");
    }

    public string? Optional(string name) => _named.GetValueOrDefault(name);

    public bool Flag(string name) => _named.ContainsKey(name);

    public string Positional(int index)
    {
        return index < _positional.Count ? _positional[index] : throw new ArgumentException("A path is required.");
    }
}
