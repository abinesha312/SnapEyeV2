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
        private ProfileService? profileService;
        private readonly System.Collections.ObjectModel.ObservableCollection<ExperienceRow> experienceRows = new();
        private bool experienceListBound;

        /// <summary>Set by the overlay's "Experience…" menu item to open the editor on return.</summary>
        public static bool RequestExperienceView { get; set; }
        private SessionData? currentSession;
        private List<ConversationSession> loadedConversations = new();
        private ConversationSession? selectedConversation;
        private Border? selectedListRow;
        // True once this Dashboard window has been closed (or the app is shutting down).
        // Guards the overlay's Closed handler from calling Show() on a closed window,
        // which throws InvalidOperationException and crashes the process on exit.
        private bool isClosed;

        public Dashboard()
        {
            InitializeComponent();
            authService = new AuthService(Config.AppConfig.BackendHttpUrl);
            historyService = new ConversationHistoryService();
            titleService = new ConversationTitleService(Config.AppConfig.BackendHttpUrl);

            authService.ErrorOccurred += OnAuthError;
            Loaded += Dashboard_Loaded;
            Closed += Dashboard_Closed;

            CheckExistingSession();
        }

        /// <summary>
        /// Apply screen-capture invisibility once the HWND exists. We do this in Loaded
        /// (rather than the ctor) because <see cref="System.Windows.Interop.WindowInteropHelper"/>
        /// needs the window to have been shown / measured at least once for its handle
        /// to be reliable. Also hooks every Popup/ContextMenu/ComboBox dropdown so they
        /// inherit the same invisibility.
        /// </summary>
        private void Dashboard_Loaded(object? sender, RoutedEventArgs e)
        {
            try
            {
                if (Config.AppConfig.InvisibleToCapture)
                    CaptureInvisibility.ApplyToWindow(this);

                CaptureInvisibility.HookAllPopups(this);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Dashboard] Capture-invisibility error: {ex.Message}");
            }
        }

        private void Dashboard_Closed(object? sender, EventArgs e)
        {
            isClosed = true;
            try
            {
                Loaded -= Dashboard_Loaded;
                authService.ErrorOccurred -= OnAuthError;
                authService.Dispose();
                titleService.Dispose();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Dashboard] Dispose error: {ex.Message}");
            }
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
            if (e.ChangedButton != MouseButton.Left) return;

            // Double-click the header bar to toggle maximize/restore, matching the standard
            // Windows chrome behavior even though we're running with WindowStyle=None.
            if (e.ClickCount == 2)
            {
                ToggleMaximizeRestore();
                return;
            }

            // DragMove throws if the window is maximized; guard against it.
            if (this.WindowState == WindowState.Normal)
                this.DragMove();
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            Application.Current.Shutdown();
        }

        private void MaximizeButton_Click(object sender, RoutedEventArgs e)
        {
            ToggleMaximizeRestore();
        }

        /// <summary>Remembered bounds so Restore can return to the pre-maximize rectangle.</summary>
        private (double left, double top, double width, double height)? preMaxBounds;

        /// <summary>
        /// Borderless-window-friendly maximize: resizes to the current screen's WorkArea so
        /// the taskbar stays visible, and restores to the user's previous bounds on toggle.
        /// Using <see cref="WindowState.Maximized"/> on a <c>AllowsTransparency=True</c> window
        /// covers the taskbar, which we don't want.
        /// </summary>
        private void ToggleMaximizeRestore()
        {
            if (preMaxBounds != null)
            {
                // Restore
                var b = preMaxBounds.Value;
                this.Left = b.left;
                this.Top = b.top;
                this.Width = b.width;
                this.Height = b.height;
                preMaxBounds = null;
            }
            else
            {
                preMaxBounds = (this.Left, this.Top, this.Width, this.Height);
                var wa = System.Windows.SystemParameters.WorkArea;
                this.Left = wa.Left;
                this.Top = wa.Top;
                this.Width = wa.Width;
                this.Height = wa.Height;
            }
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

        private async void LaunchButton_Click(object sender, RoutedEventArgs e)
        {
            await LaunchMainApplication();
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
        private async Task LaunchMainApplication()
        {
            try
            {
                if (currentSession == null)
                {
                    ShowError("No active session. Please sign in first.");
                    return;
                }

                // Verify session is still valid (refresh automatically if the JWT expired).
                if (DateTime.Now > currentSession.ExpiryTime)
                {
                    await TryRefreshToken();
                    if (DateTime.Now > currentSession.ExpiryTime)
                    {
                        ShowError("Session expired. Please sign in again.");
                        ShowLoginPanel();
                        return;
                    }
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
                    // The overlay closes both when the user returns to the dashboard AND
                    // when the whole app is shutting down (Quit). In the shutdown case the
                    // dashboard is already closed, so calling Show() would throw
                    // "Cannot ... after a Window has closed" and crash the process.
                    if (isClosed || Application.Current?.Dispatcher?.HasShutdownStarted == true)
                        return;
                    try
                    {
                        this.Show();          // bring the dashboard back
                        CheckExistingSession(); // reload session status

                        // The overlay's "Experience…" menu item asks us to jump straight
                        // to the Experience editor when it returns focus to the dashboard.
                        if (RequestExperienceView)
                        {
                            RequestExperienceView = false;
                            try { if (NavExperience != null) NavExperience.IsChecked = true; }
                            catch { /* nav not ready */ }
                        }
                    }
                    catch (InvalidOperationException ex)
                    {
                        // Window already closed / app exiting — nothing to restore.
                        Console.WriteLine($"[Dashboard] Skipped re-show on overlay close: {ex.Message}");
                    }
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
            SidebarUsername.ToolTip = currentSession.Username;
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

        private void CollapseAllViews()
        {
            if (LaunchView != null) LaunchView.Visibility = Visibility.Collapsed;
            if (ConversationsView != null) ConversationsView.Visibility = Visibility.Collapsed;
            if (PromptsView != null) PromptsView.Visibility = Visibility.Collapsed;
            if (AiModelsView != null) AiModelsView.Visibility = Visibility.Collapsed;
            if (ShortcutsView != null) ShortcutsView.Visibility = Visibility.Collapsed;
            if (ExperienceView != null) ExperienceView.Visibility = Visibility.Collapsed;
        }

        private void ShowLaunchView()
        {
            CollapseAllViews();
            if (LaunchView != null) LaunchView.Visibility = Visibility.Visible;
        }

        private void ShowConversationsView()
        {
            CollapseAllViews();
            if (ConversationsView != null) ConversationsView.Visibility = Visibility.Visible;
        }

        private void ShowPromptsView()
        {
            CollapseAllViews();
            if (PromptsView != null) PromptsView.Visibility = Visibility.Visible;
        }

        private void ShowAiModelsView()
        {
            CollapseAllViews();
            if (AiModelsView != null) AiModelsView.Visibility = Visibility.Visible;
        }

        private void ShowShortcutsView()
        {
            CollapseAllViews();
            if (ShortcutsView != null) ShortcutsView.Visibility = Visibility.Visible;

            if (ShortcutsItemsList != null && ShortcutsItemsList.ItemsSource == null)
                ShortcutsItemsList.ItemsSource = AppKeyboardShortcuts.All;
            if (ShortcutsPrivacyNote != null)
                ShortcutsPrivacyNote.Text = AppKeyboardShortcuts.ScreenCapturePrivacyNote;
        }

        private void ShowExperienceView()
        {
            CollapseAllViews();
            if (ExperienceView != null) ExperienceView.Visibility = Visibility.Visible;
        }

        private void NavShortcuts_Checked(object sender, RoutedEventArgs e)
        {
            if (!IsLoaded) return;
            ShowShortcutsView();
        }

        private void NavExperience_Checked(object sender, RoutedEventArgs e)
        {
            if (!IsLoaded) return;
            ShowExperienceView();
            _ = LoadExperiencesAsync();
        }

        #region Experience

        private ProfileService? EnsureProfileService()
        {
            if (currentSession == null || string.IsNullOrEmpty(currentSession.AccessToken))
                return null;
            if (profileService == null)
                profileService = new ProfileService(Config.AppConfig.BackendHttpUrl);
            profileService.SetAuthToken(currentSession.AccessToken);
            return profileService;
        }

        private async Task LoadExperiencesAsync()
        {
            if (!experienceListBound && ExperienceList != null)
            {
                ExperienceList.ItemsSource = experienceRows;
                experienceListBound = true;
            }

            var svc = EnsureProfileService();
            if (svc == null) return;

            try
            {
                var entries = await svc.ListAsync();
                experienceRows.Clear();
                foreach (var entry in entries)
                    experienceRows.Add(new ExperienceRow(entry));
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Dashboard] Load experiences error: {ex.Message}");
            }
            finally
            {
                UpdateExperienceEmpty();
            }
        }

        private void UpdateExperienceEmpty()
        {
            if (ExperienceEmpty != null)
                ExperienceEmpty.Visibility = experienceRows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        private void AddExperience_Click(object sender, RoutedEventArgs e)
        {
            experienceRows.Insert(0, new ExperienceRow(new ExperienceEntry { EntryType = "work" }));
            UpdateExperienceEmpty();
        }

        private void AddEducation_Click(object sender, RoutedEventArgs e)
        {
            experienceRows.Insert(0, new ExperienceRow(new ExperienceEntry
            {
                EntryType = "education",
                Company = "University of North Texas",
            }));
            UpdateExperienceEmpty();
        }

        private async void SaveExperience_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not FrameworkElement fe || fe.Tag is not ExperienceRow row)
                return;

            var svc = EnsureProfileService();
            if (svc == null)
            {
                row.StatusText = "Not signed in";
                return;
            }

            row.StatusText = "Saving...";
            try
            {
                var entry = row.ToEntry();
                var saved = string.IsNullOrEmpty(entry.EntryId)
                    ? await svc.AddAsync(entry)
                    : await svc.UpdateAsync(entry);

                if (saved != null)
                {
                    row.ApplySaved(saved);
                    row.StatusText = "Saved";
                }
                else
                {
                    row.StatusText = "Save failed (check a company, role, or summary)";
                }
            }
            catch (Exception ex)
            {
                row.StatusText = $"Save failed: {ex.Message}";
            }
        }

        private async void DeleteExperience_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not FrameworkElement fe || fe.Tag is not ExperienceRow row)
                return;

            var svc = EnsureProfileService();
            try
            {
                if (svc != null && !string.IsNullOrEmpty(row.EntryId))
                    await svc.DeleteAsync(row.EntryId);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Dashboard] Delete experience error: {ex.Message}");
            }
            finally
            {
                experienceRows.Remove(row);
                UpdateExperienceEmpty();
            }
        }

        /// <summary>Editable row view-model bound to the Experience list.</summary>
        public class ExperienceRow : System.ComponentModel.INotifyPropertyChanged
        {
            public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
            private void Raise(string name) =>
                PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(name));

            public string EntryId { get; private set; }
            public string EntryType { get; }

            private string company;
            private string role;
            private string startDate;
            private string endDate;
            private string summary;
            private string statusText = "";

            public ExperienceRow(ExperienceEntry entry)
            {
                EntryId = entry.EntryId ?? "";
                EntryType = string.IsNullOrEmpty(entry.EntryType) ? "work" : entry.EntryType;
                company = entry.Company ?? "";
                role = entry.Role ?? "";
                startDate = entry.StartDate ?? "";
                endDate = entry.EndDate ?? "";
                summary = entry.Summary ?? "";
            }

            public string Company { get => company; set { company = value; Raise(nameof(Company)); } }
            public string Role { get => role; set { role = value; Raise(nameof(Role)); } }
            public string StartDate { get => startDate; set { startDate = value; Raise(nameof(StartDate)); } }
            public string EndDate { get => endDate; set { endDate = value; Raise(nameof(EndDate)); } }
            public string Summary { get => summary; set { summary = value; Raise(nameof(Summary)); } }
            public string StatusText { get => statusText; set { statusText = value; Raise(nameof(StatusText)); } }

            public bool IsEducation => EntryType == "education";
            public string TypeLabel => IsEducation ? "EDUCATION" : "EXPERIENCE";
            public string CompanyLabel => IsEducation ? "School / University" : "Company";
            public string RoleLabel => IsEducation ? "Degree / Program" : "Role / Title";
            public string SummaryLabel => IsEducation
                ? "What you studied / built / achieved"
                : "What you did (challenge, workflow, architecture, impact)";

            public ExperienceEntry ToEntry() => new ExperienceEntry
            {
                EntryId = EntryId,
                EntryType = EntryType,
                Company = Company,
                Role = Role,
                StartDate = StartDate,
                EndDate = EndDate,
                Summary = Summary,
            };

            public void ApplySaved(ExperienceEntry saved)
            {
                if (!string.IsNullOrEmpty(saved.EntryId))
                    EntryId = saved.EntryId;
            }
        }

        #endregion

        private void RefreshConversations_Click(object sender, RoutedEventArgs e) => LoadConversations();

        #endregion

        #region Conversations

        private async void LoadConversations()
        {
            try
            {
                ConversationsSubtitle.Text = "Loading…";
                ConversationsList.Children.Clear();
                ConversationsEmpty.Visibility = Visibility.Collapsed;
                ClearDetail();

                // Read JSON files off the UI thread so opening the Conversations tab
                // never stalls the dashboard, even with hundreds of saved sessions.
                var loaded = await historyService.ListSavedSessionsAsync().ConfigureAwait(true);
                loadedConversations = loaded.ToList();

                if (loadedConversations.Count == 0)
                {
                    ConversationsEmpty.Visibility = Visibility.Visible;
                    ConversationsSubtitle.Text = "No saved conversations yet.";
                    return;
                }

                ConversationsEmpty.Visibility = Visibility.Collapsed;
                ConversationsSubtitle.Text =
                    $"{loadedConversations.Count} saved conversation{(loadedConversations.Count == 1 ? "" : "s")}.";

                foreach (var s in loadedConversations)
                    ConversationsList.Children.Add(BuildListRow(s));

                if (ConversationsList.Children.Count > 0 &&
                    ConversationsList.Children[0] is Border firstRow)
                    SelectConversation(loadedConversations[0], firstRow);

                _ = BackfillTitlesAsync();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Dashboard] LoadConversations error: {ex.Message}");
                ConversationsSubtitle.Text = "Failed to load saved conversations.";
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
                    // File I/O off the UI thread so the sidebar stays responsive during backfill.
                    await Task.Run(() => ConversationHistoryService.UpdateSavedSessionTitle(s.SessionId, title))
                              .ConfigureAwait(false);

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

        #region Prompts (custom system prompts per mode)

        private readonly System.Collections.ObjectModel.ObservableCollection<PromptRowVM> promptRows = new();

        private void NavPrompts_Checked(object sender, RoutedEventArgs e)
        {
            if (!IsLoaded) return;
            ShowPromptsView();
            LoadPromptRows();
        }

        /// <summary>
        /// Populates <see cref="promptRows"/> from the user's prompts.json (creating/seeding the
        /// file on first run) and binds it to the accordion ItemsControl.
        /// </summary>
        private void LoadPromptRows()
        {
            try
            {
                if (PromptsList == null) return;

                // Ensure the in-memory AppConfig.Modes reflects the latest user file so the
                // overlay picks up the same list we're about to show.
                Config.AppConfig.RefreshModesFromUserFile();

                var modes = Config.AppConfig.Modes
                    .OrderBy(m => m.Order)
                    .ThenBy(m => m.Name, StringComparer.OrdinalIgnoreCase)
                    .ToList();

                promptRows.Clear();
                foreach (var mode in modes)
                {
                    promptRows.Add(PromptRowVM.FromModeConfig(mode));
                }

                PromptsList.ItemsSource = promptRows;

                if (PromptsEmpty != null)
                    PromptsEmpty.Visibility = promptRows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

                // Surface parse errors (if any) via the inline banner.
                if (PromptsService.LastLoadFailed && PromptsErrorBanner != null && PromptsErrorText != null)
                {
                    PromptsErrorText.Text = $"Could not read prompts.json: {PromptsService.LastLoadError}. Showing built-in defaults instead.";
                    PromptsErrorBanner.Visibility = Visibility.Visible;
                }
                else if (PromptsErrorBanner != null)
                {
                    PromptsErrorBanner.Visibility = Visibility.Collapsed;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Dashboard] LoadPromptRows failed: {ex.Message}");
            }
        }

        private PromptRowVM? FindRow(object? sender)
        {
            if (sender is FrameworkElement fe && fe.Tag is string id)
                return promptRows.FirstOrDefault(r => r.Id == id);
            return null;
        }

        private void PromptRowExpand_Click(object sender, RoutedEventArgs e)
        {
            var row = FindRow(sender);
            if (row == null) return;
            // Auto-collapse other rows so the user only sees one editor at a time (accordion UX).
            bool wasExpanded = row.IsExpanded;
            foreach (var r in promptRows) r.IsExpanded = false;
            row.IsExpanded = !wasExpanded;
        }

        private void PromptRowSave_Click(object sender, RoutedEventArgs e)
        {
            var row = FindRow(sender);
            if (row == null) return;

            if (string.IsNullOrWhiteSpace(row.Name))
            {
                row.StatusText = "Name cannot be empty.";
                return;
            }
            if (string.IsNullOrWhiteSpace(row.SystemPrompt))
            {
                row.StatusText = "System prompt cannot be empty.";
                return;
            }

            try
            {
                PersistPromptRows();
                row.OriginalSystemPrompt = row.SystemPrompt;
                row.OriginalName = row.Name;
                row.OriginalIcon = row.Icon;
                row.StatusText = "Saved.";
                // Clear the status after a short delay so it doesn't linger forever.
                ScheduleClearStatus(row);
            }
            catch (Exception ex)
            {
                row.StatusText = $"Save failed: {ex.Message}";
            }
        }

        private void PromptRowReset_Click(object sender, RoutedEventArgs e)
        {
            var row = FindRow(sender);
            if (row == null) return;
            var builtIn = PromptsService.GetBuiltInById(row.Id);
            if (builtIn == null)
            {
                row.StatusText = "This mode has no built-in default.";
                return;
            }
            row.Name = builtIn.Name;
            row.Icon = builtIn.Icon;
            row.SystemPrompt = builtIn.SystemPrompt;
            row.OriginalName = builtIn.Name;
            row.OriginalIcon = builtIn.Icon;
            row.OriginalSystemPrompt = builtIn.SystemPrompt;
            try
            {
                PersistPromptRows();
                row.StatusText = "Reset to default.";
                ScheduleClearStatus(row);
            }
            catch (Exception ex)
            {
                row.StatusText = $"Reset failed: {ex.Message}";
            }
        }

        private void PromptRowDelete_Click(object sender, RoutedEventArgs e)
        {
            var row = FindRow(sender);
            if (row == null) return;
            var confirm = MessageBox.Show(
                this,
                $"Delete mode '{row.Name}'? This removes it from your mode selector.",
                "Delete mode",
                MessageBoxButton.OKCancel,
                MessageBoxImage.Warning);
            if (confirm != MessageBoxResult.OK) return;

            promptRows.Remove(row);
            NormalizeOrder();
            PersistPromptRows();

            if (promptRows.Count == 0 && PromptsEmpty != null)
                PromptsEmpty.Visibility = Visibility.Visible;
        }

        private void PromptRowUp_Click(object sender, RoutedEventArgs e)
        {
            var row = FindRow(sender);
            if (row == null) return;
            int idx = promptRows.IndexOf(row);
            if (idx <= 0) return;
            promptRows.Move(idx, idx - 1);
            NormalizeOrder();
            PersistPromptRows();
        }

        private void PromptRowDown_Click(object sender, RoutedEventArgs e)
        {
            var row = FindRow(sender);
            if (row == null) return;
            int idx = promptRows.IndexOf(row);
            if (idx < 0 || idx >= promptRows.Count - 1) return;
            promptRows.Move(idx, idx + 1);
            NormalizeOrder();
            PersistPromptRows();
        }

        private void AddMode_Click(object sender, RoutedEventArgs e)
        {
            var newMode = new Config.ModeConfig
            {
                Id = Guid.NewGuid().ToString("N"),
                Name = "New Mode",
                Icon = "brain",
                SystemPrompt = "",
                Order = promptRows.Count,
            };
            var vm = PromptRowVM.FromModeConfig(newMode);
            // Collapse others so the new row is the only one expanded.
            foreach (var r in promptRows) r.IsExpanded = false;
            vm.IsExpanded = true;
            promptRows.Add(vm);
            NormalizeOrder();
            PersistPromptRows();
            if (PromptsEmpty != null) PromptsEmpty.Visibility = Visibility.Collapsed;
        }

        private void ResetAllDefaults_Click(object sender, RoutedEventArgs e)
        {
            var confirm = MessageBox.Show(
                this,
                "Reset ALL modes to built-in defaults? Your custom prompts will be lost.",
                "Reset all prompts",
                MessageBoxButton.OKCancel,
                MessageBoxImage.Warning);
            if (confirm != MessageBoxResult.OK) return;

            try
            {
                PromptsService.ResetToDefaults();
                Config.AppConfig.RefreshModesFromUserFile();
                LoadPromptRows();
            }
            catch (Exception ex)
            {
                if (PromptsErrorBanner != null && PromptsErrorText != null)
                {
                    PromptsErrorText.Text = $"Reset failed: {ex.Message}";
                    PromptsErrorBanner.Visibility = Visibility.Visible;
                }
            }
        }

        private void NormalizeOrder()
        {
            for (int i = 0; i < promptRows.Count; i++)
                promptRows[i].Order = i;
        }

        private void PersistPromptRows()
        {
            var modes = promptRows.Select(r => r.ToModeConfig()).ToList();
            PromptsService.Save(modes);
            // Update the in-memory copy so the overlay (launched next) sees the saved prompts.
            Config.AppConfig.RefreshModesFromUserFile();
        }

        private void ScheduleClearStatus(PromptRowVM row)
        {
            var timer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(2),
            };
            timer.Tick += (_, __) =>
            {
                timer.Stop();
                row.StatusText = "";
            };
            timer.Start();
        }

        #endregion

        #region AI Models (provider + model + API key)

        /// <summary>Currently selected provider id in the AI Models view (not necessarily saved yet).</summary>
        private string? aiSelectedProviderId;

        /// <summary>
        /// Tracks whether the current UI state differs from what's on disk. Save button is
        /// enabled only when dirty AND the current inputs are valid.
        /// </summary>
        private bool aiIsDirty;

        /// <summary>
        /// Guards reentrancy when we programmatically sync the PasswordBox and TextBox siblings
        /// via the show/hide toggle so we don't mark the form dirty from our own writes.
        /// </summary>
        private bool aiSyncingKeyBoxes;

        private void NavAiModels_Checked(object sender, RoutedEventArgs e)
        {
            if (!IsLoaded) return;
            ShowAiModelsView();
            InitializeAiModelsView();
        }

        /// <summary>
        /// Builds the provider cards (once) and loads the saved selection from disk into the UI.
        /// </summary>
        private void InitializeAiModelsView()
        {
            try
            {
                BuildProviderCards();
                var settings = AiModelsService.Load();
                string activeId = !string.IsNullOrWhiteSpace(settings.ActiveProvider)
                    ? settings.ActiveProvider
                    : Models.AiModelsCatalogue.Providers[0].Id;
                SelectProvider(activeId, persistDirty: false);
                SetAiStatusPill(AiStatusPillKind.NotTested, null);
                aiIsDirty = false;
                UpdateSaveButtonState();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Dashboard] InitializeAiModelsView failed: {ex.Message}");
                ShowAiInlineError(ex.Message);
            }
        }

        private void BuildProviderCards()
        {
            if (ProviderCardsGrid == null) return;
            if (ProviderCardsGrid.Children.Count > 0) return; // only build once

            foreach (var provider in Models.AiModelsCatalogue.Providers)
            {
                ProviderCardsGrid.Children.Add(BuildProviderCard(provider));
            }
        }

        private FrameworkElement BuildProviderCard(Models.AiModelsCatalogue.ProviderEntry provider)
        {
            // Each card is a plain Button with a custom template so selection state is a
            // single boolean (Tag == aiSelectedProviderId) and hover/selected visuals are
            // driven by data triggers rather than ToggleButton's chrome.
            var button = new Button
            {
                Tag = provider.Id,
                Cursor = Cursors.Hand,
                Margin = new Thickness(0, 0, 10, 0),
                Padding = new Thickness(14),
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
            };
            button.Click += ProviderCard_Click;

            var border = new Border
            {
                Background = Brushes.White,
                BorderBrush = (Brush)new BrushConverter().ConvertFrom("#C5DFF8")!,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(12),
                Padding = new Thickness(14),
            };

            var layout = new StackPanel { Orientation = Orientation.Vertical };

            // Monogram chip
            var chip = new Border
            {
                Width = 36,
                Height = 36,
                CornerRadius = new CornerRadius(10),
                Background = (Brush)new BrushConverter().ConvertFrom(provider.AccentHex)!,
                HorizontalAlignment = HorizontalAlignment.Left,
            };
            chip.Child = new TextBlock
            {
                Text = provider.Monogram,
                Foreground = Brushes.White,
                FontSize = 16,
                FontWeight = FontWeights.Bold,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };

            layout.Children.Add(chip);
            layout.Children.Add(new TextBlock
            {
                Text = provider.DisplayName,
                FontSize = 14,
                FontWeight = FontWeights.SemiBold,
                Foreground = (Brush)new BrushConverter().ConvertFrom("#333333")!,
                Margin = new Thickness(0, 10, 0, 0),
            });
            layout.Children.Add(new TextBlock
            {
                Text = provider.Tagline,
                FontSize = 11,
                Foreground = (Brush)new BrushConverter().ConvertFrom("#888888")!,
                Margin = new Thickness(0, 2, 0, 0),
            });

            border.Child = layout;
            button.Content = border;
            button.Template = BuildProviderCardTemplate(provider.AccentHex);
            return button;
        }

        private ControlTemplate BuildProviderCardTemplate(string accentHex)
        {
            // A minimal template that just shows the Content (our Border). Selection is applied
            // imperatively by ApplyProviderCardSelection since setting template triggers from
            // an external bool is awkward for a transient selection model.
            var template = new ControlTemplate(typeof(Button));
            var root = new FrameworkElementFactory(typeof(ContentPresenter));
            template.VisualTree = root;
            return template;
        }

        /// <summary>
        /// Visually highlights the selected provider card and dims the others. Called whenever
        /// <see cref="aiSelectedProviderId"/> changes.
        /// </summary>
        private void ApplyProviderCardSelection()
        {
            if (ProviderCardsGrid == null) return;
            foreach (var child in ProviderCardsGrid.Children)
            {
                if (child is not Button btn || btn.Content is not Border border) continue;
                string id = btn.Tag as string ?? "";
                bool isSelected = id == aiSelectedProviderId;
                var entry = Models.AiModelsCatalogue.FindById(id);
                var accent = entry != null
                    ? (Brush)new BrushConverter().ConvertFrom(entry.AccentHex)!
                    : (Brush)new BrushConverter().ConvertFrom("#3A7BFF")!;

                border.BorderBrush = isSelected ? accent : (Brush)new BrushConverter().ConvertFrom("#C5DFF8")!;
                border.BorderThickness = new Thickness(isSelected ? 2 : 1);
                border.Effect = isSelected
                    ? new System.Windows.Media.Effects.DropShadowEffect
                    {
                        Color = ((SolidColorBrush)accent).Color,
                        BlurRadius = 18,
                        ShadowDepth = 0,
                        Opacity = 0.35,
                    }
                    : null;
                border.Background = isSelected
                    ? (Brush)new BrushConverter().ConvertFrom("#FFFFFF")!
                    : (Brush)new BrushConverter().ConvertFrom("#FAFAFC")!;
            }
        }

        private void ProviderCard_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button btn) return;
            string? id = btn.Tag as string;
            if (string.IsNullOrEmpty(id)) return;
            SelectProvider(id, persistDirty: true);
        }

        /// <summary>
        /// Switches the UI to a given provider: refreshes the model dropdown, pre-fills the key
        /// from storage (if any), and updates dirty state.
        /// </summary>
        private void SelectProvider(string providerId, bool persistDirty)
        {
            var entry = Models.AiModelsCatalogue.FindById(providerId);
            if (entry == null) return;

            aiSelectedProviderId = providerId;
            ApplyProviderCardSelection();

            // Populate model dropdown
            if (AiModelCombo != null)
            {
                AiModelCombo.Items.Clear();
                foreach (var m in entry.Models) AiModelCombo.Items.Add(m);
            }

            // Endpoint label
            if (AiEndpointText != null) AiEndpointText.Text = entry.EndpointUrl;

            // Pre-fill model + key from stored settings for THIS provider
            var settings = AiModelsService.Load();
            string? savedModel = null;
            string? savedKey = null;
            if (settings.Selections.TryGetValue(providerId, out var sel))
            {
                savedModel = sel.Model;
                savedKey = AiModelsService.UnprotectApiKey(sel.ApiKeyProtected);
            }

            string modelToSelect = !string.IsNullOrWhiteSpace(savedModel) && entry.Models.Contains(savedModel!)
                ? savedModel!
                : entry.Models[0];
            if (AiModelCombo != null) AiModelCombo.SelectedItem = modelToSelect;

            aiSyncingKeyBoxes = true;
            if (AiApiKeyBox != null) AiApiKeyBox.Password = savedKey ?? "";
            if (AiApiKeyVisibleBox != null) AiApiKeyVisibleBox.Text = savedKey ?? "";
            aiSyncingKeyBoxes = false;

            SetAiStatusPill(AiStatusPillKind.NotTested, null);
            ShowAiInlineError(null);

            if (persistDirty) aiIsDirty = true;
            UpdateSaveButtonState();
        }

        private void AiModelCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!IsLoaded) return;
            aiIsDirty = true;
            SetAiStatusPill(AiStatusPillKind.NotTested, null);
            UpdateSaveButtonState();
        }

        private void AiApiKey_Changed(object sender, RoutedEventArgs e)
        {
            if (aiSyncingKeyBoxes) return;
            aiSyncingKeyBoxes = true;
            if (AiApiKeyVisibleBox != null && AiApiKeyBox != null)
                AiApiKeyVisibleBox.Text = AiApiKeyBox.Password;
            aiSyncingKeyBoxes = false;
            aiIsDirty = true;
            SetAiStatusPill(AiStatusPillKind.NotTested, null);
            UpdateSaveButtonState();
        }

        private void AiApiKeyVisible_Changed(object sender, TextChangedEventArgs e)
        {
            if (aiSyncingKeyBoxes) return;
            aiSyncingKeyBoxes = true;
            if (AiApiKeyVisibleBox != null && AiApiKeyBox != null)
                AiApiKeyBox.Password = AiApiKeyVisibleBox.Text;
            aiSyncingKeyBoxes = false;
            aiIsDirty = true;
            SetAiStatusPill(AiStatusPillKind.NotTested, null);
            UpdateSaveButtonState();
        }

        private void TogglePasswordVisibility_Click(object sender, RoutedEventArgs e)
        {
            if (AiApiKeyBox == null || AiApiKeyVisibleBox == null) return;
            if (AiApiKeyBox.Visibility == Visibility.Visible)
            {
                aiSyncingKeyBoxes = true;
                AiApiKeyVisibleBox.Text = AiApiKeyBox.Password;
                aiSyncingKeyBoxes = false;
                AiApiKeyBox.Visibility = Visibility.Collapsed;
                AiApiKeyVisibleBox.Visibility = Visibility.Visible;
                AiApiKeyVisibleBox.Focus();
            }
            else
            {
                aiSyncingKeyBoxes = true;
                AiApiKeyBox.Password = AiApiKeyVisibleBox.Text;
                aiSyncingKeyBoxes = false;
                AiApiKeyVisibleBox.Visibility = Visibility.Collapsed;
                AiApiKeyBox.Visibility = Visibility.Visible;
                AiApiKeyBox.Focus();
            }
        }

        private async void TestConnection_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(aiSelectedProviderId))
            {
                ShowAiInlineError("Select a provider first.");
                return;
            }
            string? model = AiModelCombo?.SelectedItem as string;
            string apiKey = GetCurrentApiKeyText();
            if (string.IsNullOrWhiteSpace(model))
            {
                ShowAiInlineError("Pick a model.");
                return;
            }
            if (string.IsNullOrWhiteSpace(apiKey))
            {
                ShowAiInlineError("Paste your API key first.");
                return;
            }

            ShowAiInlineError(null);
            SetAiStatusPill(AiStatusPillKind.Testing, null);
            if (TestConnectionBtn != null) TestConnectionBtn.IsEnabled = false;

            try
            {
                var result = await AiModelsService.TestConnectionAsync(
                    Config.AppConfig.BackendHttpUrl,
                    aiSelectedProviderId,
                    model!,
                    apiKey,
                    currentSession?.AccessToken);

                if (result.Ok)
                {
                    SetAiStatusPill(AiStatusPillKind.Live, $"LIVE · {result.LatencyMs} ms");
                }
                else
                {
                    SetAiStatusPill(AiStatusPillKind.Failed, "FAILED");
                    ShowAiInlineError(result.Error ?? "Unknown error");
                }
            }
            catch (Exception ex)
            {
                SetAiStatusPill(AiStatusPillKind.Failed, "FAILED");
                ShowAiInlineError(ex.Message);
            }
            finally
            {
                if (TestConnectionBtn != null) TestConnectionBtn.IsEnabled = true;
                UpdateSaveButtonState();
            }
        }

        private void SaveAiModels_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (string.IsNullOrEmpty(aiSelectedProviderId))
                {
                    ShowAiInlineError("Select a provider first.");
                    return;
                }
                string? model = AiModelCombo?.SelectedItem as string;
                string apiKey = GetCurrentApiKeyText();
                if (string.IsNullOrWhiteSpace(model))
                {
                    ShowAiInlineError("Pick a model.");
                    return;
                }

                var settings = AiModelsService.Load();
                settings.ActiveProvider = aiSelectedProviderId!;
                if (!settings.Selections.TryGetValue(aiSelectedProviderId!, out var sel))
                {
                    sel = new AiModelProviderChoice();
                    settings.Selections[aiSelectedProviderId!] = sel;
                }
                sel.Model = model!;
                // Only overwrite the stored key when the user actually typed something; an empty
                // input leaves any previously-saved key intact (common when the user just wants
                // to change the model).
                if (!string.IsNullOrEmpty(apiKey))
                    sel.ApiKeyProtected = AiModelsService.ProtectApiKey(apiKey);

                AiModelsService.Save(settings);

                aiIsDirty = false;
                ShowAiInlineError(null);
                UpdateSaveButtonState();

                // If the user hasn't tested, leave the pill alone. If they tested successfully,
                // upgrade the pill text to confirm persistence.
                if (AiStatusText?.Text?.StartsWith("LIVE") == true)
                    SetAiStatusPill(AiStatusPillKind.Live, "LIVE · saved");
                else
                    SetAiStatusPill(AiStatusPillKind.Saved, "SAVED");
            }
            catch (Exception ex)
            {
                ShowAiInlineError($"Save failed: {ex.Message}");
            }
        }

        private string GetCurrentApiKeyText()
        {
            if (AiApiKeyBox?.Visibility == Visibility.Visible)
                return AiApiKeyBox.Password ?? "";
            return AiApiKeyVisibleBox?.Text ?? "";
        }

        private void UpdateSaveButtonState()
        {
            if (SaveAiModelsBtn == null) return;
            bool hasProvider = !string.IsNullOrEmpty(aiSelectedProviderId);
            bool hasModel = AiModelCombo?.SelectedItem is string m && !string.IsNullOrWhiteSpace(m);
            bool keyPresent = !string.IsNullOrWhiteSpace(GetCurrentApiKeyText())
                              || HasStoredKeyForCurrentProvider();
            SaveAiModelsBtn.IsEnabled = aiIsDirty && hasProvider && hasModel && keyPresent;
        }

        private bool HasStoredKeyForCurrentProvider()
        {
            if (string.IsNullOrEmpty(aiSelectedProviderId)) return false;
            var settings = AiModelsService.Load();
            if (!settings.Selections.TryGetValue(aiSelectedProviderId!, out var sel)) return false;
            return !string.IsNullOrEmpty(sel.ApiKeyProtected);
        }

        private enum AiStatusPillKind { NotTested, Testing, Live, Failed, Saved }

        private void SetAiStatusPill(AiStatusPillKind kind, string? customText)
        {
            if (AiStatusPill == null || AiStatusDot == null || AiStatusText == null) return;
            BrushConverter bc = new();
            switch (kind)
            {
                case AiStatusPillKind.Live:
                    AiStatusDot.Fill = (Brush)bc.ConvertFrom("#16A34A")!;
                    AiStatusText.Text = customText ?? "LIVE";
                    AiStatusText.Foreground = (Brush)bc.ConvertFrom("#166534")!;
                    AiStatusPill.Background = (Brush)bc.ConvertFrom("#E6F7EB")!;
                    AiStatusPill.BorderBrush = (Brush)bc.ConvertFrom("#A7E3B9")!;
                    break;
                case AiStatusPillKind.Failed:
                    AiStatusDot.Fill = (Brush)bc.ConvertFrom("#B84848")!;
                    AiStatusText.Text = customText ?? "FAILED";
                    AiStatusText.Foreground = (Brush)bc.ConvertFrom("#9A3A3A")!;
                    AiStatusPill.Background = (Brush)bc.ConvertFrom("#FFF4F4")!;
                    AiStatusPill.BorderBrush = (Brush)bc.ConvertFrom("#F5B5B5")!;
                    break;
                case AiStatusPillKind.Testing:
                    AiStatusDot.Fill = (Brush)bc.ConvertFrom("#3A7BFF")!;
                    AiStatusText.Text = customText ?? "TESTING";
                    AiStatusText.Foreground = (Brush)bc.ConvertFrom("#2E5490")!;
                    AiStatusPill.Background = (Brush)bc.ConvertFrom("#E3F0FF")!;
                    AiStatusPill.BorderBrush = (Brush)bc.ConvertFrom("#A8C8EE")!;
                    break;
                case AiStatusPillKind.Saved:
                    AiStatusDot.Fill = (Brush)bc.ConvertFrom("#16A34A")!;
                    AiStatusText.Text = customText ?? "SAVED";
                    AiStatusText.Foreground = (Brush)bc.ConvertFrom("#166534")!;
                    AiStatusPill.Background = (Brush)bc.ConvertFrom("#E6F7EB")!;
                    AiStatusPill.BorderBrush = (Brush)bc.ConvertFrom("#A7E3B9")!;
                    break;
                default:
                    AiStatusDot.Fill = (Brush)bc.ConvertFrom("#8BA8C4")!;
                    AiStatusText.Text = customText ?? "NOT TESTED";
                    AiStatusText.Foreground = (Brush)bc.ConvertFrom("#6B6478")!;
                    AiStatusPill.Background = (Brush)bc.ConvertFrom("#EBF4FC")!;
                    AiStatusPill.BorderBrush = (Brush)bc.ConvertFrom("#A8C8EE")!;
                    break;
            }
        }

        private void ShowAiInlineError(string? message)
        {
            if (AiInlineError == null) return;
            AiInlineError.Text = message ?? "";
        }

        #endregion
    }

    /// <summary>
    /// View-model for a single row in the Prompts accordion. Exposes the editable fields from
    /// <see cref="Config.ModeConfig"/> plus UI-only state like <see cref="IsExpanded"/> and a
    /// transient "Saved." status message.
    /// </summary>
    public class PromptRowVM : System.ComponentModel.INotifyPropertyChanged
    {
        private string _name = "";
        private string _icon = "brain";
        private string _systemPrompt = "";
        private int _order;
        private bool _isExpanded;
        private string _statusText = "";

        public string Id { get; set; } = "";

        public string Name
        {
            get => _name;
            set { if (_name != value) { _name = value; Raise(nameof(Name)); Raise(nameof(IsDirty)); } }
        }

        public string Icon
        {
            get => _icon;
            set { if (_icon != value) { _icon = value; Raise(nameof(Icon)); Raise(nameof(IsDirty)); } }
        }

        public string SystemPrompt
        {
            get => _systemPrompt;
            set { if (_systemPrompt != value) { _systemPrompt = value; Raise(nameof(SystemPrompt)); Raise(nameof(IsDirty)); } }
        }

        public int Order
        {
            get => _order;
            set { if (_order != value) { _order = value; Raise(nameof(Order)); } }
        }

        public bool IsExpanded
        {
            get => _isExpanded;
            set { if (_isExpanded != value) { _isExpanded = value; Raise(nameof(IsExpanded)); } }
        }

        public string StatusText
        {
            get => _statusText;
            set { if (_statusText != value) { _statusText = value; Raise(nameof(StatusText)); } }
        }

        // Snapshot of the last-saved values; used to highlight unsaved edits and enable "Reset".
        public string OriginalName { get; set; } = "";
        public string OriginalIcon { get; set; } = "";
        public string OriginalSystemPrompt { get; set; } = "";

        /// <summary>True if any field differs from the last saved snapshot.</summary>
        public bool IsDirty =>
            !string.Equals(Name, OriginalName, StringComparison.Ordinal) ||
            !string.Equals(Icon, OriginalIcon, StringComparison.Ordinal) ||
            !string.Equals(SystemPrompt, OriginalSystemPrompt, StringComparison.Ordinal);

        /// <summary>True if this mode has a corresponding built-in default we can reset to.</summary>
        public bool HasBuiltInDefault => Services.PromptsService.GetBuiltInById(Id) != null;

        public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
        private void Raise(string prop) => PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(prop));

        public static PromptRowVM FromModeConfig(Config.ModeConfig m) => new()
        {
            Id = m.Id,
            Name = m.Name,
            Icon = string.IsNullOrWhiteSpace(m.Icon) ? "brain" : m.Icon,
            SystemPrompt = m.SystemPrompt ?? "",
            Order = m.Order,
            OriginalName = m.Name,
            OriginalIcon = string.IsNullOrWhiteSpace(m.Icon) ? "brain" : m.Icon,
            OriginalSystemPrompt = m.SystemPrompt ?? "",
        };

        public Config.ModeConfig ToModeConfig() => new()
        {
            Id = string.IsNullOrWhiteSpace(Id) ? Guid.NewGuid().ToString("N") : Id,
            Name = Name,
            Icon = string.IsNullOrWhiteSpace(Icon) ? "brain" : Icon,
            SystemPrompt = SystemPrompt ?? "",
            Order = Order,
        };
    }
}

