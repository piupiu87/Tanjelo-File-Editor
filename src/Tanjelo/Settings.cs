using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows.Input;
using Tanjelo.Core;

namespace Tanjelo;

/// <summary>User settings in %APPDATA%\Tanjelo\settings.json. Edited by hand; the app reloads it on save.</summary>
sealed class Settings
{
    public string Hotkey { get; set; } = "Ctrl+Shift+Space";
    public string Theme { get; set; } = "system"; // "system", "light" or "dark"
    public int JpegQuality { get; set; } = 90;
    public int WebpQuality { get; set; } = 90;
    public int HeicQuality { get; set; } = 90;
    public int PdfRenderDpi { get; set; } = 300;
    public string? FfmpegPath { get; set; }
    public string? SevenZipPath { get; set; }

    public static string FilePath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Tanjelo", "settings.json");

    static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    /// <summary>Loads the settings, falling back to defaults when the file is missing or malformed.</summary>
    public static Settings Load(out string? error)
    {
        error = null;
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath), Json) ?? new Settings();
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            error = ex.Message;
        }
        return new Settings();
    }

    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(this, Json));
    }

    public ConverterOptions ToOptions() => new()
    {
        JpegQuality = Math.Clamp(JpegQuality, 1, 100),
        WebpQuality = Math.Clamp(WebpQuality, 1, 100),
        HeicQuality = Math.Clamp(HeicQuality, 1, 100),
        PdfRenderDpi = Math.Clamp(PdfRenderDpi, 72, 600),
    };

    /// <summary>Parses "Ctrl+Shift+Space"-style text; at least one modifier is required.</summary>
    public static bool TryParseHotkey(string text, out ModifierKeys modifiers, out Key key)
    {
        modifiers = ModifierKeys.None;
        key = Key.None;
        var parts = text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length < 2) return false;
        foreach (var part in parts[..^1])
        {
            switch (part.ToLowerInvariant())
            {
                case "ctrl" or "control": modifiers |= ModifierKeys.Control; break;
                case "shift": modifiers |= ModifierKeys.Shift; break;
                case "alt": modifiers |= ModifierKeys.Alt; break;
                case "win" or "windows": modifiers |= ModifierKeys.Windows; break;
                default: return false;
            }
        }
        var name = parts[^1];
        if (name.Length == 1 && char.IsDigit(name[0])) name = "D" + name; // "1" is Key.D1
        return Enum.TryParse(name, ignoreCase: true, out key) && key != Key.None;
    }
}
