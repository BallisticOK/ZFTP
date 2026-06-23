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

    protected override void OnStartup(StartupEventArgs e)
    {
        _singleInstance = new Mutex(initiallyOwned: true, "ZFTP.SingleInstance.Mutex", out bool isNew);
        if (!isNew)
        {
            // Another ZFTP is already running. Quietly exit.
            Shutdown();
            return;
        }

        // Load WinFsp's native DLL up front so mounting works even when ZFTP is
        // published as a self-contained app, and move any old config to the new
        // C:\ProgramData\ZFTP folder.
        WinFspNative.EnsureLoaded();
        ProfileStore.MigrateOldLocation();

        // Safety net + crash logging so we can see what's going wrong.
        var log = System.IO.Path.Combine(ProfileStore.FolderPath, "crash.log");
        void Write(string where, object? ex)
        {
            try { System.IO.File.AppendAllText(log, $"[{where}] {DateTime.Now:HH:mm:ss}\n{ex}\n\n"); } catch { }
        }
        DispatcherUnhandledException += (_, ex) => { Write("Dispatcher", ex.Exception); ex.Handled = true; };
        AppDomain.CurrentDomain.UnhandledException += (_, ex) => Write("AppDomain", ex.ExceptionObject);
        System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (_, ex) => { Write("Task", ex.Exception); ex.SetObserved(); };

        StartHidden = e.Args.Any(a => a.Equals("--minimized", StringComparison.OrdinalIgnoreCase));
        base.OnStartup(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _singleInstance?.ReleaseMutex();
        _singleInstance?.Dispose();
        base.OnExit(e);
    }
}
