using ClaudeUsageTray.App;
using ClaudeUsageTray.UI;
using ClaudeUsageTray.Utilities;

namespace ClaudeUsageTray;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        // build helper: regenerates the icon embedded in the .exe (assets/app.ico) from the same drawing code
        if (args is ["--write-icon", var iconPath])
        {
            File.WriteAllBytes(iconPath, AppIcon.IcoBytes());
            return;
        }

        // single instance per user session
        using var mutex = new Mutex(initiallyOwned: true, AppConstants.MutexName, out var createdNew);
        if (!createdNew) return;

        // must run before any Control exists on this thread
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        ApplicationConfiguration.Initialize();
        // standard controls (buttons, dialogs) follow the Windows light/dark setting; must precede the first window
        Application.SetColorMode(SystemColorMode.System);
        // make sure UsageService (built before the message loop starts) can marshal events to the UI thread
        SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());

        Application.ThreadException += (_, e) => Logger.Error($"UI exception: {e.Exception.GetType().Name}: {e.Exception.Message}");
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            Logger.Error($"Unhandled exception: {(e.ExceptionObject as Exception)?.GetType().Name}");

        var mock = args.Contains("--mock", StringComparer.OrdinalIgnoreCase);
        Logger.Info(mock ? "Starting (mock data)" : "Starting");

        using var context = new TrayApplicationContext(mock);
        Application.Run(context);
        Logger.Info("Exited");
    }
}
