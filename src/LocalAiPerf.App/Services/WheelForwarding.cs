using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using LocalAiPerf.Presentation.Input;
using ScottPlot.WPF;

namespace LocalAiPerf.App.Services;

/// <summary>
/// Pages scroll as a whole, but WPF gives the wheel to the control under the pointer: tables and lists kept it even
/// with nothing left to scroll, and zoomable charts used it for zooming, so the page seemed stuck. Registered once at
/// startup for every DataGrid, ListBox and chart; see <see cref="WheelRouting"/> for the rules.
/// </summary>
public static class WheelForwarding
{
    private static bool _registered;

    public static void Register()
    {
        if (_registered)
        {
            return;
        }

        _registered = true;
        foreach (var type in new[] { typeof(DataGrid), typeof(ListBox), typeof(WpfPlot) })
        {
            EventManager.RegisterClassHandler(type, UIElement.PreviewMouseWheelEvent, new MouseWheelEventHandler(OnPreviewMouseWheel));
        }
    }

    private static void OnPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (e.Handled || sender is not DependencyObject element)
        {
            return;
        }

        var target = sender is WpfPlot plot
            ? WheelRouting.ForChart(plot.UserInputProcessor.IsEnabled, Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
            : FindDescendant<ScrollViewer>(element) is { } inner
                ? WheelRouting.ForList(e.Delta, inner.VerticalOffset, inner.ScrollableHeight)
                : WheelRouting.ForList(e.Delta, null, 0);
        if (target == WheelTarget.Inner)
        {
            return;
        }

        // Marking the preview handled also stops the control's own wheel handling (zoom or list scroll).
        e.Handled = true;
        if (FindAncestor<ScrollViewer>(VisualTreeHelper.GetParent(element)) is { } page)
        {
            page.RaiseEvent(new MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta)
            {
                RoutedEvent = UIElement.MouseWheelEvent,
                Source = sender,
            });
        }
    }

    private static T? FindAncestor<T>(DependencyObject? node) where T : DependencyObject
    {
        while (node is not null and not T)
        {
            node = node is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node);
        }

        return node as T;
    }

    private static T? FindDescendant<T>(DependencyObject node) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
        {
            var child = VisualTreeHelper.GetChild(node, i);
            if (child is T found)
            {
                return found;
            }

            if (FindDescendant<T>(child) is { } deeper)
            {
                return deeper;
            }
        }

        return null;
    }
}
