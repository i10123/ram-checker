using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using RAMChecker.Models;
using RAMChecker.Services;

namespace RAMChecker.Views;

public partial class RezultatyView : UserControl
{
    public RezultatyView() => InitializeComponent();

    // Сохраняет отчёт выбранной проверки. Для PDF отчёт открывается в браузере, где его можно напечатать в PDF.
    private void Export_Click(object sender, RoutedEventArgs e)
    {
        if (((Button)sender).DataContext is not DiagnosticResult result)
            return;
        string type = (string)((Button)sender).Tag;
        string path = Path.Combine(Path.GetTempPath(), $"RAM-{result.Started:yyyyMMdd-HHmmss}.html");
        if (type != "pdf")
        {
            var dialog = new SaveFileDialog { FileName = Path.ChangeExtension(Path.GetFileName(path), type), Filter = $"{type.ToUpper()}|*.{type}" };
            if (dialog.ShowDialog() != true)
                return;
            path = dialog.FileName;
        }
        try
        {
            File.WriteAllText(path, type == "csv" ? ReportExporter.Csv(result) : ReportExporter.Html(result), new UTF8Encoding(true));
            if (type == "pdf")
                Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex) { MessageBox.Show(ex.Message, "Не удалось сохранить отчёт", MessageBoxButton.OK, MessageBoxImage.Error); }
    }
}
