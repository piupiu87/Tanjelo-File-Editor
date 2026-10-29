using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ImageMagick;
using Orientation = System.Windows.Controls.Orientation;
using Tanjelo.Core;

namespace Tanjelo.Editors;

/// <summary>The pieces the editors' side panels and toolbars are made of, in the look of the options dialog.</summary>
static class Panels
{
    public static TextBlock Heading(string text, double top = 0) => Themed(new TextBlock
    {
        Text = text,
        FontWeight = FontWeights.SemiBold,
        FontSize = 12.5,
        Margin = new Thickness(0, top, 0, 6),
    }, TextBlock.ForegroundProperty, "TextPrimary");

    public static TextBlock Hint(string text, double top = 4) => Themed(new TextBlock
    {
        Text = text,
        FontSize = 12,
        TextWrapping = TextWrapping.Wrap,
        Margin = new Thickness(0, top, 0, 0),
    }, TextBlock.ForegroundProperty, "TextTertiary");

    /// <summary>A button with an icon and a word, as on the editors' toolbars.</summary>
    public static Button Button(string glyph, string text, string tip, Action click, bool mirror = false)
    {
        var content = new StackPanel { Orientation = Orientation.Horizontal };
        if (glyph.Length > 0)
        {
            var icon = Themed(new TextBlock { Text = glyph, FontSize = 13, Margin = new Thickness(0, 0, 7, 0), VerticalAlignment = VerticalAlignment.Center },
                TextBlock.FontFamilyProperty, "IconFont");
            if (mirror) (icon.RenderTransformOrigin, icon.RenderTransform) = (new Point(0.5, 0.5), new ScaleTransform(-1, 1));
            content.Children.Add(icon);
        }
        content.Children.Add(new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center });
        var button = Themed(new Button { Content = content, ToolTip = tip, Margin = new Thickness(0, 0, 6, 0) }, FrameworkElement.StyleProperty, "SubtleButton");
        System.Windows.Automation.AutomationProperties.SetName(button, text);
        System.Windows.Automation.AutomationProperties.SetHelpText(button, tip);
        button.Click += (_, _) => click();
        return button;
    }

    /// <summary>One choice among a few, as chips; the returned action changes the choice without calling <paramref name="picked"/>.</summary>
    public static (WrapPanel Panel, Action<string> Show) Chips(IReadOnlyList<Choice> choices, string selected, Action<string> picked)
    {
        var panel = new WrapPanel { Margin = new Thickness(0, 0, 0, -6) };
        var chips = new Dictionary<string, RadioButton>();
        var quiet = false;
        var group = "chips-" + Guid.NewGuid().ToString("N");
        foreach (var choice in choices)
        {
            var chip = Themed(new RadioButton { Content = choice.Label, GroupName = group, IsChecked = choice.Value == selected }, FrameworkElement.StyleProperty, "Chip");
            chip.Checked += (_, _) =>
            {
                if (!quiet) picked(choice.Value);
            };
            panel.Children.Add(chip);
            chips[choice.Value] = chip;
        }
        return (panel, value =>
        {
            quiet = true;
            if (chips.TryGetValue(value, out var chip)) chip.IsChecked = true;
            quiet = false;
        });
    }

    /// <summary>A row of colour dots, the chosen one ringed; the returned action moves the ring without calling <paramref name="picked"/>.</summary>
    public static (Panel Panel, Action<Colour> Show) Swatches(IReadOnlyList<string> colours, Colour selected, Action<Colour> picked, double size = 22)
    {
        var panel = new WrapPanel();
        var swatches = new List<(Colour Colour, RadioButton Swatch)>();
        var group = "swatches-" + Guid.NewGuid().ToString("N");
        foreach (var hex in colours)
        {
            Colour.TryParse(hex, out var colour);
            var dot = new Border
            {
                Width = size,
                Height = size,
                CornerRadius = new CornerRadius(size / 2),
                BorderThickness = new Thickness(1),
                Background = new SolidColorBrush(Color.FromRgb(colour.R, colour.G, colour.B)),
            };
            dot.SetResourceReference(Border.BorderBrushProperty, "FieldBorder");
            var swatch = Themed(new RadioButton { Content = dot, GroupName = group, ToolTip = ColourName(hex) }, FrameworkElement.StyleProperty, "Swatch");
            System.Windows.Automation.AutomationProperties.SetName(swatch, ColourName(hex));
            // Click, not Checked: picking the colour already ringed still applies it, such as to a newly selected mark.
            swatch.Click += (_, _) => picked(colour);
            panel.Children.Add(swatch);
            swatches.Add((colour, swatch));
        }
        void Show(Colour chosen)
        {
            foreach (var (colour, swatch) in swatches) swatch.IsChecked = colour == chosen;
        }
        Show(selected);
        return (panel, Show);
    }

    /// <summary>What a screen reader calls one of the editors' colours; anything else by its code.</summary>
    public static string ColourName(string hex) => hex.ToUpperInvariant() switch
    {
        "#E5383B" => Strings.Red,
        "#F97A3D" => Strings.Orange,
        "#C2410C" => Strings.DarkOrange,
        "#FFD400" => Strings.Yellow,
        "#2E8540" => Strings.Green,
        "#3A7BD5" => Strings.Blue,
        "#000000" or "#1B1B1B" => Strings.Black,
        "#2B2B2B" => Strings.DarkGrey,
        "#5F5F5F" => Strings.Grey,
        "#F3EFEA" => Strings.OffWhite,
        "#FFFFFF" => Strings.White,
        _ => hex,
    };

    /// <summary>Keys and what they do, each key drawn as a small key cap: "Shift" beside "Lines at 45°".</summary>
    public static FrameworkElement Keys(IReadOnlyList<(string Keys, string Does)> rows)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        for (var i = 0; i < rows.Count; i++)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var caps = new WrapPanel { Margin = new Thickness(0, 0, 10, 6), VerticalAlignment = VerticalAlignment.Top };
            foreach (var key in rows[i].Keys.Split(", "))
            {
                var cap = Themed(new Border
                {
                    CornerRadius = new CornerRadius(4),
                    BorderThickness = new Thickness(1, 1, 1, 2),
                    Padding = new Thickness(5, 0, 5, 1),
                    Margin = new Thickness(0, 0, 4, 0),
                    Child = new TextBlock { Text = key, FontSize = 11.5 },
                }, Border.BackgroundProperty, "Subtle");
                cap.SetResourceReference(Border.BorderBrushProperty, "FieldBorder");
                caps.Children.Add(cap);
            }
            var does = Themed(new TextBlock { Text = rows[i].Does, FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 1, 0, 6) },
                TextBlock.ForegroundProperty, "TextSecondary");
            Grid.SetRow(caps, i);
            Grid.SetRow(does, i);
            Grid.SetColumn(does, 1);
            grid.Children.Add(caps);
            grid.Children.Add(does);
        }
        return grid;
    }

    /// <summary>" · one selected", or " · 2 of 3 selected" when there are several, so a screen reader hears which one Tab picked.</summary>
    public static string Which(int? selected, int count) =>
        selected is not { } s ? "" : count == 1 ? Strings.OneSelected : string.Format(Strings.OfSelected, s + 1, count);

    public static T Themed<T>(T element, DependencyProperty property, string key) where T : FrameworkElement
    {
        element.SetResourceReference(property, key);
        return element;
    }

    /// <summary>A picture from ImageMagick for WPF to show, pixel for pixel.</summary>
    public static BitmapSource Bitmap(IMagickImage<byte> image)
    {
        var (width, height) = ((int)image.Width, (int)image.Height);
        // Read through the pixels: writing the image itself out as BGRA turns on an alpha channel it doesn't fill, and
        // areas cut from it afterwards come out see-through.
        using var collection = image.GetPixelsUnsafe();
        var pixels = collection.ToByteArray("BGRA")!;
        var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4);
        bitmap.Freeze();
        return bitmap;
    }
}
