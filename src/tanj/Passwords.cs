using System.Text;
using Tanjelo.Core;

namespace Tanj;

/// <summary>
/// How tanj gets a tool's passwords. <c>--password=-</c> reads one line from standard input; left out, tanj asks for
/// it with the typing hidden (twice for a new password); typed on the command line it works, with a warning that it
/// stays in the shell's history. Passwords are never printed.
/// </summary>
static class Passwords
{
    /// <summary>The value that means "read it from standard input".</summary>
    public const string FromInput = "-";

    /// <summary>Fills in the passwords the tool will use, in the order it lists them; false, with the reason written, when one can't be had.</summary>
    /// <param name="values">What the command line gave, by option name; updated in place.</param>
    /// <param name="input">Standard input, for "-".</param>
    /// <param name="ask">Asks with the typing hidden; null when nobody can be asked, such as when input is redirected.</param>
    public static bool Fill(Target target, Dictionary<string, string> values, TextReader input, TextWriter error, Func<string, string?>? ask)
    {
        var settings = target.WithParameters(values).Parameters;
        foreach (var option in target.Options.Where(o => o.Kind == OptionKind.Password && o.IsShown(settings)))
        {
            if (values.TryGetValue(option.Name, out var given))
            {
                if (given == FromInput)
                {
                    if (input.ReadLine() is not { } line)
                    {
                        error.WriteLine($"--{option.Name}=- found nothing on standard input.");
                        return false;
                    }
                    values[option.Name] = line;
                }
                else
                {
                    error.WriteLine($"note: --{option.Name} typed on the command line stays in the shell's history; --{option.Name}=- reads it from standard input instead.");
                }
                continue;
            }
            if (ask is null)
            {
                if (option.Optional) continue;
                error.WriteLine($"--{option.Name} ({option.Label}) is needed; --{option.Name}=- reads it from standard input.");
                return false;
            }
            if (ask($"{option.Label}{(option.Optional ? " (Enter for none)" : "")}: ") is not { } typed) return false;
            if (option.Repeat && typed.Length > 0 && ask("Repeat it: ") != typed)
            {
                error.WriteLine("The two passwords differ; nothing was done.");
                return false;
            }
            values[option.Name] = typed;
        }
        return true;
    }

    /// <summary>Reads a line from the console without showing it; the prompt goes to standard error, so output stays clean.</summary>
    public static string? AskHidden(string prompt)
    {
        Console.Error.Write(prompt);
        var typed = new StringBuilder();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter) break;
            if (key.Key == ConsoleKey.Backspace)
            {
                if (typed.Length > 0) typed.Length--;
            }
            else if (!char.IsControl(key.KeyChar))
            {
                typed.Append(key.KeyChar);
            }
        }
        Console.Error.WriteLine();
        return typed.ToString();
    }
}

/// <summary>What <c>tanj options</c> prints about a tool's settings.</summary>
static class OptionList
{
    public static void Write(Target target, TextWriter output)
    {
        output.WriteLine($"{target.Key}: {target.Label}  {target.Description}");
        if (target.Options.Count == 0)
        {
            var settings = string.Join(", ", target.Parameters.Select(p => $"--{p.Key}={p.Value}"));
            output.WriteLine(settings.Length == 0 ? "  It takes no settings." : $"  Settings: {settings}");
            return;
        }
        foreach (var option in target.Options)
        {
            var allowed = option.Kind switch
            {
                OptionKind.Number => $"{OptionValues.Format(option.Min)} to {OptionValues.Format(option.Max)}{(option.Unit is { } unit ? " " + unit : "")}",
                OptionKind.Choice => string.Join(", ", option.Choices.Select(c => $"{c.Value} ({c.Label})")),
                OptionKind.Toggle => "true or false",
                OptionKind.Colour => "#RRGGBB, or #RRGGBBAA with transparency",
                OptionKind.File => option.Optional ? "a file, or empty" : "a file",
                OptionKind.TimeRange => "from-to, such as 0:30-1:45; empty for all of it",
                OptionKind.Times => "times such as 0:05,1:12.5",
                OptionKind.Order => "each file's place in the selection, such as 2,1,3",
                OptionKind.Password => $"a password{(option.Optional ? ", or empty" : "")}; - reads it from standard input, and left out, tanj asks for it",
                _ => option.Optional ? "text, or empty" : "text",
            };
            var when = option.ShownWhen is { } w ? $"  (with {ShownWhen(w)})" : "";
            // A password is never printed, not even a default.
            var value = option.Kind == OptionKind.Password ? "" : target.Parameters[option.Name];
            output.WriteLine($"  {$"--{option.Name}={value}",-22} {option.Label}: {allowed}{when}");
            if (option.Hint is { } hint)
                output.WriteLine($"  {"",-22} {hint}");
            if (option.Presets.Count > 0)
                output.WriteLine($"  {"",-22} Such as: {string.Join(", ", option.Presets.Select(p => $"\"{p.Value}\""))}");
        }
    }

    public static string ShownWhen(Condition condition) =>
        (condition.Contains ? $"{string.Join(" or ", condition.Values)} in --{condition.Name}" : $"--{condition.Name}={string.Join("|", condition.Values)}")
        + (condition.Or is { } or ? " or " + ShownWhen(or) : "");
}
