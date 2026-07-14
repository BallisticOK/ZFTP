using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using ZFTP.Core;

namespace ZFTP.App;

public partial class App : Application
{
    /// <summary>True when launched with --minimized (e.g. from OS startup/login).</summary>
    public static bool StartHidden { get; private set; }

    // Held for the whole app lifetime so only one ZFTP runs at a time. A second
    // launch fails to acquire it and exits — preventing two instances from
    // fighting over the same drive letters / mount paths.
    private Mutex? _singleInstance;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            _singleInstance = new Mutex(initiallyOwned: true, "ZFTP.SingleInstance.Mutex", out bool isNew);
            if (!isNew)
            {
                // Another ZFTP is already running. Quietly exit.
                desktop.Shutdown();
                return;
            }

#if WINDOWS
            // Load WinFsp's native DLL up front so mounting works even when ZFTP
            // is published as a self-contained app.
            WinFspNative.EnsureLoaded();
#endif
            ProfileStore.MigrateOldLocation();

            // Safety net + crash logging so we can see what's going wrong.
            var log = Path.Combine(ProfileStore.FolderPath, "crash.log");
            void Write(string where, object? ex)
            {
                try { File.AppendAllText(log, $"[{where}] {DateTime.Now:HH:mm:ss}\n{ex}\n\n"); } catch { }
            }
            AppDomain.CurrentDomain.UnhandledException += (_, ex) => Write("AppDomain", ex.ExceptionObject);
            TaskScheduler.UnobservedTaskException += (_, ex) => { Write("Task", ex.Exception); ex.SetObserved(); };

            StartHidden = desktop.Args?.Any(a => a.Equals("--minimized", StringComparison.OrdinalIgnoreCase)) ?? false;

            var main = new MainWindow();
            desktop.MainWindow = main;
            desktop.Exit += (_, _) =>
            {
                _singleInstance?.ReleaseMutex();
                _singleInstance?.Dispose();
            };
        }

        base.OnFrameworkInitializationCompleted();
    }
}
