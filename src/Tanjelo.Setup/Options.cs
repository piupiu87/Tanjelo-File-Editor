namespace Tanjelo.Setup;

/// <summary>
/// Setup's command line, for winget and scripts: <c>--silent</c> installs (or uninstalls) with no window, just for
/// this user in the default folder unless <c>--all-users</c> or <c>--dir &lt;folder&gt;</c> say otherwise, with no
/// optional tools, and doesn't start Tanjelo. The rest is what setup passes to its own elevated copy.
/// </summary>
sealed class Options
{
    public bool Silent { get; private set; }
    public bool AllUsers { get; private set; }
    public string? Folder { get; private set; }

    /// <summary>The job of an elevated copy, with the pipe it reports on and the uninstaller in use, if any.</summary>
    public string? Elevated { get; private set; }
    public string? Pipe { get; private set; }
    public string? Running { get; private set; }

    /// <summary>The log to write to, when it isn't the default one.</summary>
    public string? Log { get; private set; }

    public static Options Parse(IReadOnlyList<string> args)
    {
        var options = new Options();
        for (var i = 0; i < args.Count; i++)
        {
            var (name, value) = args[i].IndexOf('=') is var equals and > 0 && args[i].StartsWith("--", StringComparison.Ordinal)
                ? (args[i].Substring(0, equals), args[i].Substring(equals + 1))
                : (args[i], null);
            string Value() => value ?? (i + 1 < args.Count ? args[++i] : throw new SetupException(string.Format(Strings.NeedsAValue, name), ExitCodes.BadCommandLine));
            switch (name.ToLowerInvariant())
            {
                case "--silent" or "/silent" or "-s" or "/s": options.Silent = true; break;
                case "--all-users": options.AllUsers = true; break;
                case "--dir": options.Folder = Value(); break;
                case "--elevated": options.Elevated = Value(); break;
                case "--pipe": options.Pipe = Value(); break;
                case "--running": options.Running = Value(); break;
                case "--log": options.Log = Value(); break;
                default: throw new SetupException(string.Format(Strings.SetupDoesnTKnow, args[i]), ExitCodes.BadCommandLine);
            }
        }
        if (options.Folder is { Length: 0 }) throw new SetupException(string.Format(Strings.NeedsAValue, "--dir"), ExitCodes.BadCommandLine);
        return options;
    }
}
