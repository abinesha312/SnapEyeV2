using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using SnapEye.Models;
using SnapEye.Services;
using static SnapEye.Services.SessionManager;

namespace SnapEye.Dashboard
{
    /// <summary>
    /// Dashboard window for user authentication and session management
    /// </summary>
    public partial class Dashboard : Window
    {
        private readonly AuthService authService;
        private readonly ConversationHistoryService historyService;
        private readonly ConversationTitleService titleService;
        private SessionData? currentSession;
        private List<ConversationSession> loadedConversations = new();
        private ConversationSession? selectedConversation;
        private Border? selectedListRow;

        public Dashboard()
        {
            InitializeComponent();
            authService = new AuthService(Config.AppConfig.BackendHttpUrl);
            historyService = new ConversationHistoryService();
            titleService = new ConversationTitleService(Config.AppConfig.BackendHttpUrl);

            // Subscribe to auth service events
            authService.ErrorOccurred += OnAuthError;

            // Check for existing session
            CheckExistingSession();
        }

        #region Initialization

        /// <summary>
        /// Check if a valid session already exists
        /// </summary>
        private async void CheckExistingSession()
        {
            try
            {
                currentSession = SessionManager.LoadSession();

                if (currentSession == null)
                {
                    // No session - show login form
                    ShowLoginPanel();
                    return;
                }

                // Session exists - verify it's still valid
                if (DateTime.Now <= currentSession.ExpiryTime)
                {
                    // Token still valid - verify with backend
                    bool isValid = await VerifyTokenWithBackend(currentSession.AccessToken);
                    
                    if (isValid)
                    {
                        ShowProfilePanel();
                    }
                    else
                    {
                        // Token invalid - try to refresh
                        await TryRefreshToken();
                    }
                }
                else
                {
                    // Token expired - try to refresh
                    await TryRefreshToken();
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Session check error: {ex.Message}");
                ShowLoginPanel();
            }
        }

        #endregion

        #region UI Events

        private void Header_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton == MouseButton.Left)
            {
                this.DragMove();
            }
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            Application.Current.Shutdown();
        }

        private void InputField_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                LoginButton_Click(sender, e);
            }
        }

        private async void LoginButton_Click(object sender, RoutedEventArgs e)
        {
            await PerformLogin();
        }

        private void LaunchButton_Click(object sender, RoutedEventArgs e)
        {
            LaunchMainApplication();
        }

        private void SignOutButton_Click(object sender, RoutedEventArgs e)
        {
            SignOut();
        }

        #endregion

        #region Authentication Logic

        /// <summary>
        /// Perform login with username and API key
        /// </summary>
        private async Task PerformLogin()
        {
            // Get credentials
            string username = UsernameTextBox.Text.Trim();
            string apiKey = ApiKeyPasswordBox.Password.Trim();

            // Validate input
            if (string.IsNullOrEmpty(username))
            {
                ShowError("Please enter your username");
                return;
            }

            if (string.IsNullOrEmpty(apiKey))
            {
                ShowError("Please enter your API key");
                return;
            }

            // Show loading state
            ShowLoading(true);
            HideError();

            try
            {
                // Authenticate with backend
                bool authenticated = await authService.LoginAsync(username, apiKey);

                if (!authenticated)
                {
                    ShowError("Authentication failed. Please check your credentials.");
                    ShowLoading(false);
                    return;
                }

                // Create session data with all tokens from backend
                currentSession = new SessionData
                {
                    Username = username,
                    AccessToken = authService.AccessToken ?? "",
                    RefreshToken = authService.RefreshToken ?? "", // Capture refresh token
                    TokenType = "bearer",
                    ExpiresIn = authService.ExpiresIn,
                    LoginTime = DateTime.Now,
                    ExpiryTime = DateTime.Now.AddSeconds(authService.ExpiresIn - 60), // 60 second buffer
                    StorageExpiry = DateTime.Now.AddDays(90) // Store for 90 days (2160 hours)
                };

                // Save session
                SessionManager.SaveSession(currentSession);

                // Show success
                ShowLoading(false);
                ShowProfilePanel();

                Console.WriteLine($"SnapEye: Login successful for {username}");
            }
            catch (Exception ex)
            {
                ShowError($"Login failed: {ex.Message}");
                ShowLoading(false);
                Console.WriteLine($"SnapEye Login Error: {ex.Message}");
            }
        }

        /// <summary>
        /// Verify token with backend
        /// </summary>
        private async Task<bool> VerifyTokenWithBackend(string token)
        {
            try
            {
                // Set the token on the auth service so it can be used for verification
                authService.AccessToken = token;
                bool isValid = await authService.VerifyTokenAsync();
                
                return isValid;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Try to refresh expired token
        /// </summary>
        private async Task TryRefreshToken()
        {
            if (currentSession == null)
            {
                // No session - show login
                ShowLoginPanel();
                return;
            }

            // Check if storage has expired (90 days)
            if (DateTime.Now > currentSession.StorageExpiry)
            {
                // Storage expired - delete session and show login
                SessionManager.DeleteSession();
                ShowLoginPanel();
                ShowError("Session storage expired (90 days). Please sign in again.");
                return;
            }

            // Try to refresh the token
            try
            {
                // Set the current tokens in auth service for refresh
                authService.AccessToken = currentSession.AccessToken;
                authService.RefreshToken = currentSession.RefreshToken;

                // Attempt token refresh
                bool refreshed = await authService.RefreshTokenAsync();

                if (refreshed)
                {
                    // Update session with new tokens
                    currentSession.AccessToken = authService.AccessToken ?? currentSession.AccessToken;
                    currentSession.RefreshToken = authService.RefreshToken ?? currentSession.RefreshToken;
                    currentSession.ExpiresIn = authService.ExpiresIn;
                    currentSession.ExpiryTime = DateTime.Now.AddSeconds(authService.ExpiresIn - 60);
                    
                    // Save updated session
                    SessionManager.SaveSession(currentSession);

                    // Show profile
                    ShowProfilePanel();
                    
                    Console.WriteLine($"SnapEye: Token refreshed successfully for {currentSession.Username}");
                }
                else
                {
                    // Refresh failed - show login
                    ShowLoginPanel();
                    ShowError("Session expired. Please sign in again.");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"SnapEye: Token refresh error - {ex.Message}");
                ShowLoginPanel();
                ShowError("Failed to refresh session. Please sign in again.");
            }
        }

        /// <summary>
        /// Sign out user
        /// </summary>
        private void SignOut()
        {
            // Delete session
            SessionManager.DeleteSession();
            currentSession = null;

            // Clear input fields
            UsernameTextBox.Clear();
            ApiKeyPasswordBox.Clear();

            // Show login panel
            ShowLoginPanel();

            Console.WriteLine("SnapEye: User signed out");
        }

        #endregion

        #region Application Launch

        /// <summary>
        /// Launch main SnapEye overlay application
        /// </summary>
        private void LaunchMainApplication()
        {
            try
            {
                if (currentSession == null)
                {
                    ShowError("No active session. Please sign in first.");
                    return;
                }

                // Verify session is still valid
                if (DateTime.Now > currentSession.ExpiryTime)
                {
                    ShowError("Session expired. Please sign in again.");
                    ShowLoginPanel();
                    return;
                }

                // Create and show overlay window
                OverlayWindow overlayWindow = new OverlayWindow();
                
                // Pass session data to overlay
                overlayWindow.SetSession(currentSession);
                
                overlayWindow.Show();

                // Hide dashboard
                this.Hide();

                // Subscribe to overlay closed event
                overlayWindow.Closed += (s, e) => {
                    // Show dashboard again when overlay closes
                    this.Show();
                    
                    // Reload session status
                    CheckExistingSession();
                };

                Console.WriteLine("SnapEye: Main application launched");
            }
            catch (Exception ex)
            {
                ShowError($"Failed to launch application: {ex.Message}");
                Console.WriteLine($"SnapEye Launch Error: {ex.Message}");
            }
        }

        #endregion

        #region UI Helper Methods

        private void ShowLoginPanel()
        {
            LoginPanel.Visibility = Visibility.Visible;
            ProfilePanel.Visibility = Visibility.Collapsed;
        }

        private void ShowProfilePanel()
        {
            if (currentSession == null) return;

            // Update profile information
            ProfileUsername.Text = currentSession.Username;
            SidebarUsername.Text = currentSession.Username;
            ProfileExpiry.Text = currentSession.ExpiryTime.ToString("MMM dd, yyyy HH:mm");
            ProfileLastLogin.Text = currentSession.LoginTime.ToString("MMM dd, yyyy HH:mm");

            // Update status
            if (DateTime.Now <= currentSession.ExpiryTime)
            {
                ProfileStatus.Text = "Active";
                ProfileStatus.Foreground = new System.Windows.Media.SolidColorBrush(
                    System.Windows.Media.Color.FromRgb(76, 175, 80)); // Green
            }
            else
            {
                ProfileStatus.Text = "Expired";
                ProfileStatus.Foreground = new System.Windows.Media.SolidColorBrush(
                    System.Windows.Media.Color.FromRgb(255, 107, 107)); // Red
            }

            // Wire title service auth token so lazy title generation works if needed.
            titleService.SetAuthToken(currentSession.AccessToken);

            // Reset sidebar selection to Launch on every show.
            NavLaunch.IsChecked = true;
            ShowLaunchView();

            LoginPanel.Visibility = Visibility.Collapsed;
            ProfilePanel.Visibility = Visibility.Visible;
        }

        private void ShowLoading(bool show)
        {
            LoadingPanel.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            LoginButton.IsEnabled = !show;
            UsernameTextBox.IsEnabled = !show;
            ApiKeyPasswordBox.IsEnabled = !show;
        }

        private void ShowError(string message)
        {
            ErrorTextBlock.Text = message;
            ErrorTextBlock.Visibility = Visibility.Visible;
        }

        private void HideError()
        {
            ErrorTextBlock.Visibility = Visibility.Collapsed;
        }

        private void OnAuthError(object? sender, string error)
        {
            Dispatcher.Invoke(() => {
                ShowError(error);
                ShowLoading(false);
            });
        }

        #endregion

        #region Public Methods

        /// <summary>
        /// Get current session data
        /// </summary>
        public SessionData? GetCurrentSession()
        {
            return currentSession;
        }

        #endregion

        #region Sidebar Navigation

        private void NavLaunch_Checked(object sender, RoutedEventArgs e)
        {
            if (!IsLoaded) return;
            ShowLaunchView();
        }

        private void NavConversations_Checked(object sender, RoutedEventArgs e)
        {
            if (!IsLoaded) return;
            ShowConversationsView();
            LoadConversations();
        }

        private void ShowLaunchView()
        {
            if (LaunchView != null) LaunchView.Visibility = Visibility.Visible;
            if (ConversationsView != null) ConversationsView.Visibility = Visibility.Collapsed;
        }

        private void ShowConversationsView()
        {
            if (LaunchView != null) LaunchView.Visibility = Visibility.Collapsed;
            if (ConversationsView != null) ConversationsView.Visibility = Visibility.Visible;
        }

        private void RefreshConversations_Click(object sender, RoutedEventArgs e) => LoadConversations();

        #endregion

        #region Conversations

        private void LoadConversations()
        {
            try
            {
                loadedConversations = historyService.ListSavedSessions().ToList();
                ConversationsList.Children.Clear();

                if (loadedConversations.Count == 0)
                {
                    ConversationsEmpty.Visibility = Visibility.Visible;
                    ConversationsSubtitle.Text = "No saved conversations yet.";
                    ClearDetail();
                    return;
                }

                ConversationsEmpty.Visibility = Visibility.Collapsed;
                ConversationsSubtitle.Text = $"{loadedConversations.Count} saved conversation{(loadedConversations.Count == 1 ? "" : "s")}.";

                foreach (var s in loadedConversations)
                    ConversationsList.Children.Add(BuildListRow(s));

                // Auto-select the first row.
                if (loadedConversations.Count > 0)
                    SelectConversation(loadedConversations[0], (Border)ConversationsList.Children[0]);

                // Kick off lazy title generation for any saved session without a title.
                _ = BackfillTitlesAsync();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Dashboard] LoadConversations error: {ex.Message}");
            }
        }

        private Border BuildListRow(ConversationSession s)
        {
            string title = !string.IsNullOrWhiteSpace(s.Title)
                ? s.Title!
                : ConversationTitleService.Fallback(s);

            int count = s.Messages?.Count ?? 0;
            string meta = $"{s.StartedAt:MMM d, yyyy · h:mm tt} · {count} message{(count == 1 ? "" : "s")}";

            var titleBlock = new TextBlock
            {
                Text = title,
                FontSize = 13,
                FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(Color.FromRgb(0x33, 0x33, 0x33)),
                TextWrapping = TextWrapping.Wrap,
                FontFamily = new FontFamily("Segoe UI"),
            };
            var metaBlock = new TextBlock
            {
                Text = meta,
                FontSize = 11,
                Foreground = new SolidColorBrush(Color.FromRgb(0x8A, 0x83, 0x94)),
                Margin = new Thickness(0, 3, 0, 0),
                FontFamily = new FontFamily("Segoe UI"),
            };

            var panel = new StackPanel();
            panel.Children.Add(titleBlock);
            panel.Children.Add(metaBlock);

            var row = new Border
            {
                Background = new SolidColorBrush(Colors.Transparent),
                Padding = new Thickness(12, 10, 12, 10),
                CornerRadius = new CornerRadius(8),
                Margin = new Thickness(4, 2, 4, 2),
                Cursor = Cursors.Hand,
                Child = panel,
                Tag = s,
            };

            row.MouseEnter += (_, _) =>
            {
                if (row != selectedListRow)
                    row.Background = new SolidColorBrush(Color.FromRgb(0xF0, 0xEA, 0xF8));
            };
            row.MouseLeave += (_, _) =>
            {
                if (row != selectedListRow)
                    row.Background = new SolidColorBrush(Colors.Transparent);
            };
            row.MouseLeftButtonUp += (_, _) => SelectConversation(s, row);

            return row;
        }

        private void SelectConversation(ConversationSession s, Border row)
        {
            if (selectedListRow != null)
                selectedListRow.Background = new SolidColorBrush(Colors.Transparent);

            selectedListRow = row;
            selectedConversation = s;

            row.Background = new SolidColorBrush(Color.FromRgb(0xE2, 0xD2, 0xF7));

            RenderDetail(s);
        }

        private void ClearDetail()
        {
            DetailTitle.Text = "Select a conversation";
            DetailMeta.Text = "";
            DetailThread.Children.Clear();
        }

        private void RenderDetail(ConversationSession s)
        {
            string title = !string.IsNullOrWhiteSpace(s.Title) ? s.Title! : ConversationTitleService.Fallback(s);
            DetailTitle.Text = title;

            int count = s.Messages?.Count ?? 0;
            string duration = s.EndedAt.HasValue
                ? FormatDuration(s.EndedAt.Value - s.StartedAt)
                : "in progress";
            DetailMeta.Text = $"{s.StartedAt:dddd, MMM d · h:mm tt} · {count} message{(count == 1 ? "" : "s")} · {duration}";

            DetailThread.Children.Clear();
            if (s.Messages == null) return;

            foreach (var m in s.Messages)
                DetailThread.Children.Add(BuildMessageBubble(m));
        }

        private static UIElement BuildMessageBubble(ConversationMessage m)
        {
            bool isUser = m.Kind == ConversationMessageKind.UserTyped
                       || m.Kind == ConversationMessageKind.UserSpoken
                       || m.Kind == ConversationMessageKind.QuickAction;
            bool isOther = m.Kind == ConversationMessageKind.OtherSpoken;
            bool isAI = m.Kind == ConversationMessageKind.AssistantAnswer;

            Color bubbleColor, borderColor, textColor;
            string roleLabel;

            if (isUser)
            {
                bubbleColor = Color.FromRgb(0x25, 0x63, 0xEB);
                borderColor = Color.FromRgb(0x1D, 0x4E, 0xD8);
                textColor = Color.FromRgb(0xF8, 0xFA, 0xFC);
                roleLabel = m.Kind == ConversationMessageKind.QuickAction
                    ? $"You · {m.Label}"
                    : "You";
            }
            else if (isOther)
            {
                bubbleColor = Color.FromRgb(0xE6, 0xE4, 0xEF);
                borderColor = Color.FromRgb(0xCF, 0xCB, 0xDE);
                textColor = Color.FromRgb(0x33, 0x33, 0x33);
                roleLabel = "Other";
            }
            else // AI
            {
                bubbleColor = Color.FromRgb(0xEC, 0xFD, 0xF5);
                borderColor = Color.FromRgb(0xA7, 0xF3, 0xD0);
                textColor = Color.FromRgb(0x14, 0x53, 0x2D);
                roleLabel = "Assistant";
            }

            var role = new TextBlock
            {
                Text = roleLabel + "  ·  " + m.Timestamp.ToString("h:mm tt"),
                FontSize = 10,
                Foreground = new SolidColorBrush(Color.FromRgb(0x8A, 0x83, 0x94)),
                Margin = isUser ? new Thickness(0, 0, 4, 3) : new Thickness(4, 0, 0, 3),
                HorizontalAlignment = isUser ? HorizontalAlignment.Right : HorizontalAlignment.Left,
                FontFamily = new FontFamily("Segoe UI"),
            };

            var text = new TextBlock
            {
                Text = m.Text,
                Foreground = new SolidColorBrush(textColor),
                FontSize = 13,
                LineHeight = 18,
                TextWrapping = TextWrapping.Wrap,
                FontFamily = new FontFamily("Segoe UI"),
            };

            var bubble = new Border
            {
                Background = new SolidColorBrush(bubbleColor),
                BorderBrush = new SolidColorBrush(borderColor),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(12),
                Padding = new Thickness(12, 8, 12, 8),
                MaxWidth = 560,
                HorizontalAlignment = isUser ? HorizontalAlignment.Right : HorizontalAlignment.Left,
                Child = text,
            };

            var row = new StackPanel
            {
                Margin = new Thickness(0, 4, 0, 8),
                HorizontalAlignment = isUser ? HorizontalAlignment.Right : HorizontalAlignment.Left,
            };
            row.Children.Add(role);
            row.Children.Add(bubble);
            return row;
        }

        private static string FormatDuration(TimeSpan t)
        {
            if (t.TotalSeconds < 60) return $"{(int)t.TotalSeconds}s";
            if (t.TotalMinutes < 60) return $"{(int)t.TotalMinutes}m";
            return $"{(int)t.TotalHours}h {t.Minutes}m";
        }

        private async Task BackfillTitlesAsync()
        {
            try
            {
                var missing = loadedConversations
                    .Where(s => string.IsNullOrWhiteSpace(s.Title) && s.Messages.Count > 0)
                    .ToList();
                if (missing.Count == 0) return;

                foreach (var s in missing)
                {
                    string title = await titleService.GenerateAsync(s).ConfigureAwait(false);
                    if (string.IsNullOrWhiteSpace(title)) continue;
                    s.Title = title;
                    ConversationHistoryService.UpdateSavedSessionTitle(s.SessionId, title);

                    await Dispatcher.BeginInvoke(new Action(() =>
                    {
                        // If this session is currently rendered in the list, update its title block.
                        foreach (var child in ConversationsList.Children)
                        {
                            if (child is Border b && b.Tag is ConversationSession bs && bs.SessionId == s.SessionId)
                            {
                                if (b.Child is StackPanel sp && sp.Children.Count > 0 && sp.Children[0] is TextBlock tb)
                                    tb.Text = title;
                            }
                        }
                        if (selectedConversation != null && selectedConversation.SessionId == s.SessionId)
                            DetailTitle.Text = title;
                    }));
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Dashboard] Backfill titles failed: {ex.Message}");
            }
        }

        #endregion
    }
}

