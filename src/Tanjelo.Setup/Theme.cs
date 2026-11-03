using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;

namespace Tanjelo.Setup;

/// <summary>
/// Setup's colours: Tanjelo's light or dark theme as Windows' app setting says, or a Windows contrast theme's own
/// colours when one is on. The same palette as the app's (src/Tanjelo/Theme.cs).
/// </summary>
static class Theme
{
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

    /// <summary>A contrast theme, or light or dark, to use whatever Windows says; for the tests.</summary>
    public static Contrast? ContrastOverride { get; set; }
    public static bool? DarkOverride { get; set; }

    public static Contrast? CurrentContrast => ContrastOverride ?? (SystemParameters.HighContrast ? Contrast.FromWindows() : null);

    public static bool IsDark => CurrentContrast is { } contrast ? contrast.IsDark
        : DarkOverride ?? Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "AppsUseLightTheme", 1) is 0;

    static readonly (string Name, string Light, string Dark)[] Palette =
    [
        ("Surface", "#FCFCFC", "#2B2B2B"),
        ("Footer", "#F3F3F3", "#242424"),
        ("TextPrimary", "#1B1B1B", "#F3F3F3"),
        ("TextSecondary", "#5F5F5F", "#C5C5C5"),
        ("TextTertiary", "#8A8A8A", "#9A9A9A"),
        ("Hover", "#F0F0F0", "#383838"),
        ("Accent", "#D9480C", "#FF8A4C"),
        ("AccentButton", "#C2410C", "#F97A3D"),
        ("AccentButtonHover", "#A8380B", "#FF9459"),
        ("OnAccent", "#FFFFFF", "#1B1B1B"),
        ("Field", "#FFFFFF", "#1F1F1F"),
        ("FieldBorder", "#DEDEDE", "#3F3F3F"),
        ("Track", "#E5E5E5", "#454545"),
        ("Subtle", "#EAEAEA", "#3A3A3A"),
        ("SubtleHover", "#DFDFDF", "#464646"),
        ("Success", "#2E8540", "#6CCB7F"),
        ("Danger", "#C42B1C", "#FF8A80"),
        ("FocusRing", "#1B1B1B", "#F3F3F3"),
        // Only the contrast themes outline buttons; Tanjelo's own themes show them by colour.
        ("ButtonBorder", "#00000000", "#00000000"),
    ];

    static Color InContrast(string name, Contrast c) => name switch
    {
        "Surface" or "Footer" or "Hover" or "Field" => c.Window,
        "TextPrimary" or "TextSecondary" or "TextTertiary" or "Success" or "Danger" or "FieldBorder" => c.WindowText,
        "Accent" or "AccentButton" or "AccentButtonHover" or "FocusRing" => c.Highlight,
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
        foreach (var (name, light, darkColor) in Palette)
        {
            var brush = new SolidColorBrush(contrast is not null ? InContrast(name, contrast) : Hex(dark ? darkColor : light));
            brush.Freeze();
            resources[name] = brush;
        }
    }

    static Color Hex(string hex) => (Color)ColorConverter.ConvertFromString(hex);
}
