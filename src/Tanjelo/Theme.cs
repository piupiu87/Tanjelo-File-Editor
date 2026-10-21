using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;

namespace Tanjelo;

/// <summary>Follows the Windows light/dark app setting by swapping the brushes the XAML binds to dynamically.</summary>
static class Theme
{
    /// <summary>"system" (follow Windows), "light" or "dark", from settings.</summary>
    public static string Preference { get; set; } = "system";

    public static bool IsDark => Preference.ToLowerInvariant() switch
    {
        "dark" => true,
        "light" => false,
        _ => Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "AppsUseLightTheme", 1) is 0,
    };

    static readonly Dictionary<string, (string Light, string Dark)> Palette = new()
    {
        ["Surface"] = ("#FCFCFC", "#2B2B2B"),
        ["TextPrimary"] = ("#1B1B1B", "#F3F3F3"),
        ["TextSecondary"] = ("#5F5F5F", "#C5C5C5"),
        ["TextTertiary"] = ("#8A8A8A", "#9A9A9A"),
        ["Hover"] = ("#F0F0F0", "#383838"),
        // Tangelo red-orange, matching the icon; button colours keep 5:1+ contrast with their text.
        ["Selected"] = ("#FDE3D3", "#50291B"),
        ["Accent"] = ("#D9480C", "#FF8A4C"),
        ["AccentButton"] = ("#C2410C", "#F97A3D"),
        ["AccentButtonHover"] = ("#A8380B", "#FF9459"),
        ["AccentSoft"] = ("#FEE8DA", "#47271A"),
        ["OnAccent"] = ("#FFFFFF", "#1B1B1B"),
        ["Field"] = ("#FFFFFF", "#1F1F1F"),
        ["FieldBorder"] = ("#DEDEDE", "#3F3F3F"),
        ["Track"] = ("#E5E5E5", "#454545"),
        ["Subtle"] = ("#EFEFEF", "#3A3A3A"),
        ["SubtleHover"] = ("#E4E4E4", "#464646"),
        ["Success"] = ("#2E8540", "#6CCB7F"),
        ["SuccessSoft"] = ("#E3F4E6", "#1F3A25"),
        ["Danger"] = ("#C42B1C", "#FF8A80"),
        ["DangerSoft"] = ("#FBE5E3", "#44221F"),
    };

    public static void Apply(ResourceDictionary resources)
    {
        var dark = IsDark;
        foreach (var (name, (light, darkColor)) in Palette)
        {
            var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(dark ? darkColor : light));
            brush.Freeze();
            resources[name] = brush;
        }
    }
}
