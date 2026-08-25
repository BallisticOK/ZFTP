using System.Security.Principal;
using System.Threading;
using ZFTP.Core;
using Application = System.Windows.Application;
using StartupEventArgs = System.Windows.StartupEventArgs;
using ExitEventArgs = System.Windows.ExitEventArgs;

namespace ZFTP.App;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : Application
{
    /// <summary>True when launched with --minimized (e.g. from Windows startup).</summary>
    public static bool StartHidden { get; private set; }

    // Held for the whole app lifetime so only one ZFTP runs at a time. A second
    // launch fails to acquire it and exits — preventing two instances from
    // fighting over the same drive letters.
    private Mutex? _singleInstance;
    private bool _ownsSingleInstance;

    protected override void OnStartup(StartupEventArgs e)
    {
        _singleInstance = new Mutex(initiallyOwned: true, "ZFTP.SingleInstance.Mutex", out bool isNew);
        if (!isNew)
        {
            // Another ZFTP is already running. Quietly exit.
            Shutdown();
            return;
        }

        _ownsSingleInstance = true;

        AppLog.Initialize();
        AppLog.Info("App", "Primary application instance acquired.");

        // WinFsp drive letters live in the launching user's DOS-device namespace.
        // If ZFTP is elevated while Explorer is not (normal UAC behavior), ZFTP
        // can successfully mount a drive that Explorer cannot see. ZFTP never
        // needs admin rights, so stop clearly instead of reporting a misleading
        // "Mounted" state for an elevated-only drive.
        bool elevated = IsProcessElevated();
        AppLog.Info("App", $"Process elevated={elevated}.");
        if (elevated)
        {
            AppLog.Warn("App",
                "ZFTP was launched as Administrator. Mounted drive letters would be invisible to normal Windows Explorer, so startup was stopped.");
            System.Windows.MessageBox.Show(
                "ZFTP is running as Administrator. Windows Explorer normally runs without elevation, so drives mounted by this copy of ZFTP would not appear in This PC.\n\n" +
                "Close this message and launch ZFTP normally from the Start menu or desktop shortcut. ZFTP does not need administrator rights.",
                "ZFTP must run normally",
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Warning);
            Shutdown();
            return;
        }

        // Load WinFsp's native DLL up front so mounting works even when ZFTP is
        // published as a self-contained app, and move any old ProgramData config
        // into the current per-user AppData\ZFTP folder.
        WinFspNative.EnsureLoaded();
        ProfileStore.MigrateOldLocation();

        // rclone is a child process for every FTP/cloud mount. If an older ZFTP
        // was killed or crashed, Windows can leave those children alive and the
        // drive letters stay occupied forever. Reclaim only our bundled copy on
        // startup; newly-started children are also placed in a kill-on-close job.
        RcloneService.CleanupStaleProcesses();

        // Keep unhandled failures in the same .log file as mount diagnostics.
        DispatcherUnhandledException += (_, ex) =>
        {
            AppLog.Error("Dispatcher", "Unhandled UI exception.", ex.Exception);
            ex.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, ex) =>
            AppLog.Error("AppDomain", $"Unhandled application-domain exception: {ex.ExceptionObject}");
        System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (_, ex) =>
        {
            AppLog.Error("TaskScheduler", "Unobserved task exception.", ex.Exception);
            ex.SetObserved();
        };

        StartHidden = e.Args.Any(a => a.Equals("--minimized", StringComparison.OrdinalIgnoreCase));
        base.OnStartup(e);
    }

    private static bool IsProcessElevated()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        AppLog.Info("App", $"Application exiting with code {e.ApplicationExitCode}.");
        if (_ownsSingleInstance)
            _singleInstance?.ReleaseMutex();
        _singleInstance?.Dispose();
        base.OnExit(e);
    }
}
