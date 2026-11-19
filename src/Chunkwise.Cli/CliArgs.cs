namespace Chunkwise.Cli;

/// <summary>
/// A tiny <c>--flag value</c> parser. System.CommandLine is still in preview, and for a handful of
/// options a dictionary is easier to read than a framework.
/// </summary>
public sealed class CliArgs
{
    private readonly Dictionary<string, string?> _options = new(StringComparer.OrdinalIgnoreCase);

    public CliArgs(IReadOnlyList<string> args)
    {
        var positional = new List<string>();
        for (int i = 0; i < args.Count; i++)
        {
            var arg = args[i];
            if (!arg.StartsWith("--", StringComparison.Ordinal))
            {
                positional.Add(arg);
                continue;
            }

            var name = arg[2..];
            int equals = name.IndexOf('=');
            if (equals >= 0)
                _options[name[..equals]] = name[(equals + 1)..];
            else if (i + 1 < args.Count && !args[i + 1].StartsWith("--", StringComparison.Ordinal))
                _options[name] = args[++i];
            else
                _options[name] = null;   // boolean switch
        }

        Command = positional.Count > 0 ? positional[0].ToLowerInvariant() : "eval";
        Positional = positional.Skip(1).ToList();
    }

    public string Command { get; }
    public IReadOnlyList<string> Positional { get; }

    public bool Has(string name) => _options.ContainsKey(name);

    public string? Get(string name) => _options.TryGetValue(name, out var value) ? value : null;

    public string Get(string name, string fallback) => Get(name) ?? fallback;

    public int GetInt(string name, int fallback) =>
        Get(name) is { } raw
            ? int.TryParse(raw, out var value) ? value : throw new ArgumentException($"--{name} expects a whole number, got '{raw}'.")
            : fallback;

    public double GetDouble(string name, double fallback) =>
        Get(name) is { } raw
            ? double.TryParse(raw, System.Globalization.CultureInfo.InvariantCulture, out var value) ? value : throw new ArgumentException($"--{name} expects a number, got '{raw}'.")
            : fallback;
}
