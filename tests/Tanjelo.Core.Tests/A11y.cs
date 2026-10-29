using System.Reflection;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Interop;
using System.Windows.Media;

namespace Tanjelo.Core.Tests;

/// <summary>
/// What a screen reader and the keyboard find in a window off-screen: the automation tree, with every control's name,
/// and the order Tab visits controls in, as WPF works it out. The content is put in a message-only window, which is
/// never shown, so WPF counts it as visible without anything appearing on the screen.
/// </summary>
static class A11y
{
    /// <summary>Puts a host from <see cref="Ui.Host"/> in a window that's never shown; dispose it to take it out again.</summary>
    public static IDisposable Attach(FrameworkElement host)
    {
        var source = new HwndSource(new HwndSourceParameters("Tanjelo accessibility test")
        {
            ParentWindow = new IntPtr(-3), // HWND_MESSAGE
            Width = (int)host.Width,
            Height = (int)host.Height,
        })
        {
            SizeToContent = SizeToContent.WidthAndHeight,
            RootVisual = host,
        };
        Ui.Layout(host);
        return new Detach(source);
    }

    sealed class Detach(HwndSource source) : IDisposable
    {
        public void Dispose()
        {
            source.RootVisual = null;
            source.Dispose();
        }
    }

    /// <summary>The controls a person acts on, which must have names.</summary>
    static readonly HashSet<AutomationControlType> Actionable =
    [
        AutomationControlType.Button, AutomationControlType.CheckBox, AutomationControlType.RadioButton, AutomationControlType.Edit,
        AutomationControlType.Slider, AutomationControlType.ComboBox, AutomationControlType.Hyperlink, AutomationControlType.ListItem,
        AutomationControlType.MenuItem, AutomationControlType.TabItem, AutomationControlType.Spinner, AutomationControlType.List,
        AutomationControlType.DataItem, AutomationControlType.SplitButton,
    ];

    /// <summary>Every peer under the host that's on show, as a screen reader would walk them.</summary>
    public static List<AutomationPeer> Tree(FrameworkElement host)
    {
        var all = new List<AutomationPeer>();
        void Walk(AutomationPeer peer)
        {
            all.Add(peer);
            // A slider's or scrollbar's track buttons are part of it, named by it.
            if (peer.GetAutomationControlType() is AutomationControlType.Slider or AutomationControlType.ScrollBar) return;
            foreach (var child in peer.GetChildren() ?? []) Walk(child);
        }
        foreach (var child in new FrameworkElementAutomationPeer(host).GetChildren() ?? []) Walk(child);
        return all.Where(p => !p.IsOffscreen()).ToList();
    }

    /// <summary>Names of the actionable controls that have none, or only an icon: a description of each, for the failure message.</summary>
    public static List<string> Unnamed(FrameworkElement host) =>
        Tree(host)
            .Where(p => Actionable.Contains(p.GetAutomationControlType()) && p.IsEnabled() && !IsNamed(p.GetName()))
            .Select(Describe)
            .ToList();

    static bool IsNamed(string? name) =>
        !string.IsNullOrWhiteSpace(name) && name.Any(c => !(c >= '' && c <= '') && !char.IsWhiteSpace(c));

    static string Describe(AutomationPeer peer)
    {
        var owner = (peer as UIElementAutomationPeer)?.Owner as FrameworkElement;
        var text = owner switch
        {
            ContentControl { Content: string s } => s,
            TextBox box => box.Text,
            _ => "",
        };
        return $"{peer.GetAutomationControlType()} {owner?.GetType().Name} '{owner?.Name}' '{text}' '{peer.GetName()}'";
    }

    static readonly object Navigation = typeof(FrameworkElement).Assembly.GetType("System.Windows.Input.KeyboardNavigation")!
        .GetProperty("Current", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;

    static readonly MethodInfo NextTab = Navigation.GetType()
        .GetMethod("GetNextTab", BindingFlags.NonPublic | BindingFlags.Instance, [typeof(DependencyObject), typeof(DependencyObject), typeof(bool)])!;

    /// <summary>The controls Tab visits from the start of the host, in order, as WPF's own keyboard navigation finds them.</summary>
    public static List<DependencyObject> TabOrder(FrameworkElement host)
    {
        var order = new List<DependencyObject>();
        DependencyObject? at = null;
        while (true)
        {
            at = (DependencyObject?)NextTab.Invoke(Navigation, [at, host, false]);
            if (at is null || order.Contains(at)) break;
            order.Add(at);
        }
        return order;
    }

    /// <summary>Controls a person can act on that are on show and enabled, whether or not Tab reaches them.</summary>
    public static List<DependencyObject> Interactive(FrameworkElement host)
    {
        var found = new List<DependencyObject>();
        void Walk(DependencyObject node)
        {
            if (node is UIElement { IsVisible: false } or UIElement { IsEnabled: false }) return;
            if (node is ButtonBase or TextBoxBase or Slider or ComboBox or ListBoxItem) found.Add(node);
            if (node is TextBlock block)
                foreach (var link in block.Inlines.OfType<Hyperlink>().Where(l => l.IsEnabled)) found.Add(link);
            // Parts inside a control's own template, such as a slider's track buttons or a text box's scroller, belong to it.
            if (node is Slider or TextBoxBase or ScrollBar or ComboBox) return;
            for (var i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++) Walk(VisualTreeHelper.GetChild(node, i));
        }
        Walk(host);
        return found;
    }

    /// <summary>
    /// Asserts every control is named and reached by Tab, and returns the Tab order for the test's own checks.
    /// <paramref name="otherKeys"/> picks out controls reached by other keys instead, such as a list's items by the arrows.
    /// </summary>
    public static List<DependencyObject> Check(FrameworkElement host, string window, Func<DependencyObject, bool>? otherKeys = null)
    {
        var unnamed = Unnamed(host);
        Assert.True(unnamed.Count == 0, $"{window}: controls without a name:\n" + string.Join("\n", unnamed));
        var order = TabOrder(host);
        var missed = Interactive(host).Where(c => !order.Contains(c) && otherKeys?.Invoke(c) != true).Select(c => $"{c.GetType().Name} '{(c as FrameworkElement)?.Name}' '{Text(c)}'").ToList();
        Assert.True(missed.Count == 0, $"{window}: controls Tab doesn't reach:\n" + string.Join("\n", missed));
        return order;
    }

    static string Text(DependencyObject control) => control switch
    {
        ContentControl { Content: string s } => s,
        Hyperlink link => new TextRange(link.ContentStart, link.ContentEnd).Text,
        UIElement element => AutomationProperties.GetName(element),
        _ => "",
    };

    /// <summary>Asserts the Tab order visits these in this order (with others between allowed).</summary>
    public static void InOrder(List<DependencyObject> order, params DependencyObject[] expected)
    {
        var at = expected.Select(e => order.IndexOf(e)).ToList();
        Assert.DoesNotContain(-1, at);
        for (var i = 1; i < at.Count; i++)
            Assert.True(at[i] > at[i - 1], $"Tab reaches {expected[i]} before {expected[i - 1]}");
    }
}
