using System.Windows;

namespace IMPWeldPhotos;

public partial class App : Application
{
    private const string MutexName = @"Local\IMPWeldPhotos";
    private const string ShowEventName = @"Local\IMPWeldPhotos.Show";

    private Mutex? _mutex;
    private bool _ownsMutex;
    private EventWaitHandle? _showEvent;

    public static bool IsDark { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        // One window only: a second start just brings the running one forward.
        _mutex = new Mutex(true, MutexName, out _ownsMutex);
        if (!_ownsMutex)
        {
            try
            {
                using var show = EventWaitHandle.OpenExisting(ShowEventName);
                show.Set();
            }
            catch
            {
            }
            Shutdown();
            return;
        }

        base.OnStartup(e);
        DispatcherUnhandledException += (_, args) =>
        {
            MessageBox.Show(args.Exception.Message, "IMP Weld Photos", MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };

        var settings = UserSettings.Load();
        ApplyTheme(settings.Theme == null ? UserSettings.WindowsUsesDarkTheme() : settings.Theme == "dark");

        // A folder on the command line is scanned instead of waiting for a card.
        var window = new MainWindow(settings, e.Args.FirstOrDefault(Directory.Exists));
        MainWindow = window;
        window.Show();

        _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
        new Thread(() =>
        {
            while (_showEvent.WaitOne()) Dispatcher.BeginInvoke(new Action(window.BringToFront));
        }) { IsBackground = true, Name = "ShowListener" }.Start();
    }

    public static void ApplyTheme(bool dark)
    {
        IsDark = dark;
        Current.Resources.MergedDictionaries[0] = new ResourceDictionary
        {
            Source = new Uri($"pack://application:,,,/Assets/Theme.{(dark ? "Dark" : "Light")}.xaml"),
        };
        // The toned weld photo sinks into the dark background without an edge.
        Current.Resources["EmptyImageOutline"] = new Thickness(dark ? 2 : 0);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (_ownsMutex) _mutex?.ReleaseMutex();
        _mutex?.Dispose();
        base.OnExit(e);
    }
}
