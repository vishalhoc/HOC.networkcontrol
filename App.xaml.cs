using Microsoft.UI.Xaml;
using System;
using System.IO;

namespace WinNetControl;

/// <summary>
/// Provides application-specific behavior to supplement the default Application class.
/// </summary>
public partial class App : Application
{
    /// <summary>
    /// The main application window. Use <c>App.Window</c> from any class that needs
    /// the window reference (for dialogs, pickers, interop, etc.).
    /// </summary>
    public static Window Window { get; private set; } = null!;

    /// <summary>
    /// The UI thread dispatcher. Use <c>App.DispatcherQueue</c> to marshal calls
    /// to the UI thread. Fully qualified to avoid CS0104 ambiguity with
    /// <see cref="Windows.System.DispatcherQueue"/>.
    /// </summary>
    public static Microsoft.UI.Dispatching.DispatcherQueue DispatcherQueue { get; private set; } = null!;

    /// <summary>
    /// The native window handle (HWND). Use for file pickers,
    /// <c>DataTransferManager</c>, and any WinRT interop that requires
    /// <c>InitializeWithWindow</c>.
    /// </summary>
    public static nint WindowHandle => WinRT.Interop.WindowNative.GetWindowHandle(Window);

    private static readonly string CrashLog = Path.Combine(
        AppDomain.CurrentDomain.BaseDirectory, "WinNetControl_crash.log");

    /// <summary>
    /// Initializes the singleton application object.
    /// </summary>
    public App()
    {
        // Global unhandled exception handlers
        this.UnhandledException += (s, e) =>
        {
            e.Handled = true;
            LogCrash("UnhandledException", e.Exception);
        };

        AppDomain.CurrentDomain.UnhandledException += (s, e) =>
        {
            if (e.ExceptionObject is Exception ex)
                LogCrash("AppDomain", ex);
        };

        System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (s, e) =>
        {
            e.SetObserved();
            LogCrash("UnobservedTask", e.Exception);
        };

        InitializeComponent();
    }

    /// <summary>
    /// Invoked when the application is launched.
    /// </summary>
    /// <param name="args">Details about the launch request and process.</param>
    protected override void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
    {
        try
        {
            Window = new MainWindow();
            DispatcherQueue = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();

            // Always restore system proxy when the main window closes
            Window.Closed += (s, e) =>
            {
                try
                {
                    // Grab proxy service from the ViewModel and stop it cleanly
                    if (Window is MainWindow mw)
                    {
                        mw.ViewModel?.ProxyService?.Stop();
                    }
                }
                catch { }
                finally
                {
                    // Nuclear fallback — write to registry directly
                    Core.HttpProxyService.ForceRestoreSystemProxy();
                }
            };

            Window.Activate();
        }
        catch (Exception ex)
        {
            LogCrash("OnLaunched", ex);
        }
    }

    private static void LogCrash(string source, Exception ex)
    {
        try
        {
            File.AppendAllText(CrashLog,
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [{source}]\n{ex}\n\n");
        }
        catch { }
    }
}
