using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using Microsoft.Win32;
using RAMChecker.Services;
using RAMChecker.ViewModels;

namespace RAMChecker
{
    // Код окна; InitializeComponent загружает разметку из MainWindow.xaml.
    public partial class MainWindow : Window
    {
        private bool closeAllowed;
        public MainWindow()
        {
            InitializeComponent();
        }

        private async void Window_Loaded(object sender, RoutedEventArgs e)
        {
            var viewModel = (MainViewModel)DataContext;
            await viewModel.InitializeAsync();
        }

        private void Administrator_Click(object sender, RoutedEventArgs e)
        {
            if (!((MainViewModel)DataContext).CanConfigure)
                return;
            try
            {
                var start = new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, "RAMChecker.exe"))
                {
                    UseShellExecute = true,
                    Verb = "runas",
                    WorkingDirectory = AppContext.BaseDirectory
                };
                if (Process.Start(start) is not null)
                    Close();
            }
            catch (Win32Exception ex) when (ex.NativeErrorCode == 1223) { } // Отмена UAC оставляет текущее окно открытым.
            catch (Exception ex) { MessageBox.Show(this, ex.Message, "Не удалось перезапустить приложение", MessageBoxButton.OK, MessageBoxImage.Error); }
        }

        private async void Window_Closing(object? sender, CancelEventArgs e)
        {
            if (closeAllowed)
                return;
            e.Cancel = true;
            IsEnabled = false;
            await ((MainViewModel)DataContext).ShutdownAsync();
            await Dispatcher.InvokeAsync(() => { closeAllowed = true; Close(); });
        }

        private void Export_Click(object sender, RoutedEventArgs e)
        {
            var result = ((MainViewModel)DataContext).SelectedResult;
            if (result is null)
                return;
            string type = (string)((Button)sender).Tag;
            var dialog = new SaveFileDialog
            {
                FileName = $"RAM-{result.Started:yyyyMMdd-HHmmss}.{type}",
                Filter = type == "csv" ? "CSV (*.csv)|*.csv" : "HTML (*.html)|*.html",
                DefaultExt = type,
                AddExtension = true
            };
            if (dialog.ShowDialog(this) != true)
                return;
            try
            {
                File.WriteAllText(dialog.FileName, type == "csv" ? ReportExporter.Csv(result) : ReportExporter.Html(result), new UTF8Encoding(true));
            }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, "Не удалось сохранить отчёт", MessageBoxButton.OK, MessageBoxImage.Error); }
        }

        private void Print_Click(object sender, RoutedEventArgs e)
        {
            var r = ((MainViewModel)DataContext).SelectedResult;
            if (r is null)
                return;
            var dialog = new PrintDialog();
            if (dialog.ShowDialog() != true)
                return;
            try
            {
                var document = new FlowDocument { FontFamily = new FontFamily("Segoe UI"), FontSize = 12, ColumnWidth = double.PositiveInfinity, PagePadding = new Thickness(36) };
                document.Blocks.Add(new Paragraph(new Run("RAM Diagnostic Suite")) { FontSize = 24, FontWeight = FontWeights.Bold });
                document.Blocks.Add(new Paragraph(new Run($"{r.StatusText}\n{r.StartedText}\n{r.Description}\nОшибок: {r.ErrorCount} · полных проходов: {r.Passes}\n{r.Detail}")));
                document.Blocks.Add(new Paragraph(new Run("Проверка буфера процесса не покрывает всю физическую RAM и не определяет неисправную планку.")));
                document.Blocks.Add(new Paragraph(new Run($"{r.Recommendation}\n{r.Algorithms}\nКонфигурация при запуске:\n{r.Configuration}")));
                foreach (var error in r.Errors)
                    document.Blocks.Add(new Paragraph(new Run($"{error.OffsetText}: ожидалось {error.ExpectedText}, прочитано {error.ActualText} · {error.Stage} · проход {error.Pass}")));
                document.Blocks.Add(new Paragraph(new Run(string.Join(Environment.NewLine, r.Events))));
                document.PageWidth = dialog.PrintableAreaWidth;
                dialog.PrintDocument(((IDocumentPaginatorSource)document).DocumentPaginator, "RAM Diagnostic Suite — отчёт");
            }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, "Не удалось напечатать отчёт", MessageBoxButton.OK, MessageBoxImage.Error); }
        }
    }
}
