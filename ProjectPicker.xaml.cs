using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace IMPWeldPhotos;

/// <summary>
/// Typeable combobox, copied from Kosovnice's Combobox.tsx: focus opens the list and shows the
/// query instead of the selection, ↑/↓ move the highlight (back to the top when the list
/// changes), Enter or a click picks, Escape or a click elsewhere closes, clears the query and
/// blurs. Matching is ProjectEntry.Matches.
/// </summary>
public partial class ProjectPicker : UserControl
{
    private List<ProjectEntry> _all = new();
    private List<ProjectEntry> _filtered = new();
    private ProjectEntry? _selected;
    private bool _open;
    private bool _settingText;
    private Window? _window;
    private Point _lastPointer;

    public event Action<ProjectEntry>? Picked;

    public ProjectPicker()
    {
        InitializeComponent();
    }

    /// <summary>selectedFolder is ignored when that project is no longer in the list.</summary>
    public void SetProjects(List<ProjectEntry> projects, string? selectedFolder)
    {
        _all = projects;
        _selected = projects.FirstOrDefault(p => string.Equals(p.FolderName, selectedFolder, StringComparison.OrdinalIgnoreCase));
        if (_open) Filter();
        else ShowSelected();
    }

    private void ShowSelected()
    {
        _settingText = true;
        Input.Text = _selected?.Name ?? "";
        _settingText = false;
        Input.ToolTip = _selected?.FolderName;
    }

    private void Open(bool clearQuery)
    {
        if (_open) return;
        _open = true;
        if (clearQuery)
        {
            _settingText = true;
            Input.Text = "";
            _settingText = false;
        }
        Filter();
        Dropdown.IsOpen = true;

        _window = Window.GetWindow(this);
        if (_window != null)
        {
            _window.PreviewMouseDown += OnWindowMouseDown;
            _window.Deactivated += OnWindowChanged;
            _window.LocationChanged += OnWindowChanged;
        }
    }

    private void Close()
    {
        if (!_open) return;
        _open = false;
        Dropdown.IsOpen = false;
        if (_window != null)
        {
            _window.PreviewMouseDown -= OnWindowMouseDown;
            _window.Deactivated -= OnWindowChanged;
            _window.LocationChanged -= OnWindowChanged;
            _window = null;
        }
        ShowSelected();
        if (Input.IsKeyboardFocusWithin)
        {
            FocusManager.SetFocusedElement(FocusManager.GetFocusScope(Input), null);
            Keyboard.ClearFocus();
        }
    }

    private void Choose(ProjectEntry project)
    {
        _selected = project;
        Close();
        Picked?.Invoke(project);
    }

    private void Filter()
    {
        _filtered = _all.Where(p => p.Matches(Input.Text)).ToList();
        foreach (var p in _all) p.IsCurrent = ReferenceEquals(p, _selected);
        Options.ItemsSource = _filtered;
        NoMatch.Visibility = _filtered.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        Options.Visibility = _filtered.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        Highlight(0);
    }

    private void Highlight(int index)
    {
        if (_filtered.Count == 0) return;
        Options.SelectedIndex = Math.Clamp(index, 0, _filtered.Count - 1);
        Options.ScrollIntoView(Options.SelectedItem);
    }

    // ─── Input ───────────────────────────────────────────────────────────────

    private void OnInputFocused(object sender, KeyboardFocusChangedEventArgs e) => Open(clearQuery: true);

    // Focus doesn't change when the already-focused input is clicked, so open from the click too.
    private void OnInputMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (Input.IsKeyboardFocused) Open(clearQuery: true);
    }

    private void OnInputTextChanged(object sender, TextChangedEventArgs e)
    {
        if (_settingText) return;
        if (!_open) Open(clearQuery: false);
        else Filter();
    }

    private void OnInputKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Down:
                if (!_open) Open(clearQuery: true);
                else Highlight(Options.SelectedIndex + 1);
                e.Handled = true;
                break;
            case Key.Up:
                Highlight(Options.SelectedIndex - 1);
                e.Handled = true;
                break;
            case Key.Enter:
                if (_open && Options.SelectedItem is ProjectEntry highlighted) Choose(highlighted);
                e.Handled = true;
                break;
            case Key.Escape:
                Close();
                e.Handled = true;
                break;
            case Key.Tab:
                Close();
                break;
        }
    }

    // ─── Options ─────────────────────────────────────────────────────────────

    private void OnOptionsMouseMove(object sender, MouseEventArgs e)
    {
        // WPF also raises MouseMove when the list opens or reflows under a still pointer;
        // only a real move may take the highlight away from the top match.
        var at = e.GetPosition(null);
        if (at == _lastPointer) return;
        _lastPointer = at;
        if (OptionAt(e.OriginalSource) is { } item) Options.SelectedItem = item;
    }

    private void OnOptionsMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (OptionAt(e.OriginalSource) is not { } item) return;
        e.Handled = true; // as onMouseDown preventDefault: the click never takes focus from the input
        Choose(item);
    }

    private ProjectEntry? OptionAt(object source) =>
        source is DependencyObject d && ItemsControl.ContainerFromElement(Options, d) is ListBoxItem { DataContext: ProjectEntry p }
            ? p
            : null;

    // ─── Closing from outside ────────────────────────────────────────────────

    private void OnWindowMouseDown(object sender, MouseButtonEventArgs e)
    {
        // The list lives in its own popup window, so only clicks outside the picker get here.
        if (e.OriginalSource is DependencyObject d && IsInside(d)) return;
        Close();
    }

    private void OnWindowChanged(object? sender, EventArgs e) => Close();

    private bool IsInside(DependencyObject d)
    {
        for (var cur = d; cur != null; cur = cur is Visual ? VisualTreeHelper.GetParent(cur) : LogicalTreeHelper.GetParent(cur))
        {
            if (ReferenceEquals(cur, this)) return true;
        }
        return false;
    }
}
