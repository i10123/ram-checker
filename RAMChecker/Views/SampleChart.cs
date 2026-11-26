using System.Windows;
using System.Windows.Media;
using System.Globalization;
using RAMChecker.Helpers;
using RAMChecker.Models;

namespace RAMChecker.Views;

public class SampleChart : FrameworkElement
{
    public static readonly DependencyProperty SamplesProperty = DependencyProperty.Register(nameof(Samples),
        typeof(IEnumerable<LoadSample>), typeof(SampleChart), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    public IEnumerable<LoadSample>? Samples { get => (IEnumerable<LoadSample>?)GetValue(SamplesProperty); set => SetValue(SamplesProperty, value); }
    public bool ShowCommit { get; set; }

    protected override void OnRender(DrawingContext dc)
    {
        var samples = Samples?.ToList();
        if (ActualWidth < 48 || ActualHeight < 30) return;
        const double left = 27, right = 4, top = 15, bottom = 16, maximum = 100;
        double chartWidth = ActualWidth - left - right, chartHeight = ActualHeight - top - bottom;
        var gridPen = new Pen(new SolidColorBrush(Color.FromArgb(90, 255, 255, 255)), 0.75);
        var axisPen = new Pen(new SolidColorBrush(Color.FromArgb(150, 255, 255, 255)), 0.75);
        var linePen = new Pen(Brushes.DeepSkyBlue, 1.5);
        DrawLabel(dc, UiLanguage.Current.English ? "Usage, %" : "Загрузка, %", left, 0, false);
        dc.DrawLine(axisPen, new(left, top), new(left, top + chartHeight));
        foreach (double percent in new[] { 100d, 50d, 0d })
        {
            double y = top + (maximum - percent) / maximum * chartHeight;
            dc.DrawLine(percent == 0 ? axisPen : gridPen, new(left, y), new(left + chartWidth, y));
            DrawLabel(dc, percent.ToString("0"), left - 3, y - 5, true);
        }
        if (samples is not { Count: > 0 }) return;
        double first = samples[0].Seconds, duration = Math.Max(0.001, samples[^1].Seconds - first);
        DrawCenteredLabel(dc, UiLanguage.Current.English ? "Time" : "Время", left + chartWidth / 2, top + chartHeight + 2);
        Point? previous = null;
        foreach (var sample in samples)
        {
            if ((ShowCommit ? sample.CommitPercent : sample.PhysicalPercent) is not double value) { previous = null; continue; }
            Point point = new(left + (sample.Seconds - first) / duration * chartWidth, top + (maximum - value) / maximum * chartHeight);
            if (previous is Point from) dc.DrawLine(linePen, from, point);
            dc.DrawEllipse(linePen.Brush, null, point, 1.5, 1.5);
            previous = point;
        }
    }

    private void DrawLabel(DrawingContext dc, string text, double x, double y, bool alignRight)
    {
        var label = new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            new Typeface("Segoe UI"), 8, new SolidColorBrush(Color.FromArgb(190, 255, 255, 255)), VisualTreeHelper.GetDpi(this).PixelsPerDip);
        dc.DrawText(label, new(alignRight ? x - label.Width : x, y));
    }

    private void DrawCenteredLabel(DrawingContext dc, string text, double x, double y)
    {
        var label = new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            new Typeface("Segoe UI"), 8, new SolidColorBrush(Color.FromArgb(190, 255, 255, 255)), VisualTreeHelper.GetDpi(this).PixelsPerDip);
        dc.DrawText(label, new(x - label.Width / 2, y));
    }
}
