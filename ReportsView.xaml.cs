using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Web.WebView2.Core;

namespace IMPWeldPhotos;

/// <summary>
/// The Poročila page: every report of the picked project, the PDF itself shown in Edge's
/// viewer (zoom, search, print), and the two things done with a finished report — make the
/// PDF (through Word) and edit the document in Word.
/// </summary>
public partial class ReportsView : UserControl
{
    private AppConfig _cfg = null!;
    private UserSettings _settings = null!;
    private List<ProjectEntry> _projects = new();
    private List<IsoEntry> _allIsos = new();
    private List<ReportEntry> _reports = new();
    private ReportEntry? _current;
    private bool _viewerBroken;
    private string? _project;
    private bool _settingSource;
    private bool _busy;

    /// <summary>Report folder to select on the next refresh, right after making one.</summary>
    public string? PendingFolder { get; set; }

    /// <summary>Asks the window to make a report for one of these skids; it owns the dialogs.</summary>
    public event Func<IReadOnlyList<ReportRequest>, Task>? NewReportRequested;

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
        _allIsos = isos;
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

        NewReportButton.IsEnabled = _project != null;
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
        HidePdf();
        ShowEmpty(_reports.Count == 0 ? "NI POROČIL" : "IZBERITE POROČILO",
                  _reports.Count == 0
                      ? "Novo poročilo naredite z gumbom Novo poročilo."
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
        await ShowPdfAsync(report);
    }

    private async Task ShowPdfAsync(ReportEntry report)
    {
        if (report.PdfPath is not { } pdf)
        {
            HidePdf();
            ShowEmpty("PDF ŠE NI USTVARJEN",
                      "Poročilo je dokument Word. Za predogled in pošiljanje ga shranite kot PDF.",
                      showPdfButton: true);
            return;
        }

        if (!await EnsureViewerAsync())
        {
            HidePdf();
            ShowEmpty("PREDOGLEDA NI MOGOČE PRIKAZATI",
                      "Za predogled v aplikaciji je potreben Microsoft Edge WebView2. " +
                      "PDF lahko odprete v svojem pregledovalniku.",
                      showOpenPdfButton: true);
            return;
        }

        // A fresh copy each time: Word may have just rewritten the file.
        PdfView.CoreWebView2!.Navigate(new Uri(pdf).AbsoluteUri + "#zoom=page-width");
        CenterEmpty.Visibility = Visibility.Collapsed;
        PdfView.Visibility = Visibility.Visible;
    }

    /// <summary>Starts the embedded viewer once, with its data next to the app's settings
    /// (the folder beside the exe may be read-only or on a share).</summary>
    private async Task<bool> EnsureViewerAsync()
    {
        if (PdfView.CoreWebView2 != null) return true;
        if (_viewerBroken) return false;
        try
        {
            var dataFolder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "IMP", "IMPWeldPhotos", "webview");
            Directory.CreateDirectory(dataFolder);
            var environment = await CoreWebView2Environment.CreateAsync(null, dataFolder);
            await PdfView.EnsureCoreWebView2Async(environment);
            PdfView.CoreWebView2!.Settings.AreDevToolsEnabled = false;
            PdfView.CoreWebView2.Settings.IsStatusBarEnabled = false;
            return true;
        }
        catch (Exception)
        {
            _viewerBroken = true;
            return false;
        }
    }

    private void HidePdf()
    {
        PdfView.Visibility = Visibility.Collapsed;
        if (PdfView.CoreWebView2 != null) PdfView.CoreWebView2.Navigate("about:blank");
    }

    private void ShowEmpty(string title, string text, bool showPdfButton = false, bool showOpenPdfButton = false)
    {
        EmptyTitle.Text = title;
        EmptyText.Text = text;
        EmptyPdfButton.Visibility = showPdfButton ? Visibility.Visible : Visibility.Collapsed;
        EmptyOpenButton.Visibility = showOpenPdfButton ? Visibility.Visible : Visibility.Collapsed;
        CenterEmpty.Visibility = Visibility.Visible;
    }

    private void OnOpenPdfClick(object sender, RoutedEventArgs e)
    {
        if (_current?.PdfPath is { } pdf) Start(pdf);
    }

    // ─── Actions ─────────────────────────────────────────────────────────────

    /// <summary>One report per skid, so the window is handed this project's skids to choose from.</summary>
    private async void OnNewReportClick(object sender, RoutedEventArgs e)
    {
        if (NewReportRequested is not { } handler || _project is not { } project) return;
        var projectFolder = Path.Combine(_cfg.DestinationRoot, project);
        var skids = _allIsos
            .Where(i => string.Equals(i.ProjectName, project, StringComparison.OrdinalIgnoreCase))
            .GroupBy(i => i.UnitName, StringComparer.OrdinalIgnoreCase)
            .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
            .Select(g => new ReportRequest(projectFolder, project, g.Key, g.ToList()))
            .ToList();

        NewReportButton.IsEnabled = false;
        try
        {
            await handler(skids);
        }
        finally
        {
            NewReportButton.IsEnabled = true;
        }
    }

    private async void OnPdfClick(object sender, RoutedEventArgs e)
    {
        if (_current is not { } report || _busy) return;
        _busy = true;
        PdfButton.IsEnabled = EmptyPdfButton.IsEnabled = false;
        var wasText = PdfButtonText.Text;
        PdfButtonText.Text = "Ustvarjam …";
        HidePdf();
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
