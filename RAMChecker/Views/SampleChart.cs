using System.Windows;
using System.Windows.Media;
using System.Windows.Input;
using System.Globalization;
using RAMChecker.Helpers;
using RAMChecker.Models;

namespace RAMChecker.Views;

public class SampleChart : FrameworkElement
{
    public static readonly DependencyProperty SamplesProperty = DependencyProperty.Register(nameof(Samples),
        typeof(IEnumerable<LoadSample>), typeof(SampleChart), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender,
            (sender, _) => ((SampleChart)sender).selectedSecond = null));
    public IEnumerable<LoadSample>? Samples { get => (IEnumerable<LoadSample>?)GetValue(SamplesProperty); set => SetValue(SamplesProperty, value); }
    public bool ShowCommit { get; set; }

    private const double left = 44, right = 8;
    private long lastSecond = -1;
    private long? selectedSecond;


    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if (lastSecond < 0 || ActualWidth <= left + right) return;
        SelectAt(e.GetPosition(this).X);
        CaptureMouse();
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (IsMouseCaptured && e.LeftButton == MouseButtonState.Pressed)
            SelectAt(e.GetPosition(this).X);
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (!IsMouseCaptured) return;
        SelectAt(e.GetPosition(this).X);
        ReleaseMouseCapture();
        e.Handled = true;
    }

    private void SelectAt(double x)
    {
        double fraction = Math.Clamp((x - left) / (ActualWidth - left - right), 0, 1);
        long second = (long)Math.Round(fraction * lastSecond);
        if (selectedSecond == second) return;
        selectedSecond = second;
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        lastSecond = -1;
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(RenderSize));
        var samples = Samples?.ToList();
        if (ActualWidth < 84 || ActualHeight < 64) return;
        const double top = 12, bottom = 28, maximum = 100;
        double chartWidth = ActualWidth - left - right, chartHeight = ActualHeight - top - bottom;
        var gridPen = new Pen(new SolidColorBrush(Color.FromArgb(36, 255, 255, 255)), 0.75);
        var axisPen = new Pen(new SolidColorBrush(Color.FromArgb(72, 255, 255, 255)), 0.75);
        var linePen = new Pen(ShowCommit ? Brushes.MediumAquamarine : Brushes.DeepSkyBlue, 2.5);
        if (samples is not { Count: > 0 } || (ShowCommit && samples.All(s => s.CommitPercent is null)))
        {
            DrawLabel(dc, UiLanguage.Current.Display("Нет измерений"), ActualWidth / 2, ActualHeight / 2, TextAlignment.Center);
            return;
        }
        foreach (double percent in new[] { 100d, 75d, 50d, 25d, 0d })
        {
            double y = top + (maximum - percent) / maximum * chartHeight;
            dc.DrawLine(percent == 0 ? axisPen : gridPen, new(left, y), new(left + chartWidth, y));
            DrawLabel(dc, $"{percent:0}%", left - 8, y, TextAlignment.Right);
        }
        lastSecond = (long)Math.Floor(samples[^1].Seconds);
        string unit = UiLanguage.Current.English ? "s" : "с";
        double labelY = top + chartHeight + bottom / 2;
        DrawLabel(dc, $"0 {unit}", left, labelY, TextAlignment.Left);
        if (lastSecond > 0 && chartWidth >= 140)
            DrawLabel(dc, $"{lastSecond} {unit}", left + chartWidth, labelY, TextAlignment.Right);

        // Для длинной проверки рисуем границы изменений, не создавая точку на каждую секунду.
        var seconds = new SortedSet<long> { 0, lastSecond };
        if (lastSecond <= chartWidth / 3)
            for (long second = 1; second < lastSecond; second++) seconds.Add(second);
        else
            foreach (var sample in samples)
            {
                long second = (long)Math.Floor(sample.Seconds);
                seconds.Add(second);
                if (second > 0) seconds.Add(second - 1);
            }
        if (selectedSecond is long selection && selection <= lastSecond) seconds.Add(selection);

        int index = -1;
        Point? previous = null, selectedPoint = null;
        double selectedValue = 0;
        foreach (long second in seconds)
        {
            while (index + 1 < samples.Count && samples[index + 1].Seconds < second + 1) index++;
            double? reading = index < 0 ? null : ShowCommit ? samples[index].CommitPercent : samples[index].PhysicalPercent;
            if (reading is not double value) { previous = null; continue; }
            Point point = new(left + second * chartWidth / Math.Max(1, lastSecond), top + (maximum - value) / maximum * chartHeight);
            if (previous is Point from) dc.DrawLine(linePen, from, point);
            double radius = seconds.Count <= 32 ? 2.5 : 1.5;
            dc.DrawEllipse(linePen.Brush, null, point, radius, radius);
            if (selectedSecond == second) (selectedPoint, selectedValue) = (point, value);
            previous = point;
        }
        if (selectedPoint is Point selected)
        {
            var label = new FormattedText($"{selectedSecond} {unit} · {selectedValue:0.##} %", CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                new Typeface("Segoe UI Semibold"), 14, Brushes.White, VisualTreeHelper.GetDpi(this).PixelsPerDip);
            var point = selected;
            dc.DrawLine(axisPen, new(point.X, top), new(point.X, top + chartHeight));
            dc.DrawEllipse(linePen.Brush, new Pen(Brushes.White, 1.5), point, 4.5, 4.5);
            double width = label.Width + 14, height = label.Height + 6;
            double x = Math.Clamp(point.X - width / 2, 0, Math.Max(0, ActualWidth - width));
            double y = point.Y >= height + 8 ? point.Y - height - 8 : point.Y + 8;
            dc.DrawRoundedRectangle(Brushes.Black, new Pen(linePen.Brush, 1), new Rect(x, y, width, height), 5, 5);
            dc.DrawText(label, new Point(x + 7, y + 3));
        }
    }

    private void DrawLabel(DrawingContext dc, string text, double x, double y, TextAlignment alignment)
    {
        var label = new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            new Typeface("Segoe UI"), 14, Brushes.Gainsboro, VisualTreeHelper.GetDpi(this).PixelsPerDip)
        {
            TextAlignment = alignment
        };
        dc.DrawText(label, new(x, y - label.Height / 2));
    }
}
