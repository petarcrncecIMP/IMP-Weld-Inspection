using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace IMPWeldPhotos;

public partial class MainWindow : Window
{
    private readonly AppConfig _cfg;
    private readonly UserSettings _settings;
    private readonly string? _startFolder;
    private readonly ObservableCollection<WeldPlan> _rows = new();
    private readonly ObservableCollection<string> _warnings = new();
    private readonly Dictionary<string, string> _banner = new();

    private DriveWatcher? _watcher;
    private CommonDataApi? _api;
    private ImportPlan? _plan;
    private ImportResult? _lastResult;
    private string? _lastResultRoot;
    private CancellationTokenSource? _importCts;
    private TaskCompletionSource<string?>? _dialog;
    private bool _busy;
    private bool _importing;
    private bool _viewerReady;
    private bool _reportsReady;
    private Updater.Release? _update;
    private bool _updating;

    public MainWindow(UserSettings settings, string? startFolder)
    {
        InitializeComponent();
        _settings = settings;
        _startFolder = startFolder;
        _cfg = AppConfig.Load(out var configError);
        if (configError != null) _banner["config"] = configError;

        WeldGrid.ItemsSource = _rows;
        WarningsList.ItemsSource = _warnings;
        RestorePlacement();
    }

    // ─── Startup, card arrival, scanning ─────────────────────────────────────

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        UpdateThemeGlyph();
        UpdateBanner();
        // Back from Word (Uredi v Wordu): a report may have changed, so its PDF may be stale now.
        Activated += async (_, _) =>
        {
            if (_reportsReady && ReportsTab.IsChecked == true && DialogOverlay.Visibility != Visibility.Visible)
                await ReportsPage.RecheckAsync();
        };
        _watcher = new DriveWatcher(TimeSpan.FromSeconds(_cfg.PollSeconds));
        _watcher.DriveArrived += OnDriveArrived;

        // The version is visible, not hidden in a tooltip: people need it to tell whether an update landed.
        var version = Updater.IsDevBuild ? "razvojna različica" : $"v{Updater.CurrentVersion}";
        Title = $"IMP Weld Inspection {version}";
        VersionText.Text = version;
        BrandLabel.ToolTip = Title;
        _ = CheckForUpdateAsync();

        // Before the first scan, so the preview is built from the database when it can be.
        await SignInSilentlyAsync();

        if (_startFolder != null) await ScanAsync(_startFolder, auto: false);
        else await ScanRemovableDrivesAsync(auto: true);
    }

    private async void OnDriveArrived(string drive)
    {
        if (_importing) return;
        await ScanAsync(drive, auto: true);
    }

    private async void OnRescanClick(object sender, RoutedEventArgs e)
    {
        if (ViewerTab.IsChecked == true)
        {
            await ViewerPage.RefreshAsync();
            return;
        }
        if (ReportsTab.IsChecked == true)
        {
            await ReportsPage.RefreshAsync();
            return;
        }
        if (_busy) return;
        var root = _plan?.Scan.Root ?? _startFolder;
        if (root != null && Directory.Exists(root) && await ScanAsync(root, auto: false)) return;
        await ScanRemovableDrivesAsync(auto: false);
    }

    private async Task ScanRemovableDrivesAsync(bool auto)
    {
        var drives = await Task.Run(DriveWatcher.ReadyRemovableDrives);
        foreach (var drive in drives.OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
        {
            if (await ScanAsync(drive, auto)) return;
        }
        if (_banner.ContainsKey("share")) return;
        ShowEmpty("VSTAVITE SD KARTICO",
                  auto ? "Aplikacija jo prepozna samodejno."
                       : "Na odstranljivih pogonih ni map z zvari. Aplikacija kartico prepozna samodejno.");
    }

    /// <summary>Scans one card or folder and shows its plan. False when there is
    /// nothing to show; an automatic scan of a drive without weld folders stays silent.</summary>
    private async Task<bool> ScanAsync(string root, bool auto, IReadOnlyList<ImportFailure>? failures = null)
    {
        if (_busy) return false;
        _busy = true;
        SetScanningUi(true, root);
        try
        {
            // Before anything is scanned: no point previewing a card the share can't take.
            var shareProblem = await Task.Run(_cfg.CheckShare);
            SetBanner("share", shareProblem);
            if (shareProblem != null) return false;

            var scan = await Task.Run(() => CardScanner.Scan(root));
            if (!scan.IsCard) return false;

            var resolver = new BomResolver(_cfg, GetApi());
            var plan = await Task.Run(() => ImportPlanner.BuildAsync(scan, _cfg, resolver, CancellationToken.None));
            SetBanner("error", null);
            ShowPlan(plan, failures);
            if (auto) BringToFront();
            return true;
        }
        catch (Exception ex)
        {
            SetBanner("error", $"Pregled {root} ni uspel: {ex.Message}");
            return false;
        }
        finally
        {
            _busy = false;
            SetScanningUi(false, root);
            UpdateAccountUi(); // a scan can find out the API refuses this user
        }
    }

    private CommonDataApi? GetApi()
    {
        if (!_cfg.Api.IsConfigured) return null;
        try
        {
            return _api ??= new CommonDataApi(_cfg.Api);
        }
        catch (Exception ex)
        {
            SetBanner("api", $"Nastavitve CommonData API v {AppConfig.FileName} niso veljavne: {ex.Message}");
            return null;
        }
    }

    // ─── CommonData sign-in ──────────────────────────────────────────────────

    /// <summary>Reuses a cached sign-in (the AutoCAD tools share the cache) without any window.</summary>
    private async Task SignInSilentlyAsync()
    {
        if (GetApi() is not { } api)
        {
            AccountButton.Visibility = Visibility.Collapsed;
            return;
        }
        AccountButton.Visibility = Visibility.Visible;
        AccountText.Text = "Prijavljam …";
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await api.SignInSilentAsync(timeout.Token);
        UpdateAccountUi();
    }

    private void UpdateAccountUi()
    {
        if (_api is not { } api) return;
        if (api.IsSignedIn)
        {
            AccountText.Text = api.UserName!.Split('@')[0];
            AccountGlyph.Data = (Geometry)FindResource("Icon.User");
            AccountButton.ToolTip = $"Prijavljeni v CommonData kot {api.UserName}. Podatki o izometrijah so iz baze.";
        }
        else
        {
            AccountText.Text = "Prijava";
            AccountGlyph.Data = (Geometry)FindResource("Icon.SignIn");
            AccountButton.ToolTip = api.DisabledReason ??
                "Prijavite se v CommonData, da so podatki o izometrijah iz baze. Brez prijave se uporabi titles.json.";
        }
    }

    private async void OnAccountClick(object sender, RoutedEventArgs e)
    {
        if (_api is not { } api || _importing) return;
        if (api.IsSignedIn)
        {
            var choice = await ShowDialogAsync("COMMONDATA", "Icon.User", false, 420,
                BodyText($"Prijavljeni ste kot {api.UserName}. Projekt, sklop in ime izometrije se berejo iz baze."),
                ("close", "Zapri", "SecondaryButton"), ("switch", "Drug račun", "SecondaryButton"));
            if (choice != "switch") return;
        }

        AccountButton.IsEnabled = false;
        AccountText.Text = "Prijava v brskalniku …";
        try
        {
            await api.SignInInteractiveAsync(CancellationToken.None);
        }
        catch (Microsoft.Identity.Client.MsalClientException ex) when (ex.ErrorCode == "authentication_canceled")
        {
            // Closed the browser: nothing changes.
        }
        catch (Exception ex)
        {
            await ShowDialogAsync("PRIJAVA NI USPELA", "Icon.XCircle", true, 420, BodyText(CommonDataApi.Describe(ex)),
                                  ("ok", "V redu", "SecondaryButton"));
        }
        finally
        {
            AccountButton.IsEnabled = true;
            UpdateAccountUi();
        }

        // The table may have been built from titles.json; build it again from the database.
        if (_plan != null && !_busy) await ScanAsync(_plan.Scan.Root, auto: false);
    }

    // ─── Updates ─────────────────────────────────────────────────────────────

    /// <summary>Asks GitHub at start and whenever the button is pressed. The button stays in
    /// the header either way, so an app left running for days can still be updated.</summary>
    private async Task<bool> CheckForUpdateAsync()
    {
        _update = await Updater.CheckAsync(CancellationToken.None);
        if (_update == null)
        {
            UpdateText.Text = "Posodobitve";
            UpdateButton.ToolTip = Updater.IsDevBuild
                ? "Razvojna različica se ne posodablja."
                : $"Nameščena je v{Updater.CurrentVersion}. Kliknite za preverjanje posodobitev.";
            return false;
        }
        UpdateText.Text = $"Posodobi na {_update.Tag}";
        UpdateButton.ToolTip = $"Nameščena je v{Updater.CurrentVersion}, na voljo je {_update.Tag}.";
        return true;
    }

    private async void OnUpdateClick(object sender, RoutedEventArgs e)
    {
        if (_updating) return;
        if (_update == null)
        {
            // Nothing known yet: look again now, so the button works in an app left running.
            var previous = UpdateText.Text;
            UpdateButton.IsEnabled = false;
            UpdateText.Text = "Preverjam …";
            var found = await CheckForUpdateAsync();
            UpdateButton.IsEnabled = true;
            if (!found)
            {
                UpdateText.Text = previous == "Preverjam …" ? "Posodobitve" : previous;
                await ShowDialogAsync("POSODOBITVE", "Icon.CheckCircle", false, 420,
                    BodyText(Updater.IsDevBuild
                        ? "To je razvojna različica, ki se ne posodablja."
                        : $"Nameščena je najnovejša različica (v{Updater.CurrentVersion})."),
                    ("ok", "V redu", "SecondaryButton"));
                return;
            }
        }
        if (_update is not { } release) return;
        if (_importing)
        {
            await ShowDialogAsync("UVOZ POTEKA", "Icon.WarningCircle", true, 360,
                                  BodyText("Posodobite, ko se uvoz konča."), ("ok", "V redu", "SecondaryButton"));
            return;
        }

        var body = BodyText($"Na voljo je različica {release.Tag} (nameščena je v{Updater.CurrentVersion}). " +
                            "Prenese se v ozadju, nato se aplikacija sama znova zažene.");
        var choice = await ShowDialogAsync("POSODOBITEV", "Icon.DownloadSimple", false, 360, body,
                                           ("cancel", "Prekliči", "SecondaryButton"),
                                           ("update", "Posodobi", "PrimaryButton"));
        if (choice != "update") return;

        _updating = true;
        UpdateButton.IsEnabled = false;
        var progress = new Progress<double>(p => UpdateText.Text = $"Prenašam {p:P0}");
        try
        {
            await Updater.InstallAsync(release, progress, CancellationToken.None);
            // The new copy is starting and waits for this one to close.
            Application.Current.Shutdown();
        }
        catch (Exception ex)
        {
            _updating = false;
            UpdateButton.IsEnabled = true;
            UpdateText.Text = $"Posodobi na {release.Tag}";
            await ShowDialogAsync("POSODOBITEV NI USPELA", "Icon.XCircle", true, 360,
                                  BodyText($"{ex.Message}. Aplikacija ostaja na v{Updater.CurrentVersion}; " +
                                           "novo različico lahko prenesete tudi s strani GitHub."),
                                  ("ok", "V redu", "SecondaryButton"));
        }
    }

    // ─── Reports ─────────────────────────────────────────────────────────────

    /// <summary>A report for one skid: pick the skid and the number, then stamp its photos
    /// into a new folder under the project and fill the Word template with them.</summary>
    private async Task OnNewReportAsync(IReadOnlyList<ReportRequest> skids)
    {
        if (skids.Count == 0)
        {
            await ShowDialogAsync("NI SKLOPOV", "Icon.WarningCircle", true, 460,
                BodyText("V tem projektu še ni uvoženih fotografij, zato ni česa poročati."),
                ("ok", "V redu", "SecondaryButton"));
            return;
        }

        var template = ReportBuilder.FindTemplate(_cfg.DestinationRoot, skids[0].ProjectFolder);
        if (template == null)
        {
            await ShowDialogAsync("PREDLOGE NI", "Icon.WarningCircle", true, 560,
                BodyText("Poročilo nastane iz predloge Word, ki je ni. " +
                         ReportBuilder.TemplateHint(_cfg.DestinationRoot, skids[0].ProjectFolder)),
                ("ok", "V redu", "SecondaryButton"));
            return;
        }

        var photos = await Task.Run(() => skids.ToDictionary(
            s => s.UnitName,
            s => s.Isos.Sum(i => PhotoItem.List(i.Path).Count(p => !p.IsVideo)),
            StringComparer.OrdinalIgnoreCase));

        var skidList = new ListBox { MaxHeight = 190, Margin = new Thickness(0, 4, 0, 12) };
        skidList.SetResourceReference(StyleProperty, "PlainList");
        foreach (var skid in skids)
        {
            var count = photos[skid.UnitName];
            skidList.Items.Add(new ListBoxItem
            {
                Content = $"{skid.UnitName}  ·  {Plural(count, "fotografija", "fotografiji", "fotografije", "fotografij")}",
                Tag = skid,
                IsEnabled = count > 0,
                Padding = new Thickness(8, 6, 8, 6),
            });
        }
        // A skid without photos can't be reported on, so start on one that can.
        skidList.SelectedItem = skidList.Items.Cast<ListBoxItem>().FirstOrDefault(i => i.IsEnabled);

        // One report per skid in one go: numbered on from the number below.
        var withPhotos = skids.Where(s => photos[s.UnitName] > 0).ToList();
        if (withPhotos.Count > 1)
        {
            skidList.Items.Insert(0, new ListBoxItem
            {
                Content = $"Vsi sklopi  ·  {Plural(withPhotos.Count, "poročilo", "poročili", "poročila", "poročil")}, eno za vsak sklop",
                Tag = AllSkids,
                Padding = new Thickness(8, 6, 8, 6),
                FontWeight = FontWeights.SemiBold,
            });
        }
        bool AllPicked() => skidList.SelectedItem is ListBoxItem { Tag: string };

        var labelBox = new TextBox { Margin = new Thickness(0, 4, 0, 10) };
        labelBox.SetResourceReference(StyleProperty, "FieldBox");
        var labelCaption = Muted("Oznaka sklopa v imenu datoteke", "SmallFontSize");

        var numberBox = new TextBox
        {
            Text = ReportBuilder.SuggestNumber(_settings.LastReportNo, DateTime.Today),
            Margin = new Thickness(0, 4, 0, 10),
        };
        numberBox.SetResourceReference(StyleProperty, "FieldBox");

        // The file name is built from three parts; show what it will be as they are typed.
        var nameText = Muted("", "TinyFontSize");
        void ShowName()
        {
            if (AllPicked())
            {
                var series = ReportBuilder.NumberSeries(numberBox.Text.Trim(), withPhotos.Count, DateTime.Today);
                nameText.Text = $"{Plural(withPhotos.Count, "poročilo", "poročili", "poročila", "poročil")}: " +
                                $"{series[0]} … {series[^1]}, vsako z oznako svojega sklopa";
            }
            else
            {
                nameText.Text = ReportBuilder.DocumentName(labelBox.Text.Trim(), numberBox.Text.Trim());
            }
        }
        labelBox.TextChanged += (_, _) => ShowName();
        numberBox.TextChanged += (_, _) => ShowName();
        skidList.SelectionChanged += (_, _) =>
        {
            // For all skids each report takes its own skid's label, so there is nothing to type.
            labelBox.Visibility = labelCaption.Visibility = AllPicked() ? Visibility.Collapsed : Visibility.Visible;
            if (skidList.SelectedItem is ListBoxItem { Tag: ReportRequest picked })
                labelBox.Text = ReportBuilder.ShortSkid(picked.UnitName);
            ShowName();
        };
        if (skidList.SelectedItem is ListBoxItem { Tag: ReportRequest first })
            labelBox.Text = ReportBuilder.ShortSkid(first.UnitName);
        ShowName();

        var form = new StackPanel();
        form.Children.Add(Muted("Sklop (vse njegove fotografije gredo v poročilo)", "SmallFontSize"));
        form.Children.Add(skidList);
        form.Children.Add(labelCaption);
        form.Children.Add(labelBox);
        form.Children.Add(Muted("Številka poročila", "SmallFontSize"));
        form.Children.Add(numberBox);
        form.Children.Add(nameText);
        form.Children.Add(Muted($"Predloga: {template}", "TinyFontSize"));

        var choice = await ShowDialogAsync("NOVO POROČILO", "Icon.FileText", false, 560, form,
                                           ("cancel", "Prekliči", "SecondaryButton"),
                                           ("create", "Ustvari", "PrimaryButton"));
        if (choice != "create") return;
        var number = numberBox.Text.Trim();
        if (number.Length == 0) return;
        if (AllPicked())
        {
            await CreateAllReportsAsync(template, withPhotos, ReportBuilder.NumberSeries(number, withPhotos.Count, DateTime.Today));
            return;
        }
        if (skidList.SelectedItem is not ListBoxItem { Tag: ReportRequest request }) return;
        var skidLabel = labelBox.Text.Trim();

        _ = ShowDialogAsync("USTVARJAM POROČILO", "Icon.FileText", false, 460,
                            BodyText($"Žigosam fotografije in pripravljam dokument za sklop {request.UnitName} …"));
        ReportResult result;
        try
        {
            result = await Task.Run(() => ReportBuilder.CreateAsync(template, request, number, skidLabel, DateTime.Today, CancellationToken.None));
        }
        catch (Exception ex)
        {
            CloseDialog(null);
            await ShowDialogAsync("POROČILA NI MOGOČE USTVARITI", "Icon.XCircle", true, 560, BodyText(ex.Message),
                                  ("ok", "V redu", "SecondaryButton"));
            return;
        }
        CloseDialog(null);

        _settings.LastReportNo = number;
        _settings.Save();

        // Show it straight away, ready for its PDF.
        ReportsPage.PendingFolder = result.Folder;
        await ReportsPage.RefreshAsync();

        var done = new StackPanel();
        done.Children.Add(BodyText(
            $"{Path.GetFileName(result.DocumentPath)} · " +
            $"{Plural(result.Photos, "fotografija", "fotografiji", "fotografije", "fotografij")} · " +
            $"{Plural(result.Isometrije, "izometrija", "izometriji", "izometrije", "izometrij")}."));
        done.Children.Add(Muted(result.Folder, "TinyFontSize"));
        foreach (var problem in result.Problems)
            done.Children.Add(Line("Icon.WarningCircle", "WarningBrush", problem));

        var pick = await ShowDialogAsync("POROČILO USTVARJENO", "Icon.CheckCircle", false, 600, done,
                                         ("close", "Zapri", "SecondaryButton"),
                                         ("folder", "Odpri mapo", "SecondaryButton"),
                                         ("open", "Odpri v Wordu", "PrimaryButton"));
        if (pick == "open") Run(() => Process.Start(new ProcessStartInfo(result.DocumentPath) { UseShellExecute = true }));
        else if (pick == "folder") OpenFolder(new[] { result.Folder });
    }

    /// <summary>Deleting a report throws away the whole folder. That is safe — the photos it
    /// was made from stay on the share — but it is spelled out before anything happens.</summary>
    private async Task OnDeleteReportAsync(ReportEntry report)
    {
        var body = new StackPanel();
        body.Children.Add(BodyText(
            $"Izbrisana bo celotna mapa poročila {report.Number} ({report.UnitName}): dokument Word, " +
            (report.HasPdf ? "PDF " : "") +
            $"in {Plural(report.Photos, "ožigosana fotografija", "ožigosani fotografiji", "ožigosane fotografije", "ožigosanih fotografij")}."));
        body.Children.Add(BodyText("Uvožene fotografije na strežniku ostanejo nedotaknjene, zato lahko poročilo kadar koli naredite znova."));
        body.Children.Add(Muted(report.Folder, "TinyFontSize"));

        var choice = await ShowDialogAsync("IZBRIŠI POROČILO", "Icon.Trash", true, 560, body,
                                           ("cancel", "Prekliči", "SecondaryButton"),
                                           ("delete", "Izbriši", "DangerButton"));
        if (choice != "delete") return;

        // Only now: the viewer holds the PDF open, and an open file can't be deleted.
        ReportsPage.ReleasePdf();
        try
        {
            await ReportIndex.DeleteAsync(report.Folder, CancellationToken.None);
        }
        catch (Exception ex)
        {
            await ShowDialogAsync("BRISANJE NI USPELO", "Icon.XCircle", true, 560,
                                  BodyText($"{report.Folder}: {ex.Message}"), ("ok", "V redu", "SecondaryButton"));
        }
        await ReportsPage.RefreshAsync();
    }

    private const string AllSkids = "*";

    /// <summary>A report for every skid, one after another, then an offer to make their PDFs.
    /// A skid that fails (its number already taken, say) doesn't stop the rest.</summary>
    private async Task CreateAllReportsAsync(string template, IReadOnlyList<ReportRequest> skids, IReadOnlyList<string> numbers)
    {
        var status = BodyText("");
        _ = ShowDialogAsync("USTVARJAM POROČILA", "Icon.FileText", false, 460, status);
        var made = new List<ReportResult>();
        var failures = new List<string>();
        string? lastNumber = null;
        for (var i = 0; i < skids.Count; i++)
        {
            var request = skids[i];
            var number = numbers[i];
            status.Text = $"{i + 1} / {skids.Count} · {number} · {request.UnitName} …";
            try
            {
                made.Add(await Task.Run(() => ReportBuilder.CreateAsync(
                    template, request, number, ReportBuilder.ShortSkid(request.UnitName), DateTime.Today, CancellationToken.None)));
                lastNumber = number;
            }
            catch (Exception ex)
            {
                failures.Add($"{number} · {request.UnitName}: {ex.Message}");
            }
        }
        CloseDialog(null);

        if (lastNumber != null)
        {
            _settings.LastReportNo = lastNumber;
            _settings.Save();
        }
        await ReportsPage.RefreshAsync();

        var done = new StackPanel();
        done.Children.Add(BodyText($"Ustvarjenih poročil: {made.Count} od {skids.Count}. " +
                                   "PDF-je lahko naredite zdaj ali pozneje z gumbom Osveži PDF-je."));
        foreach (var failure in failures) done.Children.Add(Line("Icon.XCircle", "DangerBrush", failure));
        var buttons = made.Count > 0
            ? new[] { ("close", "Zapri", "SecondaryButton"), ("pdf", "Ustvari PDF-je", "PrimaryButton") }
            : new[] { ("close", "Zapri", "SecondaryButton") };
        var pick = await ShowDialogAsync(failures.Count == 0 ? "POROČILA USTVARJENA" : "NEKATERA POROČILA NISO USPELA",
                                         failures.Count == 0 ? "Icon.CheckCircle" : "Icon.WarningCircle",
                                         failures.Count > 0, 560, done, buttons);
        if (pick == "pdf")
            await OnRegenerateAllAsync(made.Select(m => ReportEntry.Create(m.Folder)).OfType<ReportEntry>().ToList());
    }

    /// <summary>Makes the project's PDFs again: those missing or older than their document,
    /// or, when all are current and the user asks for it, every one. One Word does them all.</summary>
    private async Task OnRegenerateAllAsync(IReadOnlyList<ReportEntry> reports)
    {
        var targets = reports.Where(r => r.NeedsPdf).ToList();
        if (targets.Count == 0)
        {
            var again = await ShowDialogAsync("PDF-JI SO AŽURNI", "Icon.CheckCircle", false, 460,
                BodyText($"Vsi PDF-ji tega projekta so novejši od svojih dokumentov ({reports.Count}). Jih vseeno ustvarim znova?"),
                ("cancel", "Prekliči", "SecondaryButton"), ("all", "Ustvari vse znova", "PrimaryButton"));
            if (again != "all") return;
            targets = reports.ToList();
        }

        // Word overwrites the PDFs, so the viewer lets go of the one it shows.
        ReportsPage.ReleasePdf();
        var status = BodyText("");
        _ = ShowDialogAsync("USTVARJAM PDF-JE", "Icon.FileText", false, 460, status);
        var progress = new Progress<int>(i =>
            status.Text = $"{i + 1} / {targets.Count} · {targets[i].Number} ({targets[i].UnitName}) …");
        List<WordExport.PdfResult> results;
        try
        {
            results = await WordExport.ToPdfManyAsync(targets.Select(r => r.DocumentPath).ToList(), progress);
        }
        catch (Exception ex)
        {
            CloseDialog(null);
            await ShowDialogAsync("PDF-JEV NI MOGOČE USTVARITI", "Icon.XCircle", true, 460, BodyText(ex.Message),
                                  ("ok", "V redu", "SecondaryButton"));
            await ReportsPage.RefreshAsync();
            return;
        }
        CloseDialog(null);
        await ReportsPage.RefreshAsync();

        var failed = results.Where(r => r.Error != null).ToList();
        var done = new StackPanel();
        done.Children.Add(BodyText($"Ustvarjenih PDF-jev: {results.Count - failed.Count} od {results.Count}."));
        foreach (var f in failed)
            done.Children.Add(Line("Icon.XCircle", "DangerBrush", $"{Path.GetFileName(f.Document)}: {f.Error}"));
        await ShowDialogAsync(failed.Count == 0 ? "PDF-JI USTVARJENI" : "NEKATERI PDF-JI NISO USPELI",
                              failed.Count == 0 ? "Icon.CheckCircle" : "Icon.WarningCircle", failed.Count > 0, 520, done,
                              ("ok", "V redu", "SecondaryButton"));
    }

    private TextBlock Muted(string text, string fontSizeKey)
    {
        var block = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap };
        block.SetResourceReference(StyleProperty, "Muted");
        block.SetResourceReference(TextBlock.FontSizeProperty, fontSizeKey);
        return block;
    }

    /// <summary>Starting Explorer or Word can fail; that is worth a line, not a crash.</summary>
    private void Run(Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            SetBanner("error", ex.Message);
        }
    }

    // ─── Pages ───────────────────────────────────────────────────────────────

    private async void OnTabChanged(object sender, RoutedEventArgs e)
    {
        // Fires once while InitializeComponent is still building the window.
        if (ImportPage == null || ViewerPage == null || ReportsPage == null) return;

        var viewer = ViewerTab.IsChecked == true;
        var reports = ReportsTab.IsChecked == true;
        ImportPage.Visibility = viewer || reports ? Visibility.Collapsed : Visibility.Visible;
        ViewerPage.Visibility = viewer ? Visibility.Visible : Visibility.Collapsed;
        ReportsPage.Visibility = reports ? Visibility.Visible : Visibility.Collapsed;
        RescanButton.ToolTip = viewer || reports ? "Osveži" : "Preglej znova";

        // Every visit re-reads the share, so work done meanwhile is there.
        if (viewer)
        {
            if (!_viewerReady)
            {
                ViewerPage.Initialize(_cfg, _settings);
                _viewerReady = true;
            }
            await ViewerPage.RefreshAsync();
        }
        else if (reports)
        {
            if (!_reportsReady)
            {
                ReportsPage.Initialize(_cfg, _settings);
                ReportsPage.NewReportRequested += OnNewReportAsync;
                ReportsPage.DeleteRequested += OnDeleteReportAsync;
                ReportsPage.RegenerateAllRequested += OnRegenerateAllAsync;
                _reportsReady = true;
            }
            await ReportsPage.RefreshAsync();
        }
    }

    private void OnWeldRowDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is not DependencyObject source ||
            ItemsControl.ContainerFromElement(WeldGrid, source) is not DataGridRow { Item: WeldPlan weld })
            return;
        ViewerPage.PendingFolder = weld.DestinationFolder;
        ViewerTab.IsChecked = true;
    }

    // ─── Showing the plan ────────────────────────────────────────────────────

    private void ShowPlan(ImportPlan plan, IReadOnlyList<ImportFailure>? failures)
    {
        _plan = plan;
        _rows.Clear();
        foreach (var weld in plan.Welds)
        {
            var reasons = failures?.Where(f => f.BomCode == weld.BomCode && f.WeldLabel == weld.WeldLabel)
                                   .Select(f => $"{Path.GetFileName(f.Source)}: {f.Reason}")
                                   .ToList();
            if (reasons is { Count: > 0 })
            {
                weld.Status = WeldStatus.Failed;
                weld.StatusTip = string.Join(Environment.NewLine, reasons);
            }
            _rows.Add(weld);
        }

        _warnings.Clear();
        foreach (var w in plan.Warnings) _warnings.Add(w);
        WarningsPanel.Visibility = _warnings.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        var parts = new List<string> { SourceLabel(plan.Scan.Root) };
        var projects = plan.Welds.Select(w => w.Resolution.Info?.ProjectFolderName).OfType<string>()
                           .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (projects.Count == 1) parts.Add(projects[0]);
        else if (projects.Count > 1) parts.Add(Plural(projects.Count, "projekt", "projekta", "projekti", "projektov"));
        parts.Add(Plural(plan.PhotoCount, "fotografija", "fotografiji", "fotografije", "fotografij"));
        ContextText.Text = string.Join("  ·  ", parts);

        if (plan.Welds.Count == 0)
        {
            ShowEmpty("NA KARTICI NI FOTOGRAFIJ",
                      $"{SourceLabel(plan.Scan.Root)} ima mape zvarov, a so vse prazne.");
            ContextText.Text = SourceLabel(plan.Scan.Root);
        }
        else
        {
            EmptyState.Visibility = Visibility.Collapsed;
            WeldGrid.Visibility = Visibility.Visible;
        }
        SetIdleFooter();
        ImportButton.IsEnabled = plan.Welds.Count > 0;
    }

    private void ShowEmpty(string title, string text)
    {
        _plan = null;
        _rows.Clear();
        _warnings.Clear();
        WarningsPanel.Visibility = Visibility.Collapsed;
        EmptyTitle.Text = title;
        EmptyText.Text = text;
        EmptyState.Visibility = Visibility.Visible;
        WeldGrid.Visibility = Visibility.Collapsed;
        ContextText.Text = "Čakam na SD kartico";
        ImportButton.IsEnabled = false;
        SetIdleFooter();
    }

    private void SetIdleFooter()
    {
        FooterText.Text = _plan is { Scan.EmptyWeldFolders: > 0 } p
            ? Plural(p.Scan.EmptyWeldFolders, "prazen zvar skrit", "prazna zvara skrita", "prazni zvari skriti", "praznih zvarov skritih")
            : "";
    }

    private void SetScanningUi(bool scanning, string root)
    {
        RescanButton.IsEnabled = !scanning;
        if (scanning)
        {
            ImportButton.IsEnabled = false;
            FooterText.Text = $"Pregledujem {root} …";
        }
        else if (!_importing)
        {
            ImportButton.IsEnabled = _plan is { Welds.Count: > 0 };
            SetIdleFooter();
        }
    }

    // ─── Import ──────────────────────────────────────────────────────────────

    private async void OnImportClick(object sender, RoutedEventArgs e)
    {
        if (_plan == null || _busy) return;
        var plan = _plan;

        var shareProblem = await Task.Run(_cfg.CheckShare);
        SetBanner("share", shareProblem);
        if (shareProblem != null) return;

        _busy = _importing = true;
        _importCts = new CancellationTokenSource();
        SetImportingUi(true);
        ImportResult result;
        try
        {
            var resolver = new BomResolver(_cfg, GetApi());
            var progress = new Progress<ImportProgress>(OnProgress);
            var token = _importCts.Token;
            result = await Task.Run(() => Importer.RunAsync(plan, _cfg, resolver, progress, token));
        }
        catch (Exception ex)
        {
            SetBanner("error", $"Uvoz ni uspel: {ex.Message}");
            return;
        }
        finally
        {
            _busy = _importing = false;
            SetImportingUi(false);
            _importCts.Dispose();
            _importCts = null;
        }

        _lastResult = result;
        _lastResultRoot = plan.Scan.Root;

        // Refresh the table so statuses show what is at the destination now.
        if (!Directory.Exists(plan.Scan.Root) || !await ScanAsync(plan.Scan.Root, auto: false, result.Failures))
            ShowEmpty("VSTAVITE SD KARTICO", "Aplikacija jo prepozna samodejno.");
        ClearCardButton.Visibility = CanClearCard() ? Visibility.Visible : Visibility.Collapsed;

        await ShowSummaryAsync(result);
    }

    private void OnProgress(ImportProgress p)
    {
        ImportProgressBar.Maximum = Math.Max(1, p.BytesTotal);
        ImportProgressBar.Value = p.BytesDone;
        var current = p.CurrentName == "" ? "" : $" · {p.CurrentName}";
        FooterText.Text = $"Uvažam {p.FilesDone} / {p.FilesTotal}{current} · {Megabytes(p.BytesDone)} / {Megabytes(p.BytesTotal)}";
    }

    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        _importCts?.Cancel();
        CancelButton.IsEnabled = false;
        FooterText.Text = "Preklicujem …";
    }

    private void SetImportingUi(bool importing)
    {
        ImportProgressBar.Value = 0;
        ImportProgressBar.Visibility = importing ? Visibility.Visible : Visibility.Collapsed;
        CancelButton.Visibility = importing ? Visibility.Visible : Visibility.Collapsed;
        CancelButton.IsEnabled = true;
        ImportButton.IsEnabled = !importing && _plan is { Welds.Count: > 0 };
        RescanButton.IsEnabled = !importing;
        if (importing) ClearCardButton.Visibility = Visibility.Collapsed;
        WeldGrid.IsEnabled = !importing;
    }

    private bool CanClearCard() =>
        _lastResult is { FullyVerified: true, Verified.Count: > 0 } &&
        _lastResultRoot != null && Directory.Exists(_lastResultRoot);

    // ─── Summary, clear card, open folder ────────────────────────────────────

    private async Task ShowSummaryAsync(ImportResult r)
    {
        var (title, glyph, danger) =
            r.CardRemoved ? ("KARTICA ODSTRANJENA", "Icon.XCircle", true)
            : r.Cancelled ? ("UVOZ PREKLICAN", "Icon.XCircle", true)
            : r.Failures.Count > 0 ? ("UVOZ Z NAPAKAMI", "Icon.WarningCircle", true)
            : ("UVOZ KONČAN", "Icon.CheckCircle", false);

        var buttons = new List<(string, string, string)>();
        if (r.DestinationFolders.Count > 0) buttons.Add(("open", "Odpri mapo", "SecondaryButton"));
        buttons.Add(("close", "Zapri", "SecondaryButton"));
        if (CanClearCard()) buttons.Add(("clear", "Počisti kartico", "DangerButton"));

        var choice = await ShowDialogAsync(title, glyph, danger, 520, BuildSummary(r), buttons.ToArray());
        if (choice == "open") OpenFolder(r.DestinationFolders);
        else if (choice == "clear") await ClearCardAsync();
    }

    private UIElement BuildSummary(ImportResult r)
    {
        var panel = new StackPanel();

        var stats = new UniformGrid { Columns = 4, Margin = new Thickness(0, 0, 0, 12) };
        stats.Children.Add(Stat(r.Copied, "Kopirano", r.Copied > 0 ? "SuccessBrush" : "TextMutedBrush"));
        stats.Children.Add(Stat(r.Duplicates, "Že uvoženo", "TextMutedBrush"));
        stats.Children.Add(Stat(r.UnresolvedCopied, "Nerazvrščeno", r.UnresolvedCopied > 0 ? "WarningBrush" : "TextMutedBrush"));
        stats.Children.Add(Stat(r.Failures.Count, "Napake", r.Failures.Count > 0 ? "DangerBrush" : "TextMutedBrush"));
        panel.Children.Add(stats);

        var lines = new StackPanel();
        if (r.MovedFromUnresolved > 0)
            lines.Children.Add(Line("Icon.CheckCircle", "SuccessBrush",
                $"Iz {DestinationTree.UnresolvedFolderName} na pravo mesto premaknjenih: {r.MovedFromUnresolved}."));
        if (r.UnresolvedBoms.Count > 0)
            lines.Children.Add(Line("Icon.WarningCircle", "WarningBrush",
                $"Nerazvrščene izometrije, shranjene v {DestinationTree.UnresolvedFolderName}: " +
                $"{string.Join(", ", r.UnresolvedBoms)}. Ob naslednjem uvozu se premaknejo na pravo mesto, ko bodo znane."));
        foreach (var note in r.Notes)
            lines.Children.Add(Line("Icon.WarningCircle", "WarningBrush", note));
        foreach (var f in r.Failures)
            lines.Children.Add(Line("Icon.XCircle", "DangerBrush",
                $"{f.BomCode}-{f.WeldLabel} · {Path.GetFileName(f.Source)}: {f.Reason}", f.Source));
        if (lines.Children.Count > 0)
            panel.Children.Add(new ScrollViewer { Content = lines, MaxHeight = 240, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });

        if (r.LogPath != null)
        {
            var log = new TextBlock { Text = $"Dnevnik: {r.LogPath}", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) };
            log.SetResourceReference(StyleProperty, "Muted");
            log.SetResourceReference(TextBlock.FontSizeProperty, "TinyFontSize");
            panel.Children.Add(log);
        }
        return panel;
    }

    private async void OnClearCardClick(object sender, RoutedEventArgs e) => await ClearCardAsync();

    private async Task ClearCardAsync()
    {
        if (!CanClearCard() || _lastResult == null || _lastResultRoot == null) return;
        var result = _lastResult;
        var root = _lastResultRoot;

        var body = BodyText($"Z {SourceLabel(root).ToLowerInvariant()} bodo izbrisane uvožene in preverjene fotografije " +
                            $"({result.Verified.Count}). Mape ostanejo, da je kartica pripravljena za naslednje delo.");
        var choice = await ShowDialogAsync("POČISTI KARTICO", "Icon.Trash", true, 360, body,
                                           ("cancel", "Prekliči", "SecondaryButton"),
                                           ("clear", "Počisti kartico", "DangerButton"));
        if (choice != "clear") return;

        var outcome = await Task.Run(() => CardCleaner.Clear(result.Verified));
        _lastResult = null;
        ClearCardButton.Visibility = Visibility.Collapsed;

        var done = new StackPanel();
        done.Children.Add(BodyText($"Izbrisanih fotografij: {outcome.Deleted}."));
        foreach (var p in outcome.Problems) done.Children.Add(Line("Icon.WarningCircle", "WarningBrush", p));
        await ShowDialogAsync("KARTICA POČIŠČENA", "Icon.CheckCircle", false, 360, done, ("ok", "V redu", "SecondaryButton"));

        if (Directory.Exists(root)) await ScanAsync(root, auto: false);
    }

    private void OnOpenFolderClick(object sender, RoutedEventArgs e)
    {
        if (_lastResult is { DestinationFolders.Count: > 0 } r) OpenFolder(r.DestinationFolders);
        else if (WeldGrid.SelectedItem is WeldPlan w && Directory.Exists(w.DestinationFolder)) OpenFolder(new[] { w.DestinationFolder });
        else OpenFolder(Array.Empty<string>());
    }

    /// <summary>The folder itself when there is one, else their deepest common parent.</summary>
    private void OpenFolder(IEnumerable<string> folders)
    {
        var list = folders.Where(Directory.Exists).ToList();
        var target = list.Count switch
        {
            0 => _cfg.DestinationRoot,
            1 => list[0],
            _ => CommonParent(list) ?? _cfg.DestinationRoot,
        };
        if (!Directory.Exists(target))
        {
            SetBanner("error", $"Mapa {target} ni dosegljiva.");
            return;
        }
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{target}\"") { UseShellExecute = true });
    }

    private static string? CommonParent(List<string> paths)
    {
        var split = paths.Select(p => p.TrimEnd('\\').Split('\\')).ToList();
        var common = new List<string>();
        for (var i = 0; i < split.Min(s => s.Length); i++)
        {
            var part = split[0][i];
            if (!split.All(s => string.Equals(s[i], part, StringComparison.OrdinalIgnoreCase))) break;
            common.Add(part);
        }
        return common.Count == 0 ? null : string.Join('\\', common) + (common.Count == 1 ? "\\" : "");
    }

    // ─── Dialogs ─────────────────────────────────────────────────────────────

    private Task<string?> ShowDialogAsync(string title, string glyphKey, bool danger, double width, UIElement body,
                                          params (string Id, string Text, string Style)[] buttons)
    {
        _dialog?.TrySetResult(null);
        var tcs = new TaskCompletionSource<string?>();
        _dialog = tcs;

        DialogTitle.Text = title;
        DialogTitle.SetResourceReference(TextBlock.ForegroundProperty, danger ? "DangerBrush" : "AccentBrush");
        DialogGlyph.Data = (Geometry)FindResource(glyphKey);
        DialogGlyph.SetResourceReference(ForegroundProperty, danger ? "DangerBrush" : "AccentBrush");
        DialogCard.Width = width;
        DialogBody.Content = body;

        DialogButtons.Children.Clear();
        Button? last = null;
        foreach (var (id, text, style) in buttons)
        {
            var button = new Button { Content = text, Margin = new Thickness(8, 0, 0, 0) };
            button.SetResourceReference(StyleProperty, style);
            button.Click += (_, _) => CloseDialog(id);
            DialogButtons.Children.Add(button);
            last = button;
        }

        ReportsPage?.SuspendPdf(true);
        DialogOverlay.Visibility = Visibility.Visible;
        if (SystemParameters.ClientAreaAnimation)
        {
            var duration = new Duration(TimeSpan.FromMilliseconds(180));
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            DialogOverlay.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, duration));
            DialogScale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.96, 1, duration) { EasingFunction = ease });
            DialogScale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.96, 1, duration) { EasingFunction = ease });
        }
        last?.Focus();
        return tcs.Task;
    }

    private void CloseDialog(string? id)
    {
        DialogOverlay.Visibility = Visibility.Collapsed;
        ReportsPage?.SuspendPdf(false);
        DialogBody.Content = null;
        var tcs = _dialog;
        _dialog = null;
        tcs?.TrySetResult(id);
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && DialogOverlay.Visibility == Visibility.Visible)
        {
            CloseDialog(null);
            e.Handled = true;
        }
    }

    private FrameworkElement Stat(int value, string label, string brushKey)
    {
        var panel = new StackPanel();
        var number = new TextBlock { Text = value.ToString(), FontSize = 30 };
        number.SetResourceReference(StyleProperty, "Heading");
        number.SetResourceReference(TextBlock.ForegroundProperty, brushKey);
        number.FontSize = 30;
        var caption = new TextBlock { Text = label };
        caption.SetResourceReference(StyleProperty, "Muted");
        panel.Children.Add(number);
        panel.Children.Add(caption);
        return panel;
    }

    private FrameworkElement Line(string glyphKey, string brushKey, string text, string? tooltip = null)
    {
        var glyph = new Glyph
        {
            Data = (Geometry)FindResource(glyphKey),
            Width = 14,
            Height = 14,
            Margin = new Thickness(0, 2, 8, 0),
            VerticalAlignment = VerticalAlignment.Top,
        };
        glyph.SetResourceReference(ForegroundProperty, brushKey);
        var line = new DockPanel { Margin = new Thickness(0, 0, 0, 6), ToolTip = tooltip };
        DockPanel.SetDock(glyph, Dock.Left);
        line.Children.Add(glyph);
        line.Children.Add(new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, FontSize = 12 });
        return line;
    }

    private static TextBlock BodyText(string text) => new() { Text = text, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 6) };

    // ─── Banner, theme, window ───────────────────────────────────────────────

    private void SetBanner(string key, string? message)
    {
        if (message == null) _banner.Remove(key);
        else _banner[key] = message;
        UpdateBanner();
    }

    private void UpdateBanner()
    {
        BannerText.Text = string.Join(Environment.NewLine, _banner.Values);
        Banner.Visibility = _banner.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (_banner.ContainsKey("share") && _plan == null)
            ShowEmptyStateOnly("U: NI DOSEGLJIV", "Ko bo omrežni pogon spet na voljo, pritisnite Preglej znova.");
    }

    private void ShowEmptyStateOnly(string title, string text)
    {
        EmptyTitle.Text = title;
        EmptyText.Text = text;
        EmptyState.Visibility = Visibility.Visible;
        WeldGrid.Visibility = Visibility.Collapsed;
    }

    private void OnThemeClick(object sender, RoutedEventArgs e)
    {
        App.ApplyTheme(!App.IsDark);
        _settings.Theme = App.IsDark ? "dark" : "light";
        _settings.Save();
        UpdateThemeGlyph();
    }

    private void UpdateThemeGlyph() =>
        ThemeGlyph.Data = (Geometry)FindResource(App.IsDark ? "Icon.Sun" : "Icon.Moon");

    public void BringToFront()
    {
        if (WindowState == WindowState.Minimized) WindowState = _settings.Maximized ? WindowState.Maximized : WindowState.Normal;
        Show();
        Activate();
        Topmost = true;
        Topmost = false;
        Focus();
    }

    private void RestorePlacement()
    {
        if (_settings is { Left: { } left, Top: { } top, Width: > 0, Height: > 0 })
        {
            var bounds = new Rect(left, top, _settings.Width.Value, _settings.Height.Value);
            var screen = new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
                                  SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
            if (screen.IntersectsWith(bounds))
            {
                WindowStartupLocation = WindowStartupLocation.Manual;
                Left = left;
                Top = top;
                Width = Math.Max(MinWidth, bounds.Width);
                Height = Math.Max(MinHeight, bounds.Height);
            }
        }
        if (_settings.Maximized) WindowState = WindowState.Maximized;
    }

    private async void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_importing)
        {
            e.Cancel = true;
            await ShowDialogAsync("UVOZ POTEKA", "Icon.WarningCircle", true, 360,
                                  BodyText("Pred zapiranjem uvoz prekličite ali počakajte, da se konča."),
                                  ("ok", "V redu", "SecondaryButton"));
            return;
        }

        var bounds = WindowState == WindowState.Normal ? new Rect(Left, Top, Width, Height) : RestoreBounds;
        _settings.Left = bounds.Left;
        _settings.Top = bounds.Top;
        _settings.Width = bounds.Width;
        _settings.Height = bounds.Height;
        _settings.Maximized = WindowState == WindowState.Maximized;
        _settings.Save();
        _watcher?.Dispose();
        _api?.Dispose();
    }

    // ─── Text helpers ────────────────────────────────────────────────────────

    private static string SourceLabel(string root) =>
        Path.GetPathRoot(root) is { } r && string.Equals(r, root, StringComparison.OrdinalIgnoreCase)
            ? $"SD kartica {root}"
            : $"Mapa {root}";

    /// <summary>Slovenian singular / dual / plural (3-4) / plural (5+) by the last two digits.</summary>
    internal static string Plural(int n, string one, string two, string few, string many)
    {
        var m = Math.Abs(n) % 100;
        var word = m == 1 ? one : m == 2 ? two : m is 3 or 4 ? few : many;
        return $"{n} {word}";
    }

    private static string Megabytes(long bytes) => $"{bytes / 1048576.0:0.0} MB";
}
