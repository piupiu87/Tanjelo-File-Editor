namespace Tanjelo;

/// <summary>
/// Where the menu goes, in physical pixels: beside the pointer like a context menu, flipped up or left when it
/// wouldn't fit, and always inside the monitor's work area.
/// </summary>
static class MenuPlacement
{
    const int Gap = 2;

    /// <summary>Whether a menu this tall opens above the pointer. Decided once, so later size changes don't flip it.</summary>
    public static bool OpensAbove(Native.POINT pointer, Native.RECT work, int height) => pointer.Y + Gap + height > work.Bottom;

    /// <summary>
    /// The top-left corner for a menu of this size. Above the pointer, the bottom edge stays put as the height
    /// changes; below it, the top edge does, unless the menu would run off the work area.
    /// </summary>
    public static (int X, int Y) Position(Native.POINT pointer, Native.RECT work, int width, int height, bool above)
    {
        var x = pointer.X + Gap;
        if (x + width > work.Right) x = Math.Max(work.Left, pointer.X - width - Gap);
        var y = above
            ? Math.Max(work.Top, Math.Min(pointer.Y - height - Gap, work.Bottom - height))
            : Math.Max(work.Top, Math.Min(pointer.Y + Gap, work.Bottom - height));
        return (x, y);
    }
}
