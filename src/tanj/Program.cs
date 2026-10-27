using Tanjelo.Core;

// tanj: command-line front end to the same engine the tray app uses.
//   tanj tools                                 show which external tools were found
//   tanj targets <files...>                    list what the selection can become, and the tools and actions for it
//   tanj options <key> <files...>              explain the settings a tool takes
//   tanj convert <key> [--name=value] <files>  run one of those, writing outputs next to the inputs

if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
{
    Console.WriteLine("""
        usage:
          tanj tools
          tanj targets <files...>
          tanj options <key> <files...>
          tanj convert <key> [--name=value...] <files...>

        --name=value overrides a tool's setting, e.g. tanj convert split-video-2 --parts=5 clip.mp4
        tanj options explains the settings of tools that ask for them, such as compress.
        """);
    return 0;
}

// Labels use characters such as × and …, which the console's default OEM code page can't show.
Console.OutputEncoding = System.Text.Encoding.UTF8;
ExitGuard.EndWhenMainReturns();
var engine = new ConverterEngine(Toolbox.Detect());

switch (args[0])
{
    case "tools":
        var t = engine.Tools;
        Console.WriteLine($"ffmpeg      {t.Ffmpeg ?? "-"}");
        Console.WriteLine($"ffprobe     {t.Ffprobe ?? "-"}");
        Console.WriteLine($"7-Zip       {t.SevenZip ?? "-"}");
        Console.WriteLine($"tar         {t.Tar ?? "-"}");
        Console.WriteLine($"LibreOffice {t.LibreOffice ?? "-"}");
        Console.WriteLine($"Office      Word={t.Word} PowerPoint={t.PowerPoint} Excel={t.Excel}");
        Console.WriteLine($"WebView2    {t.WebView2 ?? "-"}");
        Console.WriteLine($"pandoc      {t.Pandoc ?? "-"}");
        Console.WriteLine($"HEIC output {(t.Heic ? "Windows HEIF encoder" : "-")}");
        Console.WriteLine($"OCR         {(t.Ocr ? "Windows OCR" : "-")}");
        return 0;

    case "targets" when args.Length > 1:
    {
        if (Inputs(args[1..]) is not { } selection) return 2;
        var menu = engine.GetMenu(selection);
        foreach (var group in menu.Targets.GroupBy(x => (x.Kind, x.Section)).OrderBy(g => g.Key.Kind))
        {
            Console.WriteLine($"{group.Key.Kind} / {group.Key.Section}");
            foreach (var target in group)
            {
                var settings = string.Concat(target.Parameters.Select(p => $" [--{p.Key}={p.Value}]"));
                Console.WriteLine($"  {target.Key,-16} {target.Label,-22} {target.Description}{settings}");
            }
        }
        foreach (var hint in menu.Hints)
            Console.WriteLine($"note: {hint}");
        return 0;
    }

    case "options" when args.Length > 2:
    {
        if (Inputs(args[2..]) is not { } selection) return 2;
        var target = Find(args[1], selection);
        if (target is null) return 2;
        Console.WriteLine($"{target.Key}: {target.Label}  {target.Description}");
        if (target.Options.Count == 0)
        {
            var settings = string.Join(", ", target.Parameters.Select(p => $"--{p.Key}={p.Value}"));
            Console.WriteLine(settings.Length == 0 ? "  It takes no settings." : $"  Settings: {settings}");
            return 0;
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
                _ => option.Optional ? "text, or empty" : "text",
            };
            var when = option.ShownWhen is { } w ? $"  (with {ShownWhen(w)})" : "";
            Console.WriteLine($"  {$"--{option.Name}={target.Parameters[option.Name]}",-22} {option.Label}: {allowed}{when}");
            if (option.Hint is { } hint)
                Console.WriteLine($"  {"",-22} {hint}");
        }
        return 0;
    }

    case "convert" when args.Length > 2:
    {
        var options = args[2..].TakeWhile(a => a.StartsWith("--", StringComparison.Ordinal)).ToList();
        if (Inputs(args[(2 + options.Count)..]) is not { } inputs) return 2;
        var target = Find(args[1], inputs);
        if (target is null) return 2;

        var values = new Dictionary<string, string>();
        foreach (var option in options)
        {
            var (name, value) = option.IndexOf('=') is var eq and > 2 ? (option[2..eq], option[(eq + 1)..]) : (option[2..], "");
            // A switch on its own, such as --faces, turns it on.
            if (!option.Contains('=') && target.Options.Any(o => o.Name == name && o.Kind == OptionKind.Toggle)) value = "true";
            if (!target.Parameters.ContainsKey(name))
            {
                var known = target.Parameters.Count == 0 ? "it has no settings" : "it takes " + string.Join(", ", target.Parameters.Keys.Select(k => "--" + k));
                Console.Error.WriteLine($"{target.Key} doesn't take --{name}; {known}.");
                return 2;
            }
            values[name] = value;
        }
        target = target.WithParameters(values);
        foreach (var option in target.Options.Where(o => o.IsShown(target.Parameters)))
        {
            if (option.Problem(target.Parameters[option.Name]) is { } problem)
            {
                Console.Error.WriteLine($"--{option.Name} ({option.Label}) {problem}. Run: tanj options {target.Key} <files>");
                return 2;
            }
        }
        if (target.FolderPrompt is not null && target.Parameters.GetValueOrDefault("folder") is null or "")
        {
            Console.Error.WriteLine($"{target.Key} needs a destination: tanj convert {target.Key} --folder=<path> <files...>");
            return 2;
        }

        using var cancel = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancel.Cancel(); };
        var lastPercent = -1;
        var progress = new ActionProgress<BatchProgress>(p =>
        {
            if (double.IsNaN(p.Fraction)) return;
            var percent = (int)(p.Fraction * 100);
            if (percent / 10 == lastPercent / 10) return;
            lastPercent = percent;
            Console.Error.Write($"\r{percent,3}%  {p.Current}");
        });

        var watch = System.Diagnostics.Stopwatch.StartNew();
        var result = await engine.RunAsync(inputs, target, progress, cancel.Token);
        Console.Error.WriteLine();
        foreach (var output in result.Outputs)
            Console.WriteLine($"ok    {output}");
        if (target.Kind == TargetKind.Action && result.Outputs.Count == 0 && result.Failures.Count == 0 && !result.Canceled)
            Console.WriteLine($"ok    {target.DoneText}");
        foreach (var failure in result.Failures)
            Console.WriteLine($"FAIL  {failure.Input}: {failure.Message}");
        foreach (var skipped in result.Skipped)
            Console.WriteLine($"skip  {skipped} (already {target.Label})");
        foreach (var note in result.Notes)
            Console.WriteLine($"note  {note}");
        if (result.Canceled)
            Console.WriteLine("canceled");
        Console.Error.WriteLine($"{watch.Elapsed.TotalSeconds:0.0}s");
        return result.Failures.Count == 0 && !result.Canceled ? 0 : 1;
    }

    default:
        Console.Error.WriteLine("unknown command; run tanj --help");
        return 2;
}

static string ShownWhen(Condition condition) =>
    $"--{condition.Name}={string.Join("|", condition.Values)}" + (condition.Or is { } or ? " or " + ShownWhen(or) : "");

Target? Find(string key, IReadOnlyList<string> inputs)
{
    var target = engine.GetMenu(inputs).Targets.FirstOrDefault(x => string.Equals(x.Key, key, StringComparison.OrdinalIgnoreCase));
    if (target is null)
        Console.Error.WriteLine($"'{key}' isn't available for this selection. Run: tanj targets <files>");
    return target;
}

// The full paths, or null (with the reason printed) when one is missing or none were given.
static List<string>? Inputs(string[] paths)
{
    if (paths.Length == 0)
    {
        Console.Error.WriteLine("No files given.");
        return null;
    }
    var full = paths.Select(Path.GetFullPath).ToList();
    var missing = full.Where(p => !File.Exists(p) && !Directory.Exists(p)).ToList();
    if (missing.Count > 0)
    {
        Console.Error.WriteLine((missing.Count == 1 ? "Not found: " : "These weren't found: ") + string.Join(", ", missing));
        return null;
    }
    return full;
}
