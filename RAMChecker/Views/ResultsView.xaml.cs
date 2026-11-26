using System.Diagnostics;
using System.Security.Principal;
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
            FileName = $"MemScope {result.Started:dd-MM-yyyy HH-mm-ss}",
            Filter = "HTML|*.html|PDF|*.pdf|Markdown|*.md"
        };
        if (dialog.ShowDialog() != true)
            return;
        try
        {
            ReportExporter.Save(result, dialog.FileName);
            // Обработчик расширения настраивается пользователем. Запускаем его только с обычными правами.
            using var identity = WindowsIdentity.GetCurrent();
            if (!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
                Process.Start(new ProcessStartInfo(dialog.FileName) { UseShellExecute = true });
            else
                MessageBox.Show(dialog.FileName, RAMChecker.Helpers.UiLanguage.Current.Display("Отчёт сохранён"),
                    MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex) {
            MessageBox.Show(ex.Message, RAMChecker.Helpers.UiLanguage.Current.Display("Не удалось сохранить отчёт"), MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
}
