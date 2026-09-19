using System.Collections.Specialized;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;

namespace IMPWeldPhotos;

/// <summary>
/// Pregled: browse the photos already on the share. A project picker (remembered between runs)
/// and that project's isometrije by unit on the left, their photos grouped by weld in the
/// middle, a large preview with details and actions on the right.
/// </summary>
public partial class ViewerView : UserControl
{
    private const string PickTitle = "IZBERITE IZOMETRIJO";
    private const string PickText = "Na levi izberite izometrijo ali jo poiščite po kodi, imenu ali sklopu.";

    private AppConfig? _cfg;
    private UserSettings? _settings;
    private List<IsoEntry> _allIsos = new();
    private string? _problem;
    private string? _project;
    private ListCollectionView? _isoView;
    private ListCollectionView? _photoView;
    private CancellationTokenSource? _isoCts;
    private CancellationTokenSource? _previewCts;
    private IsoEntry? _currentIso;
    private string[] _tokens = Array.Empty<string>();
    private bool _refreshing;
    private bool _forceReload;
    private bool _settingSource;
    private bool _applyingExpansion;

    /// <summary>Units the user has opened ("project|unit"). Units start closed; while searching,
    /// every unit with a match is open instead.</summary>
    private readonly HashSet<string> _openUnits = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Folder to select on the next refresh, when jumping here from the import table.</summary>
    public string? PendingFolder { get; set; }

    public ViewerView()
    {
        InitializeComponent();
        ProjectBox.Picked += OnProjectPicked;
        ShowCenterMessage(PickTitle, PickText);
    }

    public void Initialize(AppConfig cfg, UserSettings settings)
    {
        _cfg = cfg;
        _settings = settings;
    }

    // ─── Projects and isometrije ─────────────────────────────────────────────

    /// <summary>Re-reads the share, keeping (and reloading) the selected isometrija, so photos
    /// imported meanwhile show up.</summary>
    public async Task RefreshAsync()
    {
        if (_cfg == null || _refreshing) return;
        _refreshing = true;
        IsoCountText.Text = "Nalagam …";
        try
        {
            var root = _cfg.DestinationRoot;
            (_allIsos, _problem) = await Task.Run(() => ServerIndex.Load(root));

            var jumped = PendingFolder != null;
            var want = PendingFolder ?? _currentIso?.Path;
            PendingFolder = null;
            if (jumped)
            {
                _tokens = Array.Empty<string>();
                SearchBox.Text = "";
            }
            var match = want == null
                ? null
                : _allIsos.FirstOrDefault(i => string.Equals(i.Path, want, StringComparison.OrdinalIgnoreCase));

            // Project: the one being jumped to, else the current one, else the remembered one
            // (only while it still exists), else the only one there is.
            var projects = ProjectEntry.FromIsos(_allIsos);
            bool Exists(string? folder) => folder != null &&
                projects.Any(p => string.Equals(p.FolderName, folder, StringComparison.OrdinalIgnoreCase));
            _project = match?.ProjectName
                ?? (Exists(_project) ? _project : null)
                ?? (Exists(_settings?.ViewerProject) ? _settings!.ViewerProject : null)
                ?? (projects.Count == 1 ? projects[0].FolderName : null);
            if (jumped && match != null) RememberProject();
            if (match != null) _openUnits.Add(UnitKey(match.ProjectName, match.UnitName));
            ProjectBox.SetProjects(projects, _project);

            var view = new ListCollectionView(_allIsos);
            view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(IsoEntry.UnitName)));
            view.Filter = o => o is IsoEntry iso &&
                               string.Equals(iso.ProjectName, _project, StringComparison.OrdinalIgnoreCase) &&
                               _tokens.All(t => iso.SearchText.Contains(t));
            _isoView = view;
            // Swapping the source must not count as the user picking a row: that started a
            // load which the code below then cleared again.
            _settingSource = true;
            IsoList.ItemsSource = view;
            IsoList.SelectedItem = null;
            _settingSource = false;
            UpdateListState();

            if (match != null && string.Equals(match.ProjectName, _project, StringComparison.OrdinalIgnoreCase))
            {
                _forceReload = true;
                IsoList.SelectedItem = match;
                _forceReload = false;
                IsoList.ScrollIntoView(match);
            }
            else
            {
                _currentIso = null;
                ClearPhotos();
                if (jumped) ShowCenterMessage("MAPA ŠE NE OBSTAJA", "Fotografije te izometrije še niso uvožene.");
                else ShowCenterMessage(PickTitle, PickText);
            }
        }
        finally
        {
            _refreshing = false;
        }
    }

    private void OnProjectPicked(ProjectEntry project)
    {
        if (string.Equals(project.FolderName, _project, StringComparison.OrdinalIgnoreCase)) return;
        _project = project.FolderName;
        RememberProject();

        _tokens = Array.Empty<string>();
        SearchBox.Text = "";
        _isoView?.Refresh();
        UpdateListState();

        if (_currentIso != null && !string.Equals(_currentIso.ProjectName, _project, StringComparison.OrdinalIgnoreCase))
        {
            _currentIso = null;
            _settingSource = true;
            IsoList.SelectedItem = null;
            _settingSource = false;
            ClearPhotos();
            ShowCenterMessage(PickTitle, PickText);
        }
    }

    /// <summary>People stay on one project for weeks, so it's kept in settings.json.</summary>
    private void RememberProject()
    {
        if (_settings == null || _project == null) return;
        _settings.ViewerProject = _project;
        _settings.Save();
    }

    private void OnSearchChanged(object sender, TextChangedEventArgs e)
    {
        _tokens = SearchBox.Text.ToLowerInvariant().Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
        _isoView?.Refresh();
        UpdateListState();
    }

    private void UpdateListState()
    {
        var inProject = _project == null
            ? 0
            : _allIsos.Count(i => string.Equals(i.ProjectName, _project, StringComparison.OrdinalIgnoreCase));
        var shown = _isoView?.Count ?? 0;

        var total = MainWindow.Plural(inProject, "izometrija", "izometriji", "izometrije", "izometrij");
        IsoCountText.Text = _project == null ? "" : _tokens.Length == 0 ? total : $"{shown} od {total}";

        string? message =
            _problem ?? (_allIsos.Count == 0 ? "V 140_Zvari še ni uvoženih fotografij."
            : _project == null ? "Zgoraj izberite projekt."
            : shown == 0 ? "Nobena izometrija se ne ujema."
            : null);
        IsoEmptyText.Text = message ?? "";
        IsoEmptyText.Visibility = message == null ? Visibility.Collapsed : Visibility.Visible;
    }

    // Group containers are rebuilt on every refresh, search and scroll, so each one takes its
    // open/closed state from _openUnits when it appears.
    private void OnUnitExpanderLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is not Expander { DataContext: CollectionViewGroup group } expander) return;
        _applyingExpansion = true;
        expander.IsExpanded = _tokens.Length > 0 || _openUnits.Contains(UnitKey(_project, group.Name?.ToString()));
        _applyingExpansion = false;
    }

    private void OnUnitExpanded(object sender, RoutedEventArgs e) => RememberUnit(sender, open: true);

    private void OnUnitCollapsed(object sender, RoutedEventArgs e) => RememberUnit(sender, open: false);

    private void RememberUnit(object sender, bool open)
    {
        if (_applyingExpansion || _tokens.Length > 0) return;
        if (sender is not Expander { DataContext: CollectionViewGroup group }) return;
        var key = UnitKey(_project, group.Name?.ToString());
        if (open) _openUnits.Add(key);
        else _openUnits.Remove(key);
    }

    private static string UnitKey(string? project, string? unit) => $"{project}|{unit}";

    private async void OnIsoSelected(object sender, SelectionChangedEventArgs e)
    {
        // Null when a search hides the selected row: keep showing its photos.
        if (_settingSource || IsoList.SelectedItem is not IsoEntry iso) return;
        _openUnits.Add(UnitKey(iso.ProjectName, iso.UnitName));
        if (!_forceReload && string.Equals(iso.Path, _currentIso?.Path, StringComparison.OrdinalIgnoreCase)) return;
        await LoadIsoAsync(iso);
    }

    private async Task LoadIsoAsync(IsoEntry iso)
    {
        _isoCts?.Cancel();
        var cts = new CancellationTokenSource();
        _isoCts = cts;
        var ct = cts.Token;

        var samePlace = string.Equals(iso.Path, _currentIso?.Path, StringComparison.OrdinalIgnoreCase);
        var keepPhoto = samePlace ? (PhotoList.SelectedItem as PhotoItem)?.Path : null;
        _currentIso = iso;

        IsoTitle.Text = iso.FolderName;
        IsoSubtitle.Text = iso.UnitName;
        IsoHeader.Visibility = Visibility.Visible;
        PhotoList.ItemsSource = null;
        _photoView = null;
        ClearPreview();
        ShowCenterMessage("NALAGAM …", "");

        List<PhotoItem> items;
        try
        {
            items = await Task.Run(() => PhotoItem.List(iso.Path));
        }
        catch (Exception ex)
        {
            if (!ct.IsCancellationRequested) ShowCenterMessage("MAPA NI DOSEGLJIVA", ex.Message);
            return;
        }
        if (ct.IsCancellationRequested) return;

        var count = MainWindow.Plural(items.Count, "fotografija", "fotografiji", "fotografije", "fotografij");
        IsoSubtitle.Text = $"{iso.UnitName}  ·  {count}";
        if (items.Count == 0)
        {
            ShowCenterMessage("V MAPI NI FOTOGRAFIJ", "Ko bodo uvožene, se prikažejo tukaj.");
            return;
        }

        var view = new ListCollectionView(items);
        view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(PhotoItem.WeldLabel)));
        _photoView = view;
        PhotoList.ItemsSource = view;
        CenterEmpty.Visibility = Visibility.Collapsed;
        PhotoList.Visibility = Visibility.Visible;
        PhotoList.SelectedItem = items.FirstOrDefault(p => string.Equals(p.Path, keepPhoto, StringComparison.OrdinalIgnoreCase))
                                 ?? items[0];

        // Thumbnails one at a time, so a slow share fills the grid progressively.
        foreach (var item in items)
        {
            if (item.IsVideo) continue;
            var thumb = await Task.Run(() => ImageLoader.TryLoad(item.Path, 360));
            if (ct.IsCancellationRequested) return;
            item.Thumbnail = thumb;
        }
    }

    private void ClearPhotos()
    {
        _isoCts?.Cancel();
        PhotoList.ItemsSource = null;
        _photoView = null;
        IsoHeader.Visibility = Visibility.Collapsed;
        ClearPreview();
    }

    private void ShowCenterMessage(string title, string text)
    {
        PhotoList.Visibility = Visibility.Collapsed;
        CenterEmpty.Visibility = Visibility.Visible;
        CenterEmptyTitle.Text = title;
        CenterEmptyText.Text = text;
    }

    // ─── Preview ─────────────────────────────────────────────────────────────

    private PhotoItem? Current => PhotoList.SelectedItem as PhotoItem;

    private async void OnPhotoSelected(object sender, SelectionChangedEventArgs e)
    {
        if (Current is not { } item)
        {
            ClearPreview();
            return;
        }
        PhotoList.ScrollIntoView(item);
        await ShowPreviewAsync(item);
    }

    private async Task ShowPreviewAsync(PhotoItem item)
    {
        _previewCts?.Cancel();
        var cts = new CancellationTokenSource();
        _previewCts = cts;

        ActionStatus.Visibility = Visibility.Collapsed;
        DetailsPanel.Visibility = Visibility.Visible;
        DetailName.Text = item.Name;
        DetailName.ToolTip = item.Path;
        DetailWeld.Text = item.WeldLabel;
        DetailFile.Text = $"{item.SizeText} · spremenjena {item.Modified:dd.MM.yyyy HH:mm}";
        UpdateNavButtons();

        if (item.IsVideo)
        {
            PreviewImage.Source = null;
            PreviewVideoGlyph.Visibility = Visibility.Visible;
            PreviewHint.Text = "Video: dvoklik ali Odpri ga predvaja.";
            PreviewHint.Visibility = Visibility.Visible;
            DetailTaken.Text = "—";
            DetailSize.Text = "—";
            return;
        }

        PreviewVideoGlyph.Visibility = Visibility.Collapsed;
        PreviewImage.Source = item.Thumbnail;
        PreviewHint.Text = "Nalagam …";
        PreviewHint.Visibility = Visibility.Visible;
        DetailTaken.Text = "…";
        DetailSize.Text = "…";

        var preview = await Task.Run(() => ImageLoader.LoadPreview(item.Path));
        if (cts.IsCancellationRequested) return;
        if (preview == null)
        {
            PreviewImage.Source = null;
            PreviewHint.Text = "Fotografije ni mogoče prebrati.";
            DetailTaken.Text = "—";
            DetailSize.Text = "—";
            return;
        }
        PreviewImage.Source = preview.Image;
        PreviewHint.Visibility = Visibility.Collapsed;
        DetailTaken.Text = preview.Taken?.ToString("dd.MM.yyyy HH:mm:ss") ?? "—";
        DetailSize.Text = preview.Width > 0 ? $"{preview.Width} × {preview.Height} px" : "—";
    }

    private void ClearPreview()
    {
        _previewCts?.Cancel();
        PreviewImage.Source = null;
        PreviewVideoGlyph.Visibility = Visibility.Collapsed;
        PreviewHint.Text = "Izberite fotografijo";
        PreviewHint.Visibility = Visibility.Visible;
        DetailsPanel.Visibility = Visibility.Collapsed;
        PrevButton.IsEnabled = false;
        NextButton.IsEnabled = false;
    }

    private void UpdateNavButtons()
    {
        var count = _photoView?.Count ?? 0;
        PrevButton.IsEnabled = PhotoList.SelectedIndex > 0;
        NextButton.IsEnabled = PhotoList.SelectedIndex >= 0 && PhotoList.SelectedIndex < count - 1;
    }

    private void Step(int delta)
    {
        var count = _photoView?.Count ?? 0;
        if (count == 0) return;
        PhotoList.SelectedIndex = Math.Clamp(PhotoList.SelectedIndex + delta, 0, count - 1);
    }

    private void OnPrevClick(object sender, RoutedEventArgs e) => Step(-1);

    private void OnNextClick(object sender, RoutedEventArgs e) => Step(1);

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key is not (Key.Left or Key.Right)) return;
        if (e.OriginalSource is TextBox) return;
        if (PhotoList.IsKeyboardFocusWithin) return; // the list moves its own selection
        Step(e.Key == Key.Left ? -1 : 1);
        e.Handled = true;
    }

    // ─── Actions ─────────────────────────────────────────────────────────────

    private void OnPhotoDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject source &&
            ItemsControl.ContainerFromElement(PhotoList, source) is ListBoxItem &&
            Current is { } item)
            Open(item.Path);
    }

    private void OnPreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2 && Current is { } item) Open(item.Path);
    }

    private void OnOpenClick(object sender, RoutedEventArgs e)
    {
        if (Current is { } item) Open(item.Path);
    }

    private void OnShowInFolderClick(object sender, RoutedEventArgs e)
    {
        if (Current is not { } item) return;
        Run(() => Process.Start("explorer.exe", $"/select,\"{item.Path}\""));
    }

    private void OnCopyClick(object sender, RoutedEventArgs e)
    {
        if (Current is not { } item) return;
        Run(() =>
        {
            Clipboard.SetFileDropList(new StringCollection { item.Path });
            Status("Kopirano. Prilepite jo v e-pošto, mapo ali poročilo.");
        });
    }

    private void OnOpenIsoFolderClick(object sender, RoutedEventArgs e)
    {
        if (_currentIso is not { } iso) return;
        Run(() => Process.Start(new ProcessStartInfo("explorer.exe", $"\"{iso.Path}\"") { UseShellExecute = true }));
    }

    private void Open(string path) =>
        Run(() => Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }));

    private void Run(Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            Status($"Ni uspelo: {ex.Message}");
        }
    }

    private void Status(string text)
    {
        ActionStatus.Text = text;
        ActionStatus.Visibility = Visibility.Visible;
    }
}
