using System.Management;

namespace IMPWeldPhotos;

/// <summary>
/// Raises DriveArrived (on the thread that created it) when a removable drive becomes
/// ready. The WMI volume-arrival event is the fast path; the poll catches whatever
/// WMI misses, including a card that wasn't ready yet when the event fired.
/// </summary>
public sealed class DriveWatcher : IDisposable
{
    private readonly SynchronizationContext _context;
    private readonly object _gate = new();
    private readonly ManagementEventWatcher? _wmi;
    private readonly System.Threading.Timer _poll;
    private HashSet<string> _known;

    public event Action<string>? DriveArrived;

    public DriveWatcher(TimeSpan pollInterval)
    {
        _context = SynchronizationContext.Current ?? new SynchronizationContext();
        // Cards already in at start are the startup scan's job, not arrivals.
        _known = ReadyRemovableDrives();

        try
        {
            _wmi = new ManagementEventWatcher(new WqlEventQuery(
                "SELECT * FROM Win32_VolumeChangeEvent WHERE EventType = 2"));
            _wmi.EventArrived += (_, _) => Check();
            _wmi.Start();
        }
        catch
        {
            // No WMI: the poll alone still finds cards, a few seconds later.
            _wmi?.Dispose();
            _wmi = null;
        }

        _poll = new System.Threading.Timer(_ => Check(), null, pollInterval, pollInterval);
    }

    public static HashSet<string> ReadyRemovableDrives()
    {
        var drives = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var d in DriveInfo.GetDrives())
        {
            try
            {
                if (d.DriveType == DriveType.Removable && d.IsReady) drives.Add(d.RootDirectory.FullName);
            }
            catch
            {
                // A reader slot can throw while a card is going in or out.
            }
        }
        return drives;
    }

    private void Check()
    {
        List<string> arrived;
        lock (_gate)
        {
            var now = ReadyRemovableDrives();
            arrived = now.Where(d => !_known.Contains(d)).ToList();
            _known = now;
        }
        foreach (var drive in arrived) _context.Post(_ => DriveArrived?.Invoke(drive), null);
    }

    public void Dispose()
    {
        _poll.Dispose();
        try
        {
            _wmi?.Stop();
        }
        catch
        {
        }
        _wmi?.Dispose();
    }
}
