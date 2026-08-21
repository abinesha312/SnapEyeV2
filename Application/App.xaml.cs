using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using SnapEye.Services;

namespace SnapEye
{
    public partial class App : Application
    {
        /// <summary>
        /// Application startup - check session and show appropriate window
        /// </summary>
        private void Application_Startup(object sender, StartupEventArgs e)
        {
            // Global safety net: SnapEye must never hard-crash. Any exception that
            // reaches these handlers is logged and swallowed instead of taking down
            // the process, so a single bad token/callback/background task can't kill
            // an in-progress interview, sales call, or meeting.
            this.DispatcherUnhandledException += OnDispatcherUnhandledException;
            AppDomain.CurrentDomain.UnhandledException += OnAppDomainUnhandledException;
            TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;

            try
            {
                // Load configuration
                Config.AppConfig.LoadConfiguration();

                // Warm up backend: polls /health and auto-starts the Podman Compose stack
                // (scripts/start-snapeye-backend.ps1) if it isn't already running.
                _ = BackendProcessService.EnsureBackendAsync();

                // Check if valid session exists
                SessionManager.SessionData? session = SessionManager.LoadSession();

                if (session != null && DateTime.Now <= session.ExpiryTime)
                {
                    // Valid session exists - show Dashboard with profile
                    Console.WriteLine($"SnapEye: Found valid session for {session.Username}");
                    ShowDashboard();
                }
                else if (session != null && DateTime.Now > session.ExpiryTime)
                {
                    // Session expired but within 90 day storage window
                    Console.WriteLine("SnapEye: Session expired, showing dashboard for re-authentication");
                    ShowDashboard();
                }
                else
                {
                    // No session - show Dashboard for login
                    Console.WriteLine("SnapEye: No session found, showing dashboard");
                    ShowDashboard();
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"SnapEye Startup Error: {ex.Message}");
                MessageBox.Show($"Failed to start SnapEye: {ex.Message}", 
                    "Startup Error", 
                    MessageBoxButton.OK, 
                    MessageBoxImage.Error);
                Application.Current.Shutdown();
            }
        }

        /// <summary>
        /// Show Dashboard window
        /// </summary>
        private void ShowDashboard()
        {
            Dashboard.Dashboard dashboard = new Dashboard.Dashboard();
            dashboard.Show();
        }

        // === Global exception handlers (crash resilience) ===

        /// <summary>
        /// Catches exceptions that escape a UI-thread event handler (e.g. a bad
        /// streaming-token callback). Without this, WPF's default behavior is to
        /// terminate the process. We log and mark it handled so the app survives.
        /// </summary>
        private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
        {
            LogCrash("DispatcherUnhandledException", e.Exception);
            e.Handled = true;
        }

        /// <summary>
        /// Last-resort logger for exceptions on non-UI threads. These are not
        /// recoverable (the CLR is already tearing the process down), but logging
        /// them means the real cause is in crash.log instead of a silent exit.
        /// </summary>
        private void OnAppDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
        {
            LogCrash("AppDomainUnhandledException", e.ExceptionObject as Exception);
        }

        /// <summary>
        /// Catches exceptions from fire-and-forget Task.Run(...) calls that would
        /// otherwise only surface (or crash the process) when the GC finalizes the
        /// faulted task.
        /// </summary>
        private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
        {
            LogCrash("UnobservedTaskException", e.Exception);
            e.SetObserved();
        }

        private static readonly object crashLogLock = new object();

        private static void LogCrash(string source, Exception? ex)
        {
            try
            {
                Console.WriteLine($"[Crash] {source}: {ex?.Message}");
                string logDir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SnapEye", "logs");
                Directory.CreateDirectory(logDir);
                string logPath = Path.Combine(logDir, "crash.log");
                string entry = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {source}: {ex}\n";
                lock (crashLogLock)
                {
                    File.AppendAllText(logPath, entry);
                }
            }
            catch
            {
                // Logging must never itself throw during crash handling.
            }
        }

        /// <summary>
        /// Application exit - shut down the backend we spawned (external backends untouched)
        /// </summary>
        private void Application_Exit(object sender, ExitEventArgs e)
        {
            BackendProcessService.Stop();
        }
    }
}
