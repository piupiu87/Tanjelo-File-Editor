using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;

namespace Tanjelo;

/// <summary>
/// Follows the Windows light/dark app setting by swapping the brushes the XAML binds to dynamically. With a Windows
/// contrast theme on, the brushes take that theme's colours instead of Tanjelo's own.
/// </summary>
static class Theme
{
    /// <summary>"system" (follow Windows), "light" or "dark", from settings.</summary>
    public static string Preference { get; set; } = "system";

    /// <summary>The colours of a Windows contrast theme, as Windows names them.</summary>
    public sealed record Contrast(Color Window, Color WindowText, Color Highlight, Color HighlightText, Color ButtonFace, Color ButtonText, Color GrayText)
    {
        public bool IsDark => 0.2126 * Window.R + 0.7152 * Window.G + 0.0722 * Window.B < 128;

        public static Contrast FromWindows() => new(SystemColors.WindowColor, SystemColors.WindowTextColor, SystemColors.HighlightColor,
            SystemColors.HighlightTextColor, SystemColors.ControlColor, SystemColors.ControlTextColor, SystemColors.GrayTextColor);

        /// <summary>Windows 11's Aquatic and Desert contrast themes, for the tests, which can't switch Windows' own.</summary>
        public static readonly Contrast Aquatic = new(Hex("#202020"), Hex("#FFFFFF"), Hex("#8EE3F0"), Hex("#263B50"), Hex("#202020"), Hex("#FFFFFF"), Hex("#A6A6A6"));
        public static readonly Contrast Desert = new(Hex("#FFFAEF"), Hex("#3D3D3D"), Hex("#903909"), Hex("#FFF5E3"), Hex("#FFFAEF"), Hex("#3D3D3D"), Hex("#676767"));
    }

    /// <summary>A contrast theme to use whatever Windows says; for the tests.</summary>
    public static Contrast? ContrastOverride { get; set; }

    /// <summary>The contrast theme in use, or null when there's none.</summary>
    public static Contrast? CurrentContrast => ContrastOverride ?? (SystemParameters.HighContrast ? Contrast.FromWindows() : null);

    public static bool IsDark => CurrentContrast is { } contrast ? contrast.IsDark : Preference.ToLowerInvariant() switch
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
        ["FocusRing"] = ("#1B1B1B", "#F3F3F3"),
        // Only the contrast themes outline buttons and selected items; Tanjelo's own themes show them by colour.
        ["ButtonBorder"] = ("#00000000", "#00000000"),
        ["SelectedBorder"] = ("#00000000", "#00000000"),
    };

    /// <summary>
    /// The same brushes in a contrast theme: text in the theme's text colour on its window colour, the accent in its
    /// highlight colour, and outlines where Tanjelo's own themes use a shade. Selected items keep the window colour behind
    /// them and are outlined in the highlight colour, so text on them never needs a second colour.
    /// </summary>
    static Color InContrast(string name, Contrast c) => name switch
    {
        "Surface" or "Hover" or "Selected" or "AccentSoft" or "Field" or "SuccessSoft" or "DangerSoft" => c.Window,
        "TextPrimary" or "TextSecondary" or "TextTertiary" or "Success" or "Danger" or "FieldBorder" => c.WindowText,
        "Accent" or "AccentButton" or "AccentButtonHover" or "FocusRing" or "SelectedBorder" => c.Highlight,
        "OnAccent" => c.HighlightText,
        "Subtle" or "SubtleHover" => c.ButtonFace,
        "ButtonBorder" => c.ButtonText,
        "Track" => c.GrayText,
        _ => c.WindowText,
    };

    public static void Apply(ResourceDictionary resources)
    {
        var contrast = CurrentContrast;
        var dark = IsDark;
        foreach (var (name, (light, darkColor)) in Palette)
        {
            var colour = contrast is not null ? InContrast(name, contrast) : Hex(dark ? darkColor : light);
            var brush = new SolidColorBrush(colour);
            brush.Freeze();
            resources[name] = brush;
        }
    }

    static Color Hex(string hex) => (Color)ColorConverter.ConvertFromString(hex);
}
