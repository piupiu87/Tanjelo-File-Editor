using Tanjelo.Core;

// tanj: command-line front end to the same engine the tray app uses.
//   tanj tools                                 show which external tools were found
//   tanj targets <files...>                    list what the selection can become, and the tools and actions for it
//   tanj convert <key> [--name=value] <files>  run one of those, writing outputs next to the inputs

if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
{
    Console.WriteLine("""
        usage:
          tanj tools
          tanj targets <files...>
          tanj convert <key> [--name=value...] <files...>

        --name=value overrides a tool's setting, e.g. tanj convert split-video-2 --parts=5 clip.mp4
        """);
    return 0;
}

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
        return 0;

    case "targets" when args.Length > 1:
    {
        var menu = engine.GetMenu(Inputs(args[1..]));
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

    case "convert" when args.Length > 2:
    {
        var options = args[2..].TakeWhile(a => a.StartsWith("--", StringComparison.Ordinal)).ToList();
        var inputs = Inputs(args[(2 + options.Count)..]);
        var target = engine.GetMenu(inputs).Targets.FirstOrDefault(x => string.Equals(x.Key, args[1], StringComparison.OrdinalIgnoreCase));
        if (target is null)
        {
            Console.Error.WriteLine($"'{args[1]}' isn't available for this selection. Run: tanj targets <files>");
            return 2;
        }

        var values = new Dictionary<string, string>();
        foreach (var option in options)
        {
            var (name, value) = option.IndexOf('=') is var eq and > 2 ? (option[2..eq], option[(eq + 1)..]) : (option[2..], "");
            if (!target.Parameters.ContainsKey(name))
            {
                var known = target.Parameters.Count == 0 ? "it has no settings" : "it takes " + string.Join(", ", target.Parameters.Keys.Select(k => "--" + k));
                Console.Error.WriteLine($"{target.Key} doesn't take --{name}; {known}.");
                return 2;
            }
            values[name] = value;
        }
        target = target.WithParameters(values);
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
        if (result.Canceled)
            Console.WriteLine("canceled");
        Console.Error.WriteLine($"{watch.Elapsed.TotalSeconds:0.0}s");
        return result.Failures.Count == 0 && !result.Canceled ? 0 : 1;
    }

    default:
        Console.Error.WriteLine("unknown command; run tanj --help");
        return 2;
}

static List<string> Inputs(string[] paths)
{
    var full = paths.Select(Path.GetFullPath).ToList();
    var missing = full.Where(p => !File.Exists(p) && !Directory.Exists(p)).ToList();
    if (missing.Count > 0)
        throw new FileNotFoundException("Not found: " + string.Join(", ", missing));
    return full;
}
