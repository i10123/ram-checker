using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using RAMChecker.Models;
using RAMChecker.Services;

namespace RAMChecker.Views;

public partial class ResultsView : UserControl
{
    public ResultsView() => InitializeComponent();

    private void Export_Click(object sender, RoutedEventArgs e)
    {
        if (((Button)sender).DataContext is not DiagnosticResult result)
            return;
        var dialog = new SaveFileDialog {
            FileName = $"MemScope {result.Started:dd-MM-yyyy HH-mm-ss}.html",
            Filter = "HTML|*.html|PDF|*.pdf|CSV|*.csv|Markdown|*.md"
        };
        if (dialog.ShowDialog() != true)
            return;
        try
        {
            ReportExporter.Save(result, dialog.FileName);
            Process.Start(new ProcessStartInfo(dialog.FileName) { UseShellExecute = true });
        }
        catch (Exception ex) {
            MessageBox.Show(ex.Message, RAMChecker.Helpers.UiLanguage.Current.Display("Не удалось сохранить отчёт"), MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
}
