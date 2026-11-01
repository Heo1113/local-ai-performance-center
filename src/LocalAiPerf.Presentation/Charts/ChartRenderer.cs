using ScottPlot;

namespace LocalAiPerf.Presentation.Charts;

/// <summary>
/// Draws a <see cref="ChartModel"/> onto a ScottPlot plot. Shared by the WPF control and by tests that render PNGs,
/// so the drawing code that runs in the app is the code that is tested.
/// Rules: one unit per chart, 2 px lines, gaps stay gaps, legend only for two or more series (or highlighted spans),
/// recessive grid and axes, status colors only for highlighted states.
/// </summary>
public static class ChartRenderer
{
    private static readonly string[] HangulFontCandidates = ["Malgun Gothic", "NanumGothic", "Noto Sans CJK KR", "Noto Sans KR"];

    /// <summary>
    /// A font that really has Hangul glyphs. Malgun Gothic ships with Windows; the others are for Linux build machines.
    /// The check is on glyphs, not on the reported family name: a localized system can report Malgun Gothic as "맑은 고딕";
    /// comparing names then rejected it and charts fell back to a font without Hangul (legend drawn as boxes on a user's PC).
    /// </summary>
    internal static readonly string? Font = ResolveHangulFont(HangulFontCandidates, SkiaSharp.SKFontManager.Default.MatchFamily);

    /// <param name="match">Finds the installed typeface for a family name (the caller's typeface is disposed here).</param>
    internal static string? ResolveHangulFont(IEnumerable<string> candidates, Func<string, SkiaSharp.SKTypeface?> match)
    {
        foreach (var name in candidates)
        {
            using var typeface = match(name);
            if (typeface is not null && typeface.ContainsGlyph('가'))
            {
                return name;
            }
        }

        // Any installed font with Hangul (for example on a system without Malgun Gothic).
        using var fallback = SkiaSharp.SKFontManager.Default.MatchCharacter(null, SkiaSharp.SKFontStyle.Normal, ["ko-KR", "ko"], '가');
        return fallback?.FamilyName;
    }

    /// <summary>Returns false when there is nothing to draw (the caller shows <see cref="ChartModel.EmptyText"/>).</summary>
    public static bool Render(Plot plot, ChartModel? model)
    {
        plot.Clear();
        ApplyStyle(plot);
        if (model is null || model.IsEmpty)
        {
            foreach (var panel in plot.Axes.GetPanels().OfType<ScottPlot.Panels.LegendPanel>().ToList())
            {
                plot.Axes.Remove(panel);
            }

            plot.HideLegend();
            return false;
        }

        var showLegend = model.Series.Count(s => s.X.Length > 0) >= 2 || model.Spans.Count > 0;
        var dataMax = double.MinValue;
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

        var labelled = new HashSet<string>(StringComparer.Ordinal);
        foreach (var span in model.Spans)
        {
            var band = plot.Add.HorizontalSpan(span.X1, span.X2, Color.FromHex(span.ColorHex).WithAlpha(0.22));
            band.LineWidth = 0;
            if (labelled.Add(span.Label))
            {
                band.LegendText = span.Label;
            }
        }

        var maxX = model.Series.Where(x => x.X.Length > 0).Select(x => x.X[^1]).DefaultIfEmpty(0).Max();
        foreach (var reference in model.ReferenceLines)
        {
            // A limit is a threshold, so it is the one dashed line on the chart. Its label sits inside the plot, at the right end.
            plot.Add.HorizontalLine(reference.Y, 1, Color.FromHex(reference.ColorHex), LinePattern.Dashed);
            var label = plot.Add.Text(reference.Label, maxX, reference.Y);
            label.LabelAlignment = Alignment.LowerRight;
            label.LabelFontColor = Color.FromHex(ChartPalette.SecondaryInk);
            label.LabelBackgroundColor = Color.FromHex(ChartPalette.Surface);
            label.LabelFontSize = 11;
            if (Font is not null)
            {
                label.LabelFontName = Font;
            }

            dataMax = Math.Max(dataMax, reference.Y);
        }

        if (model.XIsTime)
        {
            var axis = plot.Axes.DateTimeTicksBottom();
            if (axis.TickGenerator is ScottPlot.TickGenerators.DateTimeAutomatic ticks)
            {
                var minX = model.Series.Where(x => x.X.Length > 0).Select(x => x.X[0]).DefaultIfEmpty(maxX).Min();
                var spanDays = maxX - minX;
                var format = spanDays >= 1 ? "MM-dd HH:mm" : spanDays >= 2.0 / 24 ? "HH:mm" : "HH:mm:ss";
                ticks.LabelFormatter = time => time.ToString(format, System.Globalization.CultureInfo.InvariantCulture);
            }
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

        // A legend panel above the plot never hides data. ShowLegend(Edge) adds a panel on every call, so old ones go first.
        foreach (var panel in plot.Axes.GetPanels().OfType<ScottPlot.Panels.LegendPanel>().ToList())
        {
            plot.Axes.Remove(panel);
        }

        if (showLegend)
        {
            var legendPanel = plot.ShowLegend(Edge.Top);
            legendPanel.Padding = new PixelPadding(0);
            plot.Legend.Margin = new PixelPadding(0);
            plot.Legend.Padding = new PixelPadding(6, 6, 2, 2);
            plot.Legend.Orientation = Orientation.Horizontal;
            if (Font is not null)
            {
                plot.Legend.FontName = Font;
            }
        }
        else
        {
            plot.HideLegend();
        }

        ApplyStyle(plot); // axes created above (date axis) get the same ink
        return true;
    }

    public static void ApplyStyle(Plot plot)
    {
        if (Font is not null)
        {
            plot.Font.Set(Font);
        }

        plot.FigureBackground.Color = Color.FromHex(ChartPalette.Surface);
        plot.DataBackground.Color = Color.FromHex(ChartPalette.Surface);
        plot.Axes.Color(Color.FromHex(ChartPalette.MutedInk));
        plot.Axes.FrameColor(Color.FromHex(ChartPalette.Axis));
        plot.Grid.MajorLineColor = Color.FromHex(ChartPalette.Grid);
        plot.Legend.BackgroundColor = Color.FromHex(ChartPalette.Surface);
        plot.Legend.FontColor = Color.FromHex(ChartPalette.SecondaryInk);
        plot.Legend.OutlineColor = Color.FromHex(ChartPalette.Surface);
    }

    /// <summary>Splits a series wherever consecutive points are further apart than <paramref name="gap"/>.</summary>
    public static IEnumerable<(double[] Xs, double[] Ys)> Segments(ChartSeries series, double gap)
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
}
