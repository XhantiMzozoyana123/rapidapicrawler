using RapidApiCrawler.Application;
using RapidApiCrawler.Infrastructure;
using System.Text;

namespace RapidApiCrawler
{
    public partial class MainPage : ContentPage
    {
        private readonly CrawlOrchestrator _orchestrator;
        private readonly ISearchRunRepository _repository;
        private readonly ScraperOptions _scraperOptions;
        private readonly ICsvExporter _csvExporter;
        private CancellationTokenSource? _cts;
        private int _lastRunId = -1;

        public MainPage(CrawlOrchestrator orchestrator, ISearchRunRepository repository, ScraperOptions scraperOptions, ICsvExporter csvExporter)
        {
            _orchestrator = orchestrator;
            _repository = repository;
            _scraperOptions = scraperOptions;
            _csvExporter = csvExporter;
            InitializeComponent();
            _orchestrator.Progress += OnProgress;
            HeadlessSwitch.IsToggled = _scraperOptions.Headless;
            UpdateHeadlessHint();
        }

        private void OnHeadlessToggled(object? sender, ToggledEventArgs e)
        {
            _scraperOptions.Headless = e.Value;
            UpdateHeadlessHint();
        }

        private void UpdateHeadlessHint()
            => HeadlessHint.Text = _scraperOptions.Headless ? "(on: browser is hidden)" : "(off: watch the browser)";

        private void OnProgress(object? sender, ProgressEventArgs e)
        {
            MainThread.BeginInvokeOnMainThread(() =>
            {
                LogEditor.Text = new StringBuilder(LogEditor.Text).AppendLine(e.Message).ToString();
                StatusLabel.Text = e.Message;
                CrawlProgress.Progress = Math.Min(1.0, CrawlProgress.Progress + 0.02);
            });
        }

        private async void OnStartClicked(object? sender, EventArgs e)
        {
            var keyword = KeywordEntry.Text?.Trim();
            if (string.IsNullOrEmpty(keyword))
            {
                await DisplayAlert("Missing keyword", "Please enter a search keyword.", "OK");
                return;
            }

            StartBtn.IsEnabled = false;
            LogEditor.Text = string.Empty;
            CrawlProgress.Progress = 0;
            _cts = new CancellationTokenSource();

            try
            {
                var run = await _orchestrator.RunAsync(keyword, AnalyzeCheck.IsChecked, _cts.Token);
                _lastRunId = run.Id;
                StatusLabel.Text = $"Done — {run.ListingsFound} APIs found across {run.PagesCrawled} captured pages.";
            }
            catch (OperationCanceledException)
            {
                StatusLabel.Text = "Cancelled.";
            }
            catch (Exception ex)
            {
                StatusLabel.Text = "Failed: " + ex.Message;
                await DisplayAlert("Crawl error", ex.Message, "OK");
            }
            finally
            {
                StartBtn.IsEnabled = true;
            }
        }

        private async void OnDownloadCsvClicked(object? sender, EventArgs e)
        {
            string directory;
            try
            {
                directory = OperatingSystem.IsWindows()
                    ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads")
                    : Path.Combine(FileSystem.AppDataDirectory, "Exports");
                Directory.CreateDirectory(directory);

                var files = await _csvExporter.ExportAllToCsvAsync(directory);
                if (files.Count == 0)
                {
                    await DisplayAlert("Nothing to export", "No tables with data yet — run a crawl first.", "OK");
                    return;
                }

                var openFolder = await DisplayAlert("CSV exported",
                    $"Saved {files.Count} file(s) to:\n{directory}\n\nOpen the folder?", "Open folder", "OK");
                if (openFolder && OperatingSystem.IsWindows())
                    System.Diagnostics.Process.Start("explorer.exe", directory);
            }
            catch (Exception ex)
            {
                await DisplayAlert("Export failed", ex.Message, "OK");
            }
        }

        private async void OnViewDatabaseClicked(object? sender, EventArgs e)
            => await Navigation.PushAsync(new DatabasePage(_repository));

        private async void OnShowReportClicked(object? sender, EventArgs e)
        {
            if (_lastRunId < 0)
            {
                await DisplayAlert("No report", "Run a crawl with AI analysis first.", "OK");
                return;
            }
            var runs = await _repository.GetRunsAsync();
            var run = runs.FirstOrDefault(r => r.Id == _lastRunId);
            await Navigation.PushAsync(new ReportPage(_repository, _lastRunId, run?.Keyword ?? ""));
        }
    }
}

namespace RapidApiCrawler
{
    public class ReportPage : ContentPage
    {
        public ReportPage(ISearchRunRepository repository, int runId, string keyword)
        {
            Title = $"Report — {keyword}";
            var editor = new Editor { IsReadOnly = true, AutoSize = EditorAutoSizeOption.TextChanges };
            Content = new ScrollView { Content = editor };
            Loaded += async (_, _) =>
            {
                var report = await repository.GetLatestReportAsync(runId);
                editor.Text = string.IsNullOrWhiteSpace(report)
                    ? "No AI report was generated for this run (either analysis was unchecked or the crawl found no APIs)."
                    : report;
            };
        }
    }

    public class DatabasePage : ContentPage
    {
        private readonly ISearchRunRepository _repository;
        private readonly VerticalStackLayout _panel;
        private readonly ActivityIndicator _busy;

        public DatabasePage(ISearchRunRepository repository)
        {
            _repository = repository;
            Title = "Database";

            var tablePicker = new Picker { Title = "Select a table" };
            tablePicker.SelectedIndexChanged += async (_, _) =>
            {
                if (tablePicker.SelectedItem is string name)
                    await LoadTableAsync(name);
            };

            _busy = new ActivityIndicator { IsRunning = false, IsVisible = false };
            _panel = new VerticalStackLayout { Spacing = 4 };

            var content = new Grid
            {
                RowDefinitions = { new RowDefinition(GridLength.Auto), new RowDefinition(GridLength.Auto), new RowDefinition(GridLength.Star) }
            };
            content.Add(tablePicker); Grid.SetRow(tablePicker, 0); Grid.SetColumn(tablePicker, 0);
            content.Add(_busy); Grid.SetRow(_busy, 1); Grid.SetColumn(_busy, 0);
            var scroll = new ScrollView { Content = _panel };
            content.Add(scroll); Grid.SetRow(scroll, 2); Grid.SetColumn(scroll, 0);
            Content = content;

            Loaded += async (_, _) =>
            {
                var tables = await _repository.GetTableNamesAsync();
                foreach (var table in tables)
                    tablePicker.Items.Add(table);
                if (tables.Count > 0)
                {
                    tablePicker.SelectedIndex = 0;
                }
                else
                {
                    _panel.Children.Add(new Label { Text = "No tables found yet — run a crawl first.", Margin = new Thickness(8) });
                }
            };
        }

        private async Task LoadTableAsync(string tableName)
        {
            _busy.IsRunning = true;
            _busy.IsVisible = true;
            try
            {
                var result = await _repository.QueryTableAsync(tableName, 200);
                _panel.Children.Clear();
                if (result.Columns.Length == 0)
                {
                    _panel.Children.Add(new Label { Text = "Table is empty or not found.", Margin = new Thickness(0, 12, 0, 0) });
                    return;
                }

                var grid = new Grid { RowSpacing = 0, ColumnSpacing = 8 };
                for (var c = 0; c < result.Columns.Length; c++)
                    grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
                grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
                for (var c = 0; c < result.Columns.Length; c++)
                    grid.Add(new Label
                    {
                        Text = result.Columns[c],
                        FontAttributes = FontAttributes.Bold,
                        Padding = new Thickness(0, 4, 12, 4),
                        TextColor = Colors.SlateGray
                    }, c, 0);

                for (var r = 0; r < result.Rows.Count; r++)
                {
                    grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
                    for (var c = 0; c < result.Columns.Length; c++)
                    {
                        var raw = result.Rows[r][c];
                        var text = raw switch
                        {
                            null => "(null)",
                            string s when s.Length > 120 => s[..120] + "…",
                            var v => v.ToString() ?? ""
                        };
                        grid.Add(new Label { Text = text, Padding = new Thickness(0, 2, 12, 2), LineBreakMode = LineBreakMode.WordWrap }, c, r + 1);
                    }
                }
                _panel.Children.Add(grid);
                _panel.Children.Add(new Label
                {
                    Text = $"{result.Rows.Count} row(s) shown (of up to 200).",
                    FontSize = 12,
                    TextColor = Colors.Gray,
                    Margin = new Thickness(0, 8, 0, 0)
                });
            }
            finally
            {
                _busy.IsRunning = false;
                _busy.IsVisible = false;
            }
        }
    }
}
