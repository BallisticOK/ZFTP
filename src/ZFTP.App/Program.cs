using Avalonia;

namespace ZFTP.App;

internal static class Program
{
    // Avalonia needs an explicit entry point (WPF's was implicit via App.xaml's
    // StartupUri) - initialization happens in App.axaml.cs instead.
    [STAThread]
    public static void Main(string[] args) => BuildAvaloniaApp()
        .StartWithClassicDesktopLifetime(args);

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
