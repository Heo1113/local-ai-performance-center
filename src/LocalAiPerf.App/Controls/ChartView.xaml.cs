using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using LocalAiPerf.Presentation.Charts;
using ScottPlot;

namespace LocalAiPerf.App.Controls;

/// <summary>
/// Renders a <see cref="ChartModel"/> with ScottPlot: one unit per chart, 2 px lines, gaps kept as gaps,
/// a legend only for two or more series, and a hover readout listing every series at the pointer's time.
/// </summary>
public partial class ChartView : UserControl
{
    public static readonly DependencyProperty ModelProperty = DependencyProperty.Register(
        nameof(Model), typeof(ChartModel), typeof(ChartView), new PropertyMetadata(null, (d, _) => ((ChartView)d).Render()));

    public static readonly DependencyProperty InteractiveProperty = DependencyProperty.Register(
        nameof(Interactive), typeof(bool), typeof(ChartView), new PropertyMetadata(false, (d, _) => ((ChartView)d).ApplyInteractivity()));

    private static readonly string? KoreanFont = System.Windows.Media.Fonts.SystemFontFamilies.Any(f => f.Source == "Malgun Gothic") ? "Malgun Gothic" : null;

    public ChartView()
    {
        InitializeComponent();
        ApplyInteractivity();
        ApplyPlotStyle(Plot.Plot);
        Plot.Refresh();
    }

    public ChartModel? Model
    {
        get => (ChartModel?)GetValue(ModelProperty);
        set => SetValue(ModelProperty, value);
    }

    /// <summary>Live charts are not interactive (they redraw every second); history/compare charts allow zoom and pan.</summary>
    public bool Interactive
    {
        get => (bool)GetValue(InteractiveProperty);
        set => SetValue(InteractiveProperty, value);
    }

    private void ApplyInteractivity()
    {
        if (Interactive)
        {
            Plot.UserInputProcessor.Enable();
        }
        else
        {
            Plot.UserInputProcessor.Disable();
        }
    }

    private void Render()
    {
        var model = Model;
        var plot = Plot.Plot;
        plot.Clear();
        ApplyPlotStyle(plot);
        TitleText.Text = model?.Title ?? "";
        UnitText.Text = model is null ? "" : $"[{model.Unit}]";
        if (model is null || model.IsEmpty)
        {
            EmptyText.Text = model?.EmptyText ?? "";
            EmptyText.Visibility = Visibility.Visible;
            Plot.Refresh();
            return;
        }

        EmptyText.Visibility = Visibility.Collapsed;
        var showLegend = model.Series.Count(s => s.X.Length > 0) >= 2 || model.Spans.Count > 0;
        double dataMax = double.MinValue;
        foreach (var series in model.Series)
        {
            var color = Color.FromHex(series.ColorHex);
            var first = true;
            foreach (var (xs, ys) in Segments(series, model.GapThreshold))
            {
                var line = plot.Add.ScatterLine(xs, ys, color);
                line.LineWidth = 2;
                line.MarkerSize = xs.Length == 1 ? 5 : 0;
                if (first && showLegend)
                {
                    line.LegendText = series.Label;
                }

                first = false;
                dataMax = Math.Max(dataMax, ys.Max());
            }
        }

        var labelled = new HashSet<string>();
        foreach (var span in model.Spans)
        {
            var c = Color.FromHex(span.ColorHex);
            var band = plot.Add.HorizontalSpan(span.X1, span.X2, c.WithAlpha(0.22));
            band.LineWidth = 0;
            if (labelled.Add(span.Label))
            {
                band.LegendText = span.Label;
            }
        }

        foreach (var reference in model.ReferenceLines)
        {
            var line = plot.Add.HorizontalLine(reference.Y, 1, Color.FromHex(reference.ColorHex), LinePattern.Dashed);
            line.Text = reference.Label;
            line.LabelFontColor = Color.FromHex(ChartPalette.SecondaryInk);
            line.LabelBackgroundColor = Color.FromHex(ChartPalette.Surface);
            dataMax = Math.Max(dataMax, reference.Y);
        }

        if (model.XIsTime)
        {
            plot.Axes.DateTimeTicksBottom();
        }
        else if (model.XLabel is not null)
        {
            plot.XLabel(model.XLabel, 12);
        }

        plot.Axes.AutoScale();
        if (model.YMin is not null || model.YMax is not null)
        {
            var limits = plot.Axes.GetLimits();
            var bottom = model.YMin ?? limits.Bottom;
            var top = model.YMax is double max ? Math.Max(max, dataMax) : Math.Max(limits.Top, dataMax * 1.08);
            if (top <= bottom)
            {
                top = bottom + 1;
            }

            plot.Axes.SetLimitsY(bottom, top);
        }

        if (showLegend)
        {
            plot.ShowLegend(Alignment.UpperLeft);
        }
        else
        {
            plot.HideLegend();
        }

        Plot.Refresh();
    }

    private static IEnumerable<(double[] Xs, double[] Ys)> Segments(ChartSeries series, double gap)
    {
        var start = 0;
        for (var i = 1; i <= series.X.Length; i++)
        {
            if (i == series.X.Length || series.X[i] - series.X[i - 1] > gap)
            {
                if (i > start)
                {
                    yield return (series.X[start..i], series.Y[start..i]);
                }

                start = i;
            }
        }
    }

    private static void ApplyPlotStyle(Plot plot)
    {
        if (KoreanFont is not null)
        {
            plot.Font.Set(KoreanFont);
        }

        plot.FigureBackground.Color = Color.FromHex(ChartPalette.Surface);
        plot.DataBackground.Color = Color.FromHex(ChartPalette.Surface);
        plot.Axes.Color(Color.FromHex(ChartPalette.MutedInk));
        plot.Axes.FrameColor(Color.FromHex(ChartPalette.Axis));
        plot.Grid.MajorLineColor = Color.FromHex(ChartPalette.Grid);
        plot.Legend.BackgroundColor = Color.FromHex(ChartPalette.Surface);
        plot.Legend.FontColor = Color.FromHex(ChartPalette.SecondaryInk);
        plot.Legend.OutlineColor = Color.FromHex(ChartPalette.Axis);
    }

    private void OnPlotMouseMove(object sender, MouseEventArgs e)
    {
        var model = Model;
        if (model is null || model.IsEmpty)
        {
            return;
        }

        var pixel = Plot.GetPlotPixelPosition(e);
        var coordinates = Plot.Plot.GetCoordinates(pixel);
        var limits = Plot.Plot.Axes.GetLimits();
        var tolerance = Math.Abs(limits.Right - limits.Left) / 40;
        var parts = new List<string>();
        double? at = null;
        foreach (var series in model.Series)
        {
            if (Nearest(series.X, coordinates.X) is int i && Math.Abs(series.X[i] - coordinates.X) <= tolerance)
            {
                at ??= series.X[i];
                parts.Add($"{series.Label} {series.Y[i].ToString(model.ValueFormat, CultureInfo.InvariantCulture)} {model.Unit}");
            }
        }

        if (parts.Count == 0)
        {
            Readout.Text = "";
            return;
        }

        var x = at!.Value;
        var position = model.XIsTime
            ? DateTime.FromOADate(x).ToString("HH:mm:ss", CultureInfo.InvariantCulture)
            : $"{x.ToString("0.0", CultureInfo.InvariantCulture)}분";
        Readout.Text = $"{position} · {string.Join(" · ", parts)}";
    }

    private void OnPlotMouseLeave(object sender, MouseEventArgs e) => Readout.Text = "";

    private static int? Nearest(double[] xs, double x)
    {
        if (xs.Length == 0)
        {
            return null;
        }

        var index = Array.BinarySearch(xs, x);
        if (index >= 0)
        {
            return index;
        }

        index = ~index;
        if (index == 0)
        {
            return 0;
        }

        if (index >= xs.Length)
        {
            return xs.Length - 1;
        }

        return x - xs[index - 1] <= xs[index] - x ? index - 1 : index;
    }
}
