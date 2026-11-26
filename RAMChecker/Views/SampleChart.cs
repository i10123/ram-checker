using System.Windows;
using System.Windows.Media;
using RAMChecker.Models;

namespace RAMChecker.Views;

public class SampleChart : FrameworkElement
{
    public static readonly DependencyProperty SamplesProperty = DependencyProperty.Register(nameof(Samples),
        typeof(IEnumerable<LoadSample>), typeof(SampleChart), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    public IEnumerable<LoadSample>? Samples { get => (IEnumerable<LoadSample>?)GetValue(SamplesProperty); set => SetValue(SamplesProperty, value); }
    public bool ShowClock { get; set; }
    public bool ShowCommit { get; set; }

    protected override void OnRender(DrawingContext dc)
    {
        var samples = Samples?.ToList();
        var pen = new Pen(Brushes.DeepSkyBlue, 1.5);
        dc.DrawLine(new Pen(Brushes.Gray, 1), new(0, ActualHeight-2), new(ActualWidth, ActualHeight-2));
        if (samples is not { Count: > 0 }) return;
        double first = samples[0].Seconds, duration = Math.Max(0.001, samples[^1].Seconds - first);
        double maximum = ShowClock ? Math.Max(1, samples.Max(s => s.ClockMHz) ?? 1) : 100;
        Point? previous = null;
        foreach (var sample in samples)
        {
            if ((ShowClock ? sample.ClockMHz : ShowCommit ? sample.CommitPercent : sample.PhysicalPercent) is not double value) { previous = null; continue; }
            Point point = new((sample.Seconds-first)/duration * ActualWidth, 2 + (maximum-value)/maximum * (ActualHeight-4));
            if (previous is Point from) dc.DrawLine(pen, from, point);
            dc.DrawEllipse(pen.Brush, null, point, 1.5, 1.5);
            previous = point;
        }
    }
}
