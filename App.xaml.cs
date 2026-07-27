using Microsoft.UI.Xaml;
using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

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
    /// Strongly-typed accessor for the main window.
    /// IMP#25: replaces the fragile <c>App.MainWindow is MainWindow mw</c> cast
    /// pattern used throughout the codebase. Returns null before the window
    /// is created (during startup) so callers can do null-conditional calls.
    /// </summary>
    public static MainWindow? MainWindow => Window as MainWindow;

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

    // ── Single-instance enforcement ──────────────────────────────────────────
    private static Mutex? _instanceMutex;

    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(nint hWnd);
    [DllImport("user32.dll")] private static extern bool ShowWindow(nint hWnd, int nCmdShow);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern nint FindWindow(string? lpClassName, string lpWindowName);
    private const int SW_RESTORE = 9;

    /// <summary>Returns false if another instance is already running (caller should exit).</summary>
    public static bool EnsureSingleInstance()
    {
        _instanceMutex = new Mutex(true, "WinNetControl_SingleInstance_v2", out bool createdNew);
        if (!createdNew)
        {
            // Another instance exists — bring it to front
            nint hwnd = FindWindow(null, "WinNetControl");
            if (hwnd == nint.Zero)
            {
                // Try partial match via process
                foreach (var p in System.Diagnostics.Process.GetProcessesByName("WinNetControl"))
                {
                    if (p.MainWindowHandle != nint.Zero) { hwnd = p.MainWindowHandle; break; }
                }
            }
            if (hwnd != nint.Zero)
            {
                ShowWindow(hwnd, SW_RESTORE);
                SetForegroundWindow(hwnd);
            }
            return false;  // signal: do not continue
        }
        return true;
    }

    /// <inheritdoc />
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
        // Single instance guard — if already running, focus existing window and exit
        if (!EnsureSingleInstance())
        {
            this.Exit();
            return;
        }

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
