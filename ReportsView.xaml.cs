using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;

namespace IMPWeldPhotos;

/// <summary>
/// The Poročila page: every report of the picked project, its PDF rendered page by page,
/// and the two things done with a finished report — make the PDF (through Word) and edit
/// the document in Word. Nothing on the share is held open: the PDF is read into memory.
/// </summary>
public partial class ReportsView : UserControl
{
    private AppConfig _cfg = null!;
    private UserSettings _settings = null!;
    private List<ProjectEntry> _projects = new();
    private List<ReportEntry> _reports = new();
    private ReportEntry? _current;
    private CancellationTokenSource? _previewCts;
    private string? _project;
    private bool _settingSource;
    private bool _busy;

    /// <summary>Report folder to select on the next refresh, right after making one.</summary>
    public string? PendingFolder { get; set; }

    public ReportsView()
    {
        InitializeComponent();
    }

    public void Initialize(AppConfig cfg, UserSettings settings)
    {
        _cfg = cfg;
        _settings = settings;
        _project = settings.ViewerProject;
        ProjectBox.Picked += OnProjectPicked;
    }

    /// <summary>Re-reads the share: the project list, then the picked project's reports.</summary>
    public async Task RefreshAsync()
    {
        var root = _cfg.DestinationRoot;
        var (isos, _) = await Task.Run(() => ServerIndex.Load(root));
        _projects = ProjectEntry.FromIsos(isos).Where(p => p.Code.Length > 0).ToList();

        var wanted = PendingFolder;
        if (wanted != null)
        {
            // Straight after making a report: show that project, whatever was picked before.
            var project = Path.GetDirectoryName(Path.GetDirectoryName(wanted));
            if (project != null) _project = Path.GetFileName(project);
        }
        if (_project == null || _projects.All(p => !string.Equals(p.FolderName, _project, StringComparison.OrdinalIgnoreCase)))
            _project = _projects.FirstOrDefault()?.FolderName;
        ProjectBox.SetProjects(_projects, _project);

        await LoadReportsAsync(wanted);
        PendingFolder = null;
    }

    private async void OnProjectPicked(ProjectEntry project)
    {
        _project = project.FolderName;
        _settings.ViewerProject = _project;
        _settings.Save();
        await LoadReportsAsync(null);
    }

    private async Task LoadReportsAsync(string? selectFolder)
    {
        _reports = _project == null
            ? new List<ReportEntry>()
            : await Task.Run(() => ReportIndex.Load(Path.Combine(_cfg.DestinationRoot, _project)));

        _settingSource = true;
        ReportList.ItemsSource = _reports;
        _settingSource = false;

        ReportCountText.Text = _reports.Count == 0
            ? "Ni poročil"
            : MainWindow.Plural(_reports.Count, "poročilo", "poročili", "poročila", "poročil");

        var pick = selectFolder != null
            ? _reports.FirstOrDefault(r => string.Equals(r.Folder, selectFolder, StringComparison.OrdinalIgnoreCase))
            : _reports.FirstOrDefault(r => string.Equals(r.Folder, _current?.Folder, StringComparison.OrdinalIgnoreCase));

        if (pick != null)
        {
            ReportList.SelectedItem = pick;
            ReportList.ScrollIntoView(pick);
            return;
        }

        _current = null;
        ShowEmpty(_reports.Count == 0 ? "NI POROČIL" : "IZBERITE POROČILO",
                  _reports.Count == 0
                      ? "Poročila nastanejo na strani Pregled z gumbom Poročilo."
                      : "Na levi izberite poročilo; prikaže se njegov PDF.");
    }

    private async void OnReportSelected(object sender, SelectionChangedEventArgs e)
    {
        if (_settingSource || ReportList.SelectedItem is not ReportEntry report) return;
        await ShowReportAsync(report);
    }

    private async Task ShowReportAsync(ReportEntry report)
    {
        _current = report;
        ReportTitle.Text = report.Number;
        ReportSubtitle.Text = report.Summary;
        ReportHeader.Visibility = Visibility.Visible;
        PdfButtonText.Text = report.HasPdf ? "Osveži PDF" : "Ustvari PDF";
        PdfButton.ToolTip = report.HasPdf
            ? "Znova ustvari PDF iz dokumenta Word"
            : "Shrani poročilo kot PDF (prek Worda)";
        await ShowPagesAsync(report);
    }

    private async Task ShowPagesAsync(ReportEntry report)
    {
        _previewCts?.Cancel();
        var cts = new CancellationTokenSource();
        _previewCts = cts;

        if (report.PdfPath is not { } pdf)
        {
            ShowEmpty("PDF ŠE NI USTVARJEN",
                      "Poročilo je dokument Word. Za predogled in pošiljanje ga shranite kot PDF.",
                      showPdfButton: true);
            return;
        }

        Pages.ItemsSource = null;
        ShowEmpty("PRIPRAVLJAM PREDOGLED", Path.GetFileName(pdf));
        List<BitmapSource> pages;
        try
        {
            pages = await Task.Run(() => PdfPreview.Render(pdf), cts.Token);
        }
        catch (Exception ex)
        {
            if (cts.IsCancellationRequested) return;
            ShowEmpty("PREDOGLEDA NI MOGOČE PRIKAZATI", ex.Message, showPdfButton: true);
            return;
        }
        if (cts.IsCancellationRequested || _current != report) return;

        Pages.ItemsSource = pages;
        CenterEmpty.Visibility = Visibility.Collapsed;
        PagesScroll.Visibility = Visibility.Visible;
        PagesScroll.ScrollToTop();
    }

    private void ShowEmpty(string title, string text, bool showPdfButton = false)
    {
        Pages.ItemsSource = null;
        PagesScroll.Visibility = Visibility.Collapsed;
        EmptyTitle.Text = title;
        EmptyText.Text = text;
        EmptyPdfButton.Visibility = showPdfButton ? Visibility.Visible : Visibility.Collapsed;
        CenterEmpty.Visibility = Visibility.Visible;
    }

    // ─── Actions ─────────────────────────────────────────────────────────────

    private async void OnPdfClick(object sender, RoutedEventArgs e)
    {
        if (_current is not { } report || _busy) return;
        _busy = true;
        PdfButton.IsEnabled = EmptyPdfButton.IsEnabled = false;
        var wasText = PdfButtonText.Text;
        PdfButtonText.Text = "Ustvarjam …";
        ShowEmpty("USTVARJAM PDF", "Word pripravlja PDF; to traja nekaj sekund.");
        try
        {
            await WordExport.ToPdfAsync(report.DocumentPath);
        }
        catch (Exception ex)
        {
            ShowEmpty("PDF NI USPEL", ex.Message, showPdfButton: true);
            return;
        }
        finally
        {
            _busy = false;
            PdfButton.IsEnabled = EmptyPdfButton.IsEnabled = true;
            PdfButtonText.Text = wasText;
        }

        // Re-read the folder so the entry knows about its PDF, then show it.
        if (ReportEntry.Create(report.Folder) is { } updated)
        {
            var index = _reports.FindIndex(r => string.Equals(r.Folder, report.Folder, StringComparison.OrdinalIgnoreCase));
            if (index >= 0) _reports[index] = updated;
            _settingSource = true;
            ReportList.ItemsSource = null;
            ReportList.ItemsSource = _reports;
            ReportList.SelectedItem = updated;
            _settingSource = false;
            await ShowReportAsync(updated);
        }
    }

    private void OnWordClick(object sender, RoutedEventArgs e)
    {
        if (_current is not { } report) return;
        Start(report.DocumentPath);
    }

    private void OnOpenFolderClick(object sender, RoutedEventArgs e)
    {
        if (_current is not { } report) return;
        Start(report.Folder);
    }

    private void Start(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            ShowEmpty("NI MOGOČE ODPRETI", ex.Message);
        }
    }
}
