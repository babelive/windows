using Avalonia;

namespace Babelive;

/// <summary>
/// Avalonia entry point. Replaces the implicit WPF Main that
/// <c>UseWPF</c> generated. Hands off to App.OnFrameworkInitializationCompleted
/// for the actual window/tray setup.
/// </summary>
internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
        => BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
