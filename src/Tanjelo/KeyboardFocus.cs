using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;

namespace Tanjelo;

/// <summary>
/// Buttons, chips, ticks and sliders take the focus from Tab, so everything can be reached from the keyboard, but not
/// from a click: the keys stay where they were, such as in the menu's search box or on an editor's picture, so Space
/// still plays a video after its Play button was clicked.
/// </summary>
static class KeyboardFocus
{
    static bool _installed;

    public static void Install()
    {
        if (_installed) return;
        _installed = true;
        foreach (var type in new[] { typeof(ButtonBase), typeof(Slider) })
            EventManager.RegisterClassHandler(type, UIElement.PreviewMouseDownEvent, new MouseButtonEventHandler(Pressed), handledEventsToo: true);
    }

    static void Pressed(object sender, MouseButtonEventArgs e)
    {
        // A control that already has the focus keeps it; one inside a text box's template (none here) would too.
        if (sender is not UIElement { Focusable: true, IsKeyboardFocusWithin: false } control) return;
        control.Focusable = false;
        control.Dispatcher.BeginInvoke(() => control.Focusable = true, DispatcherPriority.Input);
    }
}
