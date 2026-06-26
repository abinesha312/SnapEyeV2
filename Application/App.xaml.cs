using System;
using System.Windows;
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
            try
            {
                // Load configuration
                Config.AppConfig.LoadConfiguration();

                // Standalone mode: start (or reuse) the backend automatically so the
                // app needs no external launcher or watchdog. Runs in the background;
                // the UI shows immediately and services connect once it's healthy.
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

        /// <summary>
        /// Application exit - shut down the backend we spawned (external backends untouched)
        /// </summary>
        private void Application_Exit(object sender, ExitEventArgs e)
        {
            BackendProcessService.Stop();
        }
    }
}
