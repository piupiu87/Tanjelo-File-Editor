using System.Text.Json;
using Tanjelo.Core;

namespace Tanjelo;

/// <summary>
/// The settings each tool was last run with, in %APPDATA%\Tanjelo\tools.json, so its options dialog opens with
/// them. Only options marked <see cref="Option.Remember"/> are kept, and values that no longer fit are dropped.
/// </summary>
sealed class ToolMemory(string path)
{
    public static string DefaultPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Tanjelo", "tools.json");

    static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    /// <summary>The remembered values for a tool's options that are still valid for it.</summary>
    public IReadOnlyDictionary<string, string> Recall(Target target)
    {
        var all = Read();
        if (!all.TryGetValue(target.Key, out var saved)) return new Dictionary<string, string>();
        return target.Options
            .Where(o => o.Remember && saved.TryGetValue(o.Name, out var value) && o.Problem(value) is null)
            .ToDictionary(o => o.Name, o => saved[o.Name]);
    }

    /// <summary>Keeps the values a tool is about to run with. Failing to save only loses the convenience, so it's logged, not raised.</summary>
    public void Store(Target target)
    {
        var all = Read();
        all[target.Key] = target.Options
            .Where(o => o.Remember && target.Parameters.ContainsKey(o.Name))
            .ToDictionary(o => o.Name, o => target.Parameters[o.Name]);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(all, Json));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Write($"tool settings: {ex.Message}");
        }
    }

    Dictionary<string, Dictionary<string, string>> Read()
    {
        try
        {
            if (File.Exists(path))
                return JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, string>>>(File.ReadAllText(path)) ?? [];
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            Log.Write($"tool settings: {ex.Message}");
        }
        return [];
    }
}
