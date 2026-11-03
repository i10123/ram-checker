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

    // Сохраняет HTML-отчёт выбранной проверки в файл, который выберет пользователь.
    private void Export_Click(object sender, RoutedEventArgs e)
    {
        if (((Button)sender).DataContext is not DiagnosticResult result)
            return;
        var dialog = new SaveFileDialog { FileName = $"MemScope-{result.Started:yyyyMMdd-HHmmss}.html", Filter = "HTML|*.html" };
        if (dialog.ShowDialog() != true)
            return;
        try
        {
            File.WriteAllText(dialog.FileName, ReportExporter.Html(result), new UTF8Encoding(true));
        }
        catch (Exception ex) { MessageBox.Show(ex.Message, "Не удалось сохранить отчёт", MessageBoxButton.OK, MessageBoxImage.Error); }
    }
}
