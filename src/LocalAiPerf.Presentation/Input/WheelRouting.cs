namespace LocalAiPerf.Presentation.Input;

public enum WheelTarget
{
    /// <summary>Leave the wheel to the control under the pointer.</summary>
    Inner,

    /// <summary>Send the wheel to the page's scroll viewer.</summary>
    Page,
}

/// <summary>
/// Who gets a mouse-wheel turn on a page that scrolls as a whole and contains tables and charts.
/// Platform-neutral so the rules can be tested without WPF.
/// </summary>
public static class WheelRouting
{
    /// <summary>
    /// A table or list scrolls itself while it can move in the wheel's direction, then the page takes over.
    /// </summary>
    /// <param name="delta">Wheel delta: positive = up.</param>
    /// <param name="offset">The inner list's current vertical offset, or null when it has no scroll area.</param>
    public static WheelTarget ForList(int delta, double? offset, double scrollableHeight)
    {
        if (offset is not double o || scrollableHeight <= 0)
        {
            return WheelTarget.Page;
        }

        const double Epsilon = 0.5;
        var canMove = delta > 0 ? o > Epsilon : o < scrollableHeight - Epsilon;
        return canMove ? WheelTarget.Inner : WheelTarget.Page;
    }

    /// <summary>A chart zooms only on Ctrl+wheel, and only when zooming is turned on; a plain wheel scrolls the page.</summary>
    public static WheelTarget ForChart(bool zoomEnabled, bool ctrlPressed)
        => zoomEnabled && ctrlPressed ? WheelTarget.Inner : WheelTarget.Page;
}
