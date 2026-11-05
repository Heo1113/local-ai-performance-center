using LocalAiPerf.Presentation.Input;
using Xunit;

namespace LocalAiPerf.Tests.Presentation;

public class WheelRoutingTests
{
    [Theory]
    [InlineData(-120, null, 0.0, WheelTarget.Page)]   // no scroll area: the page scrolls
    [InlineData(-120, 0.0, 0.0, WheelTarget.Page)]    // nothing to scroll in the table
    [InlineData(-120, 0.0, 50.0, WheelTarget.Inner)]  // table can go down: it scrolls first
    [InlineData(-120, 50.0, 50.0, WheelTarget.Page)]  // table at the bottom: the page continues
    [InlineData(120, 50.0, 50.0, WheelTarget.Inner)]  // up from the bottom: the table scrolls
    [InlineData(120, 0.0, 50.0, WheelTarget.Page)]    // table at the top: the page continues upward
    public void Lists_scroll_until_their_end_then_the_page_takes_over(int delta, double? offset, double scrollable, WheelTarget expected)
        => Assert.Equal(expected, WheelRouting.ForList(delta, offset, scrollable));

    [Theory]
    [InlineData(false, false, WheelTarget.Page)]
    [InlineData(false, true, WheelTarget.Page)]  // live charts never zoom
    [InlineData(true, false, WheelTarget.Page)]  // a plain wheel over a zoomable chart scrolls the page
    [InlineData(true, true, WheelTarget.Inner)]  // Ctrl+wheel zooms
    public void Charts_zoom_only_with_ctrl(bool zoomEnabled, bool ctrl, WheelTarget expected)
        => Assert.Equal(expected, WheelRouting.ForChart(zoomEnabled, ctrl));
}
