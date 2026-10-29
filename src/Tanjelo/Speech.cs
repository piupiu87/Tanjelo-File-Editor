using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;

namespace Tanjelo;

/// <summary>Tells screen readers about things that happen away from the focus, such as a result card appearing.</summary>
static class Speech
{
    /// <summary>
    /// Gives <paramref name="element"/> the text as its name and has it read out as a live region, politely, after
    /// whatever is being read now.
    /// </summary>
    public static void Announce(UIElement element, string text)
    {
        AutomationProperties.SetName(element, text);
        if (AutomationProperties.GetLiveSetting(element) == AutomationLiveSetting.Off)
            AutomationProperties.SetLiveSetting(element, AutomationLiveSetting.Polite);
        if (!AutomationPeer.ListenerExists(AutomationEvents.LiveRegionChanged)) return;
        var peer = UIElementAutomationPeer.FromElement(element) ?? UIElementAutomationPeer.CreatePeerForElement(element);
        peer?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
    }
}
