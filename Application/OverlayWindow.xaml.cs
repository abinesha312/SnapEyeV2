using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using SnapEye.Services;
using SnapEye.Models;
using SnapEye.Config;

namespace SnapEye
{
    public partial class OverlayWindow : Window
    {
        // Screen-share invisibility (WDA_EXCLUDEFROMCAPTURE) and global hotkey plumbing
        // live in dedicated helpers so we have a single source of truth and the popups
        // / Dashboard window can reuse them. See Application/services/CaptureInvisibility.cs
        // and Application/services/GlobalHotkeyService.cs.

        // Services
        private readonly AuthService authService;
        private readonly RealtimeTranscriptionService transcriptionService;
        private readonly AudioCaptureService audioCaptureService;
        private readonly SpeakerCaptureService speakerCaptureService;
        private readonly ScreenCaptureService screenCaptureService;
        private readonly StreamingResponseService streamingService;
        private readonly ConversationHistoryService history;
        private readonly ConversationSyncService conversationSync;
        private readonly ConversationTitleService titleService;
        private GlobalHotkeyService? hotkeyService;

        // Island Bar click-through (incognito) mode. When the toggle is OFF, clicks pass
        // through the overlay to the app behind it, except over the Island Bar button.
        private ClickThroughService? clickThroughService;

        // "Answer again" (#6): remembers the last question so the user can regenerate it
        // with a different explanation. lastAnswerWasTranscript routes the new answer to
        // the same view (Chat vs Transcript) the original was shown in.
        private string? lastAnsweredQuestion;
        private bool lastAnswerWasTranscript;

        // Microphone mute: when true, captured mic audio is NOT sent to the backend
        // (the backend keepalive keeps the mic stream open so unmute is instant).
        // Toggled by MicMuteBtn or the global Backtick+Space hook.
        // Default comes from AppConfig.MicMutedByDefault (audio.mic_muted_by_default).
        private bool micMuted = AppConfig.MicMutedByDefault;
        // Suppresses the mute toast while applying the startup default.
        private bool micMuteNotifyReady;
        private LowLevelKeyboardHook? micMuteHook;
        /// <summary>Left Ctrl + Right Ctrl → "What should I say?" (RegisterHotKey cannot bind both).</summary>
        private LowLevelKeyboardHook? whatShouldISayHook;
        private WheelCaptureHook? wheelHook;

        /// <summary>Pixels per press/repeat for Ctrl+Alt+Arrow overlay nudge (RegisterHotKey only; no LL hook).</summary>
        private const int OverlayNudgeStepPx = 28;

        /// <summary>Min/max height for the Live Insights content area when drag-resizing.</summary>
        private const double ContentAreaMinHeight = 160;
        private const double ContentAreaMaxHeight = 900;

        private bool resizingContentArea;
        private Point contentResizeStart;
        private double contentResizeStartHeight;

        /// <summary>Opacity step for Ctrl+Alt+> / Ctrl+Alt+&lt;.</summary>
        private const double OpacityStep = 0.05;

        // State
        private OverlaySessionPhase sessionPhase = OverlaySessionPhase.Ready;
        private string? lastScreenOcr;
        private bool isInvisibleToCapture;
        private bool isExpanded = true;
        private SessionManager.SessionData? currentSession;
        private int selectedModeIndex;
        private DispatcherTimer? sessionTimer;
        private DateTime sessionStartTime;

        // Interview mode: target job description captured via the JD popup.
        private string interviewJobDescription = "";
        private string interviewCompany = "";
        private string interviewRole = "";

        // Confidence reported by the backend for the answer currently completing.
        // Set just before the matching ResponseComplete / ai.suggestion.end fires.
        private double? pendingManualConfidence;
        private double? pendingAutoConfidence;

        // Response cancellation. manualStreaming tracks the HTTP SSE stream; autoStreaming
        // tracks the live WebSocket AI suggestion. suppressAutoSuggestion drops any further
        // auto tokens after the user cancels (the backend may still be generating).
        private bool manualStreaming;
        private bool autoStreaming;
        private bool suppressAutoSuggestion;
        private bool IsResponseStreaming => manualStreaming || autoStreaming;

        // Pulse animation for live dot
        private DispatcherTimer? liveDotPulseTimer;
        private bool liveDotVisible = true;

        // Inline toolbar error pill auto-dismiss timer
        private DispatcherTimer? inlineErrorDismissTimer;

        private bool captureHooksReplayed;

        // Re-entry guard for screen capture: 0 = idle, 1 = capture in flight.
        // Atomically flipped via Interlocked.Exchange so rapid clicks of the
        // camera / screenshot buttons can't fire overlapping OCR requests
        // (which would cancel each other's LLM streams and leave the UI confused).
        private int captureInFlight;

        /// <summary>Max screenshots buffered in Pause (batch) mode before we refuse more.</summary>
        private const int MaxPendingCaptures = 8;

        private sealed class PendingCapture
        {
            public required string Base64 { get; init; }
            public string? SavedPath { get; init; }
            public ImageSource? Thumbnail { get; init; }
        }

        private readonly List<PendingCapture> pendingCaptures = new();

        /// <summary>
        /// Set when the user clicks Play (leaves Pause) while a camera capture is still
        /// in flight. The capture's finally-block starts extraction as soon as it finishes
        /// so unpausing never silently drops the batch.
        /// </summary>
        private bool finalizeBatchWhenIdle;

        private bool IsListening => sessionPhase == OverlaySessionPhase.Listening;

        private bool IsBatchCaptureMode => CaptureBatchToggle?.IsChecked == true;

        public OverlayWindow()
        {
            InitializeComponent();
            // Config is loaded once in App.xaml.cs Application_Startup. We don't reload it
            // here because the disk + YAML parse takes 50–200 ms on cold cache and was
            // duplicating work for every overlay open. The Dashboard's prompts editor
            // calls AppConfig.RefreshModesFromUserFile() itself after a save, and the
            // PromptsService cache (added in this hardening pass) makes any later refresh
            // cheap if it ever does happen here.

            // Initialize services
            authService = new AuthService(AppConfig.BackendHttpUrl);
            transcriptionService = new RealtimeTranscriptionService(AppConfig.BackendWebSocketUrl);
            audioCaptureService = new AudioCaptureService();
            speakerCaptureService = new SpeakerCaptureService();
            screenCaptureService = new ScreenCaptureService(AppConfig.BackendHttpUrl);
            streamingService = new StreamingResponseService(AppConfig.BackendHttpUrl);
            history = new ConversationHistoryService();
            conversationSync = new ConversationSyncService(AppConfig.BackendHttpUrl, authService.GetAuthorizationHeader);
            titleService = new ConversationTitleService(AppConfig.BackendHttpUrl);

            InitializeWindowPosition();
            SubscribeToEvents();
            PopulateModeSelector();
            PopulateQuickActions();
            // Sync conversation id / richer server history into the local model only —
            // do NOT replay into the overlay UI on launch (Conversations tab owns history).
            _ = HydrateConversationFromServerAsync();
        }

        /// <summary>
        /// Resume the caller's ongoing server-side conversation (if reachable). Best-effort:
        /// local <c>current-session.json</c> stays authoritative when it has more messages
        /// than the server (common when sync lagged).
        /// </summary>
        private async Task HydrateConversationFromServerAsync()
        {
            try
            {
                var result = await conversationSync.FetchActiveConversationAsync();
                if (result == null) return;
                var (conversationId, messages) = result.Value;

                bool grew = history.HydrateFromServer(conversationId, messages);
                if (grew || !string.IsNullOrEmpty(conversationId))
                    history.SaveCurrentNow();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[UI] Conversation hydration error: {ex.Message}");
            }
        }

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            // Clicks must not make this overlay the foreground window.
            NoActivateWindow.Apply(this);
            // Layered WPF windows: apply as soon as HWND exists; Loaded will set flags again.
            if (AppConfig.InvisibleToCapture)
                CaptureInvisibility.ApplyToWindow(this);
        }

        public void SetSession(SessionManager.SessionData session)
        {
            currentSession = session;
            authService.AccessToken = session.AccessToken;
            authService.RefreshToken = session.RefreshToken;
            ApplyAuthTokens(session.AccessToken);
            RefreshToolbarUsername();
        }

        /// <summary>Push the current JWT to every backend client in the overlay.</summary>
        private void ApplyAuthTokens(string token)
        {
            if (string.IsNullOrEmpty(token)) return;
            transcriptionService.SetAuthToken(token);
            screenCaptureService.SetAuthToken(token);
            streamingService.SetAuthToken(token);
            titleService.SetAuthToken(token);
        }

        /// <summary>
        /// Ensure the JWT is still valid before any authenticated API call. Refreshes
        /// automatically using the stored refresh token (same flow as the Dashboard).
        /// </summary>
        private async Task<bool> EnsureAuthTokenFreshAsync()
        {
            if (currentSession == null)
            {
                ShowInlineError("Not signed in. Open the Dashboard and sign in again.");
                return false;
            }

            if (DateTime.Now <= currentSession.ExpiryTime
                && !string.IsNullOrEmpty(currentSession.AccessToken))
            {
                ApplyAuthTokens(currentSession.AccessToken);
                return true;
            }

            if (DateTime.Now > currentSession.StorageExpiry)
            {
                ShowInlineError("Session expired (90 days). Sign in again from the Dashboard.");
                return false;
            }

            authService.AccessToken = currentSession.AccessToken;
            authService.RefreshToken = currentSession.RefreshToken;

            if (!await authService.RefreshTokenAsync().ConfigureAwait(false))
            {
                ShowInlineError("Session expired. Open the Dashboard and sign in again.");
                return false;
            }

            currentSession.AccessToken = authService.AccessToken ?? currentSession.AccessToken;
            currentSession.RefreshToken = authService.RefreshToken ?? currentSession.RefreshToken;
            currentSession.ExpiresIn = authService.ExpiresIn;
            currentSession.ExpiryTime = DateTime.Now.AddSeconds(authService.ExpiresIn - 60);
            SessionManager.SaveSession(currentSession);
            ApplyAuthTokens(currentSession.AccessToken);
            Console.WriteLine("[Auth] Access token refreshed in overlay");
            return true;
        }

        /// <summary>Shows the signed-in user in the toolbar (replaces the old dashboard icon).</summary>
        private void RefreshToolbarUsername()
        {
            string name = currentSession?.Username?.Trim() ?? "";
            if (string.IsNullOrEmpty(name))
                name = SessionManager.LoadSession()?.Username?.Trim() ?? "";
            if (string.IsNullOrEmpty(name))
                name = "Account";

            if (ToolbarUsernameText != null)
                ToolbarUsernameText.Text = name;
            if (DashboardUserBtn != null)
                DashboardUserBtn.ToolTip = $"Open dashboard ({name})";
        }

        /// <summary>Background-generate an AI title for the given session and patch the saved JSON file.</summary>
        private async Task GenerateAndSaveTitleAsync(ConversationSession? session)
        {
            try
            {
                if (session == null || session.Messages.Count == 0) return;
                string title = await titleService.GenerateAsync(session).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(title)) return;
                ConversationHistoryService.UpdateSavedSessionTitle(session.SessionId, title);
                Console.WriteLine($"[History] Title generated for {session.SessionId[..Math.Min(8, session.SessionId.Length)]}: {title}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[History] Title gen error: {ex.Message}");
            }
        }

        #region Initialization

        private void Window_Loaded(object sender, RoutedEventArgs e)
        {
            if (AppConfig.InvisibleToCapture)
                EnableScreenShareInvisibility();

            RefreshCaptureHooks();

            RefreshToolbarUsername();

            this.Opacity = AppConfig.DefaultOpacity;
            OpacitySlider.Value = AppConfig.DefaultOpacity;

            UpdateCameraVisibility();

            // Nothing to regenerate until the user has asked something.
            if (RegenBtn != null) RegenBtn.IsEnabled = false;

            // Island Bar click-through: keep the Island Bar button itself clickable while
            // the rest of the overlay passes clicks through to the app behind it.
            clickThroughService = new ClickThroughService(this, GetIslandBarScreenRect);

            // Scroll Live Insights from a global wheel hook whenever the cursor is over the
            // card (click-through or normal). Nested Markdown/code viewers otherwise swallow
            // the wheel so chrome / “outside the answer text” never scrolls the conversation.
            wheelHook = new WheelCaptureHook(HandleLiveInsightsWheel);

            // Register the system-wide hotkeys so SnapEye reacts to Ctrl+Shift+L/S/C/H
            // even when another application has focus. Window_Loaded fires after the
            // HWND exists, which is what GlobalHotkeyService needs.
            RegisterGlobalHotkeys();

            // Global Backtick(`)+Space toggles mic mute. This chord can't be a
            // RegisterHotKey combo (` isn't a valid modifier), so it uses a low-level
            // keyboard hook installed on this (UI) thread, which has a message loop.
            try
            {
                micMuteHook = new LowLevelKeyboardHook(
                    LowLevelKeyboardHook.VK_OEM_3, LowLevelKeyboardHook.VK_SPACE);
                micMuteHook.ChordPressed += (s, _) => ToggleMicMute();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Hotkey] Mic mute hook error: {ex.Message}");
            }

            // Both Ctrl keys (left + right) fire "What should I say?" globally.
            try
            {
                whatShouldISayHook = new LowLevelKeyboardHook(
                    requireControl: false,
                    new[] { LowLevelKeyboardHook.VK_LCONTROL },
                    new[] { LowLevelKeyboardHook.VK_RCONTROL });
                whatShouldISayHook.ChordPressed += (s, _) => TriggerWhatShouldISay();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Hotkey] What-should-I-say hook error: {ex.Message}");
            }

            // Apply default mute without the "muted" toast; user toggles notify after this.
            try
            {
                if (MicMuteBtn != null)
                {
                    MicMuteBtn.IsChecked = AppConfig.MicMutedByDefault;
                    micMuted = AppConfig.MicMutedByDefault;
                    MicMuteBtn.ToolTip = micMuted
                        ? "Microphone muted — click or press ` + Space to unmute"
                        : "Mute microphone (` + Space)";
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[UI] Default mic mute apply error: {ex.Message}");
            }
            micMuteNotifyReady = true;
        }

        /// <summary>Screen-pixel rectangle of the Island Bar button (the click-through carve-out).</summary>
        private Rect? GetIslandBarScreenRect()
        {
            try
            {
                if (IslandBarToggle == null || !IslandBarToggle.IsVisible) return null;
                Point tl = IslandBarToggle.PointToScreen(new Point(0, 0));
                Point br = IslandBarToggle.PointToScreen(
                    new Point(IslandBarToggle.ActualWidth, IslandBarToggle.ActualHeight));
                var rect = new Rect(tl, br);
                rect.Inflate(6, 6); // a little slack so it's easy to land on
                return rect;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Global wheel handler for Live Insights. Vertical wheel scrolls the conversation;
        /// horizontal wheel (trackpad sideways) or Shift+vertical wheel pans a wide code block
        /// under the cursor. Returns true to swallow the OS event.
        /// </summary>
        private bool HandleLiveInsightsWheel(int screenX, int screenY, int delta, bool horizontal)
        {
            if (!isExpanded || !IsVisible || AlphaRegion == null) return false;
            if (LiveInsightsCard == null || !LiveInsightsCard.IsVisible) return false;
            if (LiveInsightsCard.ActualWidth <= 0 || LiveInsightsCard.ActualHeight <= 0) return false;

            Point tl = LiveInsightsCard.PointToScreen(new Point(0, 0));
            Point br = LiveInsightsCard.PointToScreen(
                new Point(LiveInsightsCard.ActualWidth, LiveInsightsCard.ActualHeight));
            if (!new Rect(tl, br).Contains(screenX, screenY)) return false;

            bool shift = (Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift;
            if (horizontal || shift)
            {
                // Trackpad H-swipe or Shift+wheel → pan code under the cursor when it overflows.
                if (AlphaRegion.TryScrollCodeBlockAt(screenX, screenY, delta))
                    return true;
                // Horizontal gesture outside a scrollable code block: swallow so the app
                // behind the overlay does not pan; Shift+wheel with nowhere to pan falls
                // through to vertical conversation scroll below when shift is held alone.
                if (horizontal)
                    return true;
            }

            // Leave multiline TextBoxes / combo dropdowns / pending-capture strip alone.
            if (IsScreenPointOverPrivateScroller(screenX, screenY))
                return false;

            AlphaRegion.ScrollActiveView(delta);
            return true;
        }

        /// <summary>
        /// True when the cursor is over a control that should keep its own wheel behavior
        /// (job-details / chat input, combo popup, horizontal pending-capture strip).
        /// </summary>
        private bool IsScreenPointOverPrivateScroller(int screenX, int screenY)
        {
            try
            {
                if (LiveInsightsCard == null) return false;
                Point local = LiveInsightsCard.PointFromScreen(new Point(screenX, screenY));
                var hit = VisualTreeHelper.HitTest(LiveInsightsCard, local);
                for (DependencyObject? d = hit?.VisualHit;
                     d != null && !ReferenceEquals(d, LiveInsightsCard);
                     d = VisualTreeHelper.GetParent(d))
                {
                    if (d is TextBox or ComboBox)
                        return true;
                    if (ReferenceEquals(d, PendingCapturesScroll))
                        return true;
                }
            }
            catch
            {
                /* best effort */
            }
            return false;
        }

        /// <summary>
        /// Fallback when the global wheel hook does not run (e.g. hook install failed).
        /// Always forwards to the response view; Shift+code horizontal pan is handled by
        /// <see cref="SolutionRegion.SolutionRegionAlpha"/>'s own PreviewMouseWheel.
        /// </summary>
        private void LiveInsightsCard_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (e.Handled || e.Delta == 0 || AlphaRegion == null) return;

            if ((Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift)
                return;

            if (e.OriginalSource is DependencyObject src)
            {
                for (DependencyObject? d = src; d != null && !ReferenceEquals(d, LiveInsightsCard);
                     d = VisualTreeHelper.GetParent(d))
                {
                    if (d is TextBox or ComboBox)
                        return;
                    if (ReferenceEquals(d, PendingCapturesScroll))
                        return;
                }
            }

            AlphaRegion.ScrollActiveView(e.Delta);
            e.Handled = true;
        }

        private void IslandBarToggle_Changed(object sender, RoutedEventArgs e)
        {
            try
            {
                // Checked (ON) = SnapEye is interactive. Unchecked (OFF) = click-through.
                bool clickThrough = IslandBarToggle.IsChecked != true;
                clickThroughService?.SetClickThrough(clickThrough);
                if (clickThrough)
                    ShowInlineError("Island Bar OFF — clicks pass through to the app behind. Click the Island Bar button to turn it back on.");
                else
                    ClearInlineError();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[UI] Island Bar toggle error: {ex.Message}");
            }
        }

        /// <summary>Fired by the MicMuteBtn toggle (Checked = muted).</summary>
        private void MicMuteBtn_Changed(object sender, RoutedEventArgs e)
        {
            try
            {
                micMuted = MicMuteBtn.IsChecked == true;
                MicMuteBtn.ToolTip = micMuted
                    ? "Microphone muted — click or press ` + Space to unmute"
                    : "Mute microphone (` + Space)";
                if (!micMuteNotifyReady)
                    return;
                if (micMuted)
                    ShowInlineError("Microphone muted — your voice isn't being transcribed.");
                else
                    ClearInlineError();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[UI] Mic mute toggle error: {ex.Message}");
            }
        }

        /// <summary>
        /// Toggles mic mute by flipping the toggle button (which raises
        /// <see cref="MicMuteBtn_Changed"/>). Safe to call from any thread / hotkey.
        /// </summary>
        public void ToggleMicMute()
        {
            try
            {
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    try { MicMuteBtn.IsChecked = MicMuteBtn.IsChecked != true; }
                    catch (Exception ex) { Console.WriteLine($"[UI] ToggleMicMute error: {ex.Message}"); }
                }));
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[UI] ToggleMicMute dispatch error: {ex.Message}");
            }
        }

        /// <summary>Remembers the last question asked so the Regenerate button can re-answer it.</summary>
        private void SetLastQuestion(string? question, bool inTranscript)
        {
            lastAnsweredQuestion = string.IsNullOrWhiteSpace(question) ? null : question;
            lastAnswerWasTranscript = inTranscript;
            if (RegenBtn != null)
                RegenBtn.IsEnabled = lastAnsweredQuestion != null;
        }

        private void Window_ContentRendered(object? sender, EventArgs e)
        {
            if (captureHooksReplayed) return;
            captureHooksReplayed = true;
            try
            {
                if (AppConfig.InvisibleToCapture)
                    CaptureInvisibility.ApplyToWindow(this);
                RefreshCaptureHooks();
                // One more pass after layout: ComboBox template popups can appear late.
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    try
                    {
                        if (AppConfig.InvisibleToCapture)
                            CaptureInvisibility.ApplyToWindow(this);
                        RefreshCaptureHooks();
                    }
                    catch
                    {
                        /* best effort */
                    }
                }), DispatcherPriority.ApplicationIdle);
            }
            catch
            {
                /* best effort */
            }
        }

        /// <summary>
        /// Popups (menus, ComboBox dropdowns) live in separate HWNDs; hook them after templates exist.
        /// </summary>
        private void RefreshCaptureHooks()
        {
            try { CaptureInvisibility.HookAllPopups(this); } catch { /* best effort */ }
            try { CaptureInvisibility.HookContextMenu(SettingsMenu); } catch { /* best effort */ }
            try { CaptureInvisibility.HookComboBoxPopup(ModeSelector); } catch { /* best effort */ }

            // Menus and dropdowns are separate top-level HWNDs; keep them from stealing focus too.
            try { NoActivateWindow.HookAllPopups(this); } catch { /* best effort */ }
            try { NoActivateWindow.HookContextMenu(SettingsMenu); } catch { /* best effort */ }
            try { NoActivateWindow.HookComboBoxPopup(ModeSelector); } catch { /* best effort */ }

            // Catch any late/default context menus (e.g. FlowDocument Copy/Select All) that
            // were not present when HookAllPopups ran, and hide them from capture.
            try
            {
                RemoveHandler(FrameworkElement.ContextMenuOpeningEvent, (ContextMenuEventHandler)OnOverlayContextMenuOpening);
                AddHandler(FrameworkElement.ContextMenuOpeningEvent, (ContextMenuEventHandler)OnOverlayContextMenuOpening, true);
            }
            catch { /* best effort */ }
        }

        private void OnOverlayContextMenuOpening(object sender, ContextMenuEventArgs e)
        {
            try
            {
                // Default document editor menu (Copy / Select All) — block; it leaks into captures.
                if (e.Source is FlowDocumentScrollViewer)
                {
                    e.Handled = true;
                    return;
                }

                if (e.Source is FrameworkElement fe && fe.ContextMenu != null)
                {
                    if (AppConfig.InvisibleToCapture)
                        CaptureInvisibility.HookContextMenu(fe.ContextMenu);
                    NoActivateWindow.HookContextMenu(fe.ContextMenu);
                }
            }
            catch
            {
                /* best effort */
            }
        }

        private void RegisterGlobalHotkeys()
        {
            try
            {
                hotkeyService?.Dispose();
                hotkeyService = new GlobalHotkeyService(this);
                var failedCombos = new System.Collections.Generic.List<string>();
                hotkeyService.HotkeyRegistrationFailed += (_, msg) => failedCombos.Add(msg);

                uint ctrlShift = GlobalHotkeyService.MOD_CONTROL | GlobalHotkeyService.MOD_SHIFT;

                // Ctrl+Shift+L: Toggle listening
                hotkeyService.Register(ctrlShift, Key.L, () =>
                {
                    try { MicToggle_Click(null, new RoutedEventArgs()); } catch (Exception ex) { Console.WriteLine($"[Hotkey] L error: {ex.Message}"); }
                });
                // Ctrl+Shift+S: Capture screen and route to OCR/LLM
                hotkeyService.Register(ctrlShift, Key.S, async () =>
                {
                    try { await CaptureScreenAndRouteAsync(); } catch (Exception ex) { Console.WriteLine($"[Hotkey] S error: {ex.Message}"); }
                });
                // Ctrl+Shift+P: Toggle Pause/Play multi-screenshot batch mode
                hotkeyService.Register(ctrlShift, Key.P, () =>
                {
                    try { ToggleCaptureBatchMode(); }
                    catch (Exception ex) { Console.WriteLine($"[Hotkey] P error: {ex.Message}"); }
                });
                // Ctrl+Shift+C: Copy current AI answer
                hotkeyService.Register(ctrlShift, Key.C, () =>
                {
                    try { CopyAnswer_Click(this, new RoutedEventArgs()); } catch (Exception ex) { Console.WriteLine($"[Hotkey] C error: {ex.Message}"); }
                });
                // Ctrl+Shift+H: Hide / show overlay
                hotkeyService.Register(ctrlShift, Key.H, () =>
                {
                    try
                    {
                        this.Visibility = this.Visibility == Visibility.Visible
                            ? Visibility.Collapsed
                            : Visibility.Visible;
                    }
                    catch (Exception ex) { Console.WriteLine($"[Hotkey] H error: {ex.Message}"); }
                });
                // Ctrl+Shift+Down / Up: expand or collapse Live Insights panel
                hotkeyService.Register(ctrlShift, Key.Down, () =>
                {
                    try { SetLiveInsightsExpanded(true); }
                    catch (Exception ex) { Console.WriteLine($"[Hotkey] Expand error: {ex.Message}"); }
                });
                hotkeyService.Register(ctrlShift, Key.Up, () =>
                {
                    try { SetLiveInsightsExpanded(false); }
                    catch (Exception ex) { Console.WriteLine($"[Hotkey] Collapse error: {ex.Message}"); }
                });

                // Ctrl+Alt+Arrow: nudge overlay. allowRepeat so holding the key keeps moving
                // (MOD_NOREPEAT would fire once and stop).
                uint ctrlAlt = GlobalHotkeyService.MOD_CONTROL | GlobalHotkeyService.MOD_ALT;
                int step = OverlayNudgeStepPx;
                hotkeyService.Register(ctrlAlt, Key.Left, () =>
                {
                    try { NudgeOverlay(-step, 0); }
                    catch (Exception ex) { Console.WriteLine($"[Hotkey] Nudge left error: {ex.Message}"); }
                }, allowRepeat: true);
                hotkeyService.Register(ctrlAlt, Key.Right, () =>
                {
                    try { NudgeOverlay(step, 0); }
                    catch (Exception ex) { Console.WriteLine($"[Hotkey] Nudge right error: {ex.Message}"); }
                }, allowRepeat: true);
                hotkeyService.Register(ctrlAlt, Key.Up, () =>
                {
                    try { NudgeOverlay(0, -step); }
                    catch (Exception ex) { Console.WriteLine($"[Hotkey] Nudge up error: {ex.Message}"); }
                }, allowRepeat: true);
                hotkeyService.Register(ctrlAlt, Key.Down, () =>
                {
                    try { NudgeOverlay(0, step); }
                    catch (Exception ex) { Console.WriteLine($"[Hotkey] Nudge down error: {ex.Message}"); }
                }, allowRepeat: true);
                // Ctrl+Alt+Z: Toggle Island Bar clickable ON/OFF
                hotkeyService.Register(ctrlAlt, Key.Z, () =>
                {
                    try { ToggleIslandBarClickable(); }
                    catch (Exception ex) { Console.WriteLine($"[Hotkey] Island Bar error: {ex.Message}"); }
                });

                // Ctrl+Alt+./> and Ctrl+Alt+,/< : opacity up / down (period & comma keys;
                // Shift not required). allowRepeat so holding the chord keeps stepping.
                hotkeyService.Register(ctrlAlt, Key.OemPeriod, () =>
                {
                    try { AdjustOpacity(OpacityStep); }
                    catch (Exception ex) { Console.WriteLine($"[Hotkey] Opacity up error: {ex.Message}"); }
                }, allowRepeat: true);
                hotkeyService.Register(ctrlAlt, Key.OemComma, () =>
                {
                    try { AdjustOpacity(-OpacityStep); }
                    catch (Exception ex) { Console.WriteLine($"[Hotkey] Opacity down error: {ex.Message}"); }
                }, allowRepeat: true);

                if (failedCombos.Count > 0)
                {
                    foreach (string msg in failedCombos)
                        Console.WriteLine($"[Hotkey] {msg}");
                    ShowInlineError(failedCombos.Count == 1
                        ? failedCombos[0]
                        : $"{failedCombos.Count} hotkeys failed (usually another SnapEye is still running). Close extras in Task Manager, then restart.");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Hotkey] Registration error: {ex.Message}");
                ShowInlineError($"Could not register global hotkeys: {ex.Message}");
            }
        }

        /// <summary>
        /// Flip Island Bar clickable mode (ON = interactive, OFF = click-through).
        /// Driven by Ctrl+Alt+Z or the Island Bar toggle button.
        /// </summary>
        private void ToggleIslandBarClickable()
        {
            if (IslandBarToggle == null) return;
            IslandBarToggle.IsChecked = IslandBarToggle.IsChecked != true;
        }

        private void InitializeWindowPosition()
        {
            // Position at top center of screen
            this.Left = (SystemParameters.PrimaryScreenWidth - this.Width) / 2;
            this.Top = 20;
        }

        /// <summary>
        /// Move the overlay by a delta in physical pixels. Uses Win32 virtual-screen bounds
        /// (PerMonitorV2-safe) and only keeps a thin strip on-screen so Ctrl+Alt+→ can reach
        /// the right edge instead of stopping halfway.
        /// </summary>
        private void NudgeOverlay(int dx, int dy)
        {
            try
            {
                if (!IsLoaded) return;
                NoActivateWindow.Nudge(this, dx, dy);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[UI] NudgeOverlay error: {ex.Message}");
            }
        }

        private void ContentResizeGrip_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            try
            {
                if (ContentRow == null || sender is not UIElement grip) return;
                resizingContentArea = true;
                contentResizeStart = e.GetPosition(this);
                contentResizeStartHeight = ContentRow.Height.IsAbsolute
                    ? ContentRow.Height.Value
                    : (AlphaRegion?.ActualHeight > 0 ? AlphaRegion.ActualHeight : 420);
                grip.CaptureMouse();
                e.Handled = true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[UI] Content resize start error: {ex.Message}");
            }
        }

        private void ContentResizeGrip_MouseMove(object sender, MouseEventArgs e)
        {
            if (!resizingContentArea || ContentRow == null) return;
            try
            {
                Point p = e.GetPosition(this);
                double dy = p.Y - contentResizeStart.Y;
                double h = Math.Clamp(contentResizeStartHeight + dy, ContentAreaMinHeight, ContentAreaMaxHeight);
                ContentRow.Height = new GridLength(h);
                e.Handled = true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[UI] Content resize move error: {ex.Message}");
            }
        }

        private void ContentResizeGrip_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (!resizingContentArea) return;
            try
            {
                resizingContentArea = false;
                if (sender is UIElement grip && grip.IsMouseCaptured)
                    grip.ReleaseMouseCapture();
                e.Handled = true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[UI] Content resize end error: {ex.Message}");
            }
        }

        /// <summary>Show or hide the main session / Live Insights body and sync the chevron.</summary>
        private void SetLiveInsightsExpanded(bool expanded)
        {
            try
            {
                if (SessionBody == null || ChevronIcon == null) return;
                isExpanded = expanded;
                SessionBody.Visibility = isExpanded ? Visibility.Visible : Visibility.Collapsed;
                ChevronIcon.Data = isExpanded
                    ? Geometry.Parse("M12,8L6,14L7.41,15.41L12,10.83L16.59,15.41L18,14L12,8Z")
                    : Geometry.Parse("M16.59,8.59L12,13.17L7.41,8.59L6,10L12,16L18,10L16.59,8.59Z");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[UI] SetLiveInsightsExpanded error: {ex.Message}");
            }
        }

        private void SubscribeToEvents()
        {
            // Transcription events
            transcriptionService.TranscriptionReceived += OnTranscriptionReceived;
            transcriptionService.ErrorOccurred += OnTranscriptionError;
            transcriptionService.Connected += OnTranscriptionConnected;
            transcriptionService.Disconnected += OnTranscriptionDisconnected;
            transcriptionService.SessionCreated += OnSessionCreated;

            // AI suggestion events
            transcriptionService.AISuggestionStarted += OnAISuggestionStarted;
            transcriptionService.AISuggestionToken += OnAISuggestionToken;
            transcriptionService.AISuggestionCompleted += OnAISuggestionCompleted;
            transcriptionService.AISuggestionConfidence += OnAISuggestionConfidence;
            transcriptionService.AISuggestionError += OnAISuggestionError;

            // Audio capture events
            audioCaptureService.MicrophoneDataAvailable += OnMicrophoneDataAvailable;
            audioCaptureService.ErrorOccurred += OnAudioError;
            speakerCaptureService.AudioDataAvailable += OnSpeakerDataAvailable;
            speakerCaptureService.ErrorOccurred += OnAudioError;

            // Screen capture events
            screenCaptureService.ErrorOccurred += OnServiceError;

            // Streaming response events. The user's prompt bubble and the empty answer bubble
            // are created up-front by the initiator (Send_Click / RunContextualPromptAsync /
            // CameraBtn_Click / transcription AI suggestions).
            // Capture + live-voice answers share one session panel.
            streamingService.TokenReceived += (s, token) => Dispatcher.BeginInvoke(new Action(() =>
            {
                try { AlphaRegion.AppendStreamingToken(token); }
                catch (Exception ex) { Console.WriteLine($"[UI] TokenReceived render error: {ex.Message}"); }
            }));
            streamingService.ConfidenceReceived += (s, conf) => pendingManualConfidence = conf;
            streamingService.StreamStarted += (s, e) => Dispatcher.BeginInvoke(new Action(() =>
            {
                try
                {
                    manualStreaming = true;
                    UpdateStopButton();
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[UI] StreamStarted handler error: {ex.Message}");
                }
            }));
            streamingService.ResponseComplete += (s, full) => Dispatcher.BeginInvoke(new Action(() =>
            {
                try
                {
                    manualStreaming = false;
                    UpdateStopButton();
                    double? conf = pendingManualConfidence;
                    pendingManualConfidence = null;
                    AlphaRegion.EndStreamingResponse(full, conf);
                    if (!string.IsNullOrWhiteSpace(full))
                        history.Append(ConversationMessageKind.AssistantAnswer, full);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[UI] ResponseComplete handler error: {ex.Message}");
                }
            }));
            streamingService.ErrorOccurred += (s, err) => Dispatcher.BeginInvoke(new Action(() =>
            {
                try
                {
                    manualStreaming = false;
                    UpdateStopButton();
                    AlphaRegion.ShowStreamingError(err);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[UI] ErrorOccurred handler error: {ex.Message}");
                }
            }));

            // Auth
            authService.ErrorOccurred += OnServiceError;

            // Server-side conversation sync (best-effort, never blocks the UI). Typed/
            // quick-action/assistant-answer messages are final the moment they're added;
            // spoken (voice) messages only sync via MessageFinalized once the transcript
            // segment is final, so interim/refining transcript revisions aren't spammed
            // to the backend on every partial update.
            history.MessageAdded += (s, msg) =>
            {
                if (msg.Kind != ConversationMessageKind.UserSpoken && msg.Kind != ConversationMessageKind.OtherSpoken)
                    conversationSync.SyncMessageAsync(msg);
            };
            history.MessageFinalized += (s, msg) => conversationSync.SyncMessageAsync(msg);
        }

        private void PopulateModeSelector()
        {
            try
            {
                ModeSelector.Items.Clear();
                foreach (var mode in AppConfig.Modes)
                {
                    ModeSelector.Items.Add(new ComboBoxItem { Content = mode.Name, Tag = mode });
                }
                if (ModeSelector.Items.Count > 0)
                    ModeSelector.SelectedIndex = 0;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[UI] Mode selector error: {ex.Message}");
            }
        }

        private void PopulateQuickActions()
        {
            try
            {
                QuickActionsPanel.Children.Clear();
                foreach (var action in AppConfig.QuickActions)
                {
                    var btn = new Button
                    {
                        Content = action.Label,
                        Tag = action,
                        Style = (Style)FindResource("QuickActionStyle"),
                    };
                    btn.Click += QuickAction_Click;
                    QuickActionsPanel.Children.Add(btn);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[UI] Quick actions error: {ex.Message}");
            }
        }

        #endregion

        #region Screen Share Invisibility

        private void EnableScreenShareInvisibility()
        {
            try
            {
                isInvisibleToCapture = CaptureInvisibility.ApplyToWindow(this);
                UpdateStealthIcon();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[UI] Invisibility error: {ex.Message}");
            }
        }

        private void UpdateStealthIcon()
        {
            // Stealth is toggled from the ⋯ menu; no toolbar glyph in this layout.
        }

        #endregion

        #region Window Events

        private void DragHandle_PreviewMouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton == MouseButton.Left)
            {
                try
                {
                    NoActivateWindow.BeginDrag(this);
                    e.Handled = true;
                }
                catch { /* drag already in progress */ }
            }
        }

        private void DashboardLogo_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                // Return to dashboard; the launch flow subscribed to Closed to show it again.
                Close();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[UI] Dashboard logo error: {ex.Message}");
            }
        }

        private async void EndSession_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                // While live listen is on: only stop audio. Keep the shared capture + voice
                // conversation (same as switching Capture ↔ Listen). A second End while
                // already idle archives the session and clears the panel for a fresh start.
                if (IsListening)
                {
                    await StopTranscriptionAsync();
                    TranscriptToggle.IsChecked = false;
                    AlphaRegion.SwitchToChatTab();
                    UpdateCameraVisibility();
                    ClearInlineError();
                    return;
                }

                streamingService.CancelCurrentStream();
                UpdateListeningUI(false);
                StopSessionTimer();

                transcriptionService.ClearConversation();

                // Capture the just-ended session before starting a new one so we can
                // generate an AI title for it in the background.
                var endedSession = history.Current;
                history.EndCurrentSession();
                history.StartNewSession();
                _ = GenerateAndSaveTitleAsync(endedSession);

                AlphaRegion.ClearAll();
                lastScreenOcr = null;
                TranscriptToggle.IsChecked = false;
                AlphaRegion.SwitchToChatTab();
                UpdateCameraVisibility();
                ClearInlineError();
                AlphaRegion.SetMarkdownContent("**Session ended.**\n\nPrevious conversation saved. Tap **Listen** to start again, or use **Ask AI** anytime.");
                sessionPhase = OverlaySessionPhase.Ready;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[UI] End session error: {ex.Message}");
            }
        }

        private void Window_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton == MouseButton.Left)
            {
                try { NoActivateWindow.BeginDrag(this); } catch { }
            }
        }

        private void Window_KeyDown(object sender, KeyEventArgs e)
        {
            // Focused-window fallback for the same shortcuts that GlobalHotkeyService
            // exposes system-wide. Useful when the global RegisterHotKey call lost a
            // collision to another app (in which case the user can still press the
            // shortcut while the overlay has focus).
            try
            {
                // Ctrl+Shift+L: Toggle listen
                if (e.Key == Key.L && Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift))
                {
                    MicToggle_Click(null, null!);
                    e.Handled = true;
                }
                // Ctrl+Shift+S: Screenshot / OCR
                else if (e.Key == Key.S && Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift))
                {
                    Screenshot_Click(null, null!);
                    e.Handled = true;
                }
                // Ctrl+Shift+C: Copy current AI answer
                else if (e.Key == Key.C && Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift))
                {
                    CopyAnswer_Click(sender, e);
                    e.Handled = true;
                }
                // Ctrl+Shift+H: Hide / show overlay
                else if (e.Key == Key.H && Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift))
                {
                    this.Visibility = this.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
                    e.Handled = true;
                }
                // Ctrl+Shift+Down / Up: Live Insights expand / collapse (global hotkey fallback)
                else if (e.Key == Key.Down && Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift))
                {
                    SetLiveInsightsExpanded(true);
                    e.Handled = true;
                }
                else if (e.Key == Key.Up && Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift))
                {
                    SetLiveInsightsExpanded(false);
                    e.Handled = true;
                }
                // Ctrl+Alt+Arrow: nudge overlay (focused-window fallback; global path uses RegisterHotKey)
                else if ((Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control
                         && (Keyboard.Modifiers & ModifierKeys.Alt) == ModifierKeys.Alt
                         && (e.Key == Key.Left || e.Key == Key.Right || e.Key == Key.Up || e.Key == Key.Down))
                {
                    int step = OverlayNudgeStepPx;
                    int ndx = 0, ndy = 0;
                    switch (e.Key)
                    {
                        case Key.Left: ndx = -step; break;
                        case Key.Right: ndx = step; break;
                        case Key.Up: ndy = -step; break;
                        case Key.Down: ndy = step; break;
                    }
                    NudgeOverlay(ndx, ndy);
                    e.Handled = true;
                }
                // Escape: cancel an in-progress response first; otherwise minimize/hide.
                else if (e.Key == Key.Escape)
                {
                    if (IsResponseStreaming)
                        CancelActiveResponse();
                    else
                        this.Visibility = Visibility.Collapsed;
                    e.Handled = true;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[UI] Keyboard shortcut error: {ex.Message}");
            }
        }

        #endregion

        #region Toolbar Events

        private void Stealth_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (isInvisibleToCapture)
                {
                    CaptureInvisibility.RestoreWindow(this);
                    isInvisibleToCapture = false;
                }
                else
                {
                    isInvisibleToCapture = CaptureInvisibility.ApplyToWindow(this);
                    RefreshCaptureHooks();
                }
                UpdateStealthIcon();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[UI] Stealth toggle error: {ex.Message}");
            }
        }

        private async void MicToggle_Click(object? sender, RoutedEventArgs e)
        {
            try
            {
                if (sessionPhase == OverlaySessionPhase.Connecting)
                    return;
                if (!IsListening)
                    await StartTranscriptionAsync();
                else
                    await StopTranscriptionAsync();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[UI] Mic toggle error: {ex.Message}");
                ShowInlineError($"Transcription: {ex.Message}");
            }
        }

        private void ExpandToggle_Click(object sender, RoutedEventArgs e)
        {
            try { SetLiveInsightsExpanded(!isExpanded); }
            catch (Exception ex)
            {
                Console.WriteLine($"[UI] Expand toggle error: {ex.Message}");
            }
        }

        private void ExpandLiveInsights_MenuClick(object sender, RoutedEventArgs e)
            => SetLiveInsightsExpanded(true);

        private void CollapseLiveInsights_MenuClick(object sender, RoutedEventArgs e)
            => SetLiveInsightsExpanded(false);

        private void Menu_Click(object sender, RoutedEventArgs e)
        {
            if (MenuBtn.ContextMenu != null)
            {
                MenuBtn.ContextMenu.PlacementTarget = MenuBtn;
                MenuBtn.ContextMenu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
                MenuBtn.ContextMenu.IsOpen = true;
            }
        }

        private void KeyboardShortcuts_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var w = new KeyboardShortcutsWindow { Owner = this };
                w.ShowDialog();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[UI] Shortcuts window error: {ex.Message}");
            }
        }

        private void Experience_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                // Return to the dashboard and open the Experience editor there.
                SnapEye.Dashboard.Dashboard.RequestExperienceView = true;
                Close();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[UI] Experience open error: {ex.Message}");
            }
        }

        private void MicToggleFromMenu_Click(object sender, RoutedEventArgs e)
            => MicToggle_Click(sender, e);

        private void ToggleOverlayVisibility_Click(object sender, RoutedEventArgs e)
        {
            this.Visibility = this.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
        }

        private void OpacitySlider_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            this.Opacity = e.NewValue;
        }

        /// <summary>
        /// Step overlay opacity by <paramref name="delta"/>, clamped to
        /// <see cref="AppConfig.MinOpacity"/>..<see cref="AppConfig.MaxOpacity"/>,
        /// and keep the menu slider in sync.
        /// </summary>
        private void AdjustOpacity(double delta)
        {
            try
            {
                double next = Math.Clamp(
                    this.Opacity + delta,
                    AppConfig.MinOpacity,
                    AppConfig.MaxOpacity);
                this.Opacity = next;
                if (OpacitySlider != null)
                    OpacitySlider.Value = next;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Opacity] Adjust error: {ex.Message}");
            }
        }

        private void Quit_Click(object sender, RoutedEventArgs e)
        {
            Application.Current.Shutdown();
        }

        private void Close_Click(object sender, RoutedEventArgs e)
        {
            this.Visibility = Visibility.Collapsed;
        }

        /// <summary>
        /// Closes the current conversation thread and starts a fresh one, both locally
        /// (new session file) and on the backend (new active conversation row) - the two
        /// updates run independently so a network hiccup never blocks the local reset.
        /// </summary>
        private void NewConversation_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                history.StartNewSession();
                conversationSync.StartNewConversationAsync();
                AlphaRegion.ClearAll();
                AlphaRegion.SwitchToChatTab();
                AlphaRegion.SetMarkdownContent(
                    "**New conversation.**\n\nPrevious thread was saved. Ask AI, capture, or Listen to continue.");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[UI] New conversation error: {ex.Message}");
            }
        }

        private void ModeSelector_Changed(object sender, SelectionChangedEventArgs e)
        {
            try
            {
                selectedModeIndex = ModeSelector.SelectedIndex;
                if (selectedModeIndex >= 0 && selectedModeIndex < AppConfig.Modes.Count)
                {
                    var mode = AppConfig.Modes[selectedModeIndex];
                    Console.WriteLine($"[UI] Mode changed to: {mode.Name}");

                    // Interview (and every other mode) uses only the mode template from the
                    // prompts bar / config — no popup and no separate job-description form.
                    PushInterviewContextToSession();
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[UI] Mode change error: {ex.Message}");
            }
        }

        private void JobDetailsToggle_Changed(object sender, RoutedEventArgs e)
        {
            try
            {
                bool expanded = JobDetailsToggle.IsChecked == true;
                JobDetailsPanel.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed;
                if (expanded)
                {
                    // Populate from whatever was entered previously (if anything) each time
                    // the panel is opened.
                    JobCompanyBox.Text = interviewCompany;
                    JobRoleBox.Text = interviewRole;
                    JobDescriptionBox.Text = interviewJobDescription;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[UI] Job details toggle error: {ex.Message}");
            }
        }

        private void JobDetailsField_LostFocus(object sender, RoutedEventArgs e)
        {
            try
            {
                interviewCompany = JobCompanyBox.Text?.Trim() ?? "";
                interviewRole = JobRoleBox.Text?.Trim() ?? "";
                interviewJobDescription = JobDescriptionBox.Text?.Trim() ?? "";
                PushInterviewContextToSession();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[UI] Job details field error: {ex.Message}");
            }
        }

        private void PushInterviewContextToSession()
        {
            try
            {
                // Prefer mode name so the backend can detect "Behavioral Interview Questions"
                // (ids are often GUIDs that do not contain "interview" / "behavio").
                _ = transcriptionService.SendInterviewContextAsync(
                    "",
                    "",
                    "",
                    GetCurrentModeLabel(),
                    GetCurrentModeSystemPrompt());
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[UI] Push interview context error: {ex.Message}");
            }
        }

        private void TranscriptToggle_Changed(object sender, RoutedEventArgs e)
        {
            try
            {
                bool showTranscript = TranscriptToggle.IsChecked == true;
                if (showTranscript)
                    AlphaRegion.SwitchToTranscriptionTab();
                else
                    AlphaRegion.SwitchToChatTab();

                UpdateCameraVisibility();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[UI] Transcript toggle error: {ex.Message}");
            }
        }

        /// <summary>
        /// The capture / regenerate buttons are available in BOTH the Chat and Transcript
        /// (Listen) views — capturing &amp; extracting screen text is useful while listening too.
        /// </summary>
        private void UpdateCameraVisibility()
        {
            if (CameraBtn != null) CameraBtn.Visibility = Visibility.Visible;
        }

        #endregion

        #region Quick Actions

        private async void QuickAction_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (sender is Button btn && btn.Tag is QuickActionConfig action)
                {
                    if (action.Expand)
                        await ExplainMoreAsync(action);
                    else
                        await RunContextualPromptAsync(action.Label, action.Prompt);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[UI] Quick action error: {ex.Message}");
                AlphaRegion.ShowStreamingError(ex.Message);
            }
        }

        /// <summary>
        /// Same as clicking "What should I say?" — used by Left Ctrl + Right Ctrl.
        /// Safe to call from the keyboard-hook thread.
        /// </summary>
        public void TriggerWhatShouldISay()
        {
            try
            {
                Dispatcher.BeginInvoke(new Action(async () =>
                {
                    try
                    {
                        if (IsResponseStreaming)
                            return;

                        var action = AppConfig.QuickActions.FirstOrDefault(a =>
                            a.Label != null
                            && a.Label.IndexOf("What should I say", StringComparison.OrdinalIgnoreCase) >= 0);
                        if (action == null)
                        {
                            ShowInlineError("No \"What should I say?\" quick action is configured.");
                            return;
                        }

                        ExpandSessionIfCollapsed();
                        await RunContextualPromptAsync(action.Label, action.Prompt);
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[UI] What-should-I-say trigger error: {ex.Message}");
                    }
                }));
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[UI] What-should-I-say dispatch error: {ex.Message}");
            }
        }

        /// <summary>
        /// "Explain more": expands the most recent AI answer into a 1-2 minute read. The
        /// previous answer is quoted in the prompt so the model expands exactly that text,
        /// and the request is sent without the quick-action length cap.
        /// </summary>
        private async Task ExplainMoreAsync(QuickActionConfig action)
        {
            string? lastAnswer = history.GetLastAssistantAnswer();
            if (string.IsNullOrWhiteSpace(lastAnswer))
            {
                ShowInlineError("Get an answer first, then tap Explain more.");
                return;
            }

            string prompt = action.Prompt + "\n\nPREVIOUS ANSWER TO EXPAND:\n" + lastAnswer;
            await RunContextualPromptAsync(action.Label, prompt, expand: true);
        }

        private async Task RunContextualPromptAsync(string displayLabel, string userPrompt, bool expand = false)
        {
            if (!await EnsureAuthTokenFreshAsync().ConfigureAwait(true))
                return;

            ExpandSessionIfCollapsed();
            ClearInlineError();

            // Quick actions route to the *current* view. They intentionally use only the conversation
            // context (typed/spoken Q&A and transcription) — NOT any pending screen OCR — so clicking
            // a quick action never silently injects a stale screenshot.
            SetLastQuestion(userPrompt, inTranscript: false);

            history.Append(ConversationMessageKind.QuickAction, userPrompt, displayLabel);
            AlphaRegion.BeginStreamingResponse(displayLabel);

            manualStreaming = true;
            UpdateStopButton();

            string systemPrompt = GetCurrentSystemPrompt();
            string context = BuildConversationContext();

            // Route through the context endpoint so quick actions are also grounded in the
            // user's saved experience (and the job description while in interview mode).
            await streamingService.StreamContextResponseAsync(
                userPrompt,
                context,
                "",
                "",
                systemPrompt,
                null,
                jobDescription: "",
                company: "",
                role: "",
                mode: GetCurrentModeLabel(),
                history: history.GetRecentDialogue(),
                quickAction: !expand,
                expand: expand);
        }

        private void ExpandSessionIfCollapsed()
        {
            if (isExpanded) return;
            SetLiveInsightsExpanded(true);
        }

        #endregion

        #region Chat Input

        private void ChatInput_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter && (Keyboard.Modifiers == ModifierKeys.Control || Keyboard.Modifiers == ModifierKeys.None))
            {
                Send_Click(sender, e);
                e.Handled = true;
            }
        }

        private async void Send_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                string query = ChatInput.Text?.Trim() ?? "";
                if (string.IsNullOrEmpty(query)) return;

                ChatInput.Text = "";

                ExpandSessionIfCollapsed();
                ClearInlineError();

                // Typed prompts + answers always append to the shared session panel.
                SetLastQuestion(query, inTranscript: false);

                history.Append(ConversationMessageKind.UserTyped, query);
                AlphaRegion.BeginStreamingResponse(query);

                await StreamUserDirectedLlmAsync(query);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[UI] Send error: {ex.Message}");
                AlphaRegion.ShowStreamingError(ex.Message);
            }
        }

        private async void Screenshot_Click(object? sender, RoutedEventArgs e)
        {
            await CaptureScreenAndRouteAsync();
        }

        private async void CameraBtn_Click(object sender, RoutedEventArgs e)
        {
            await CaptureScreenAndRouteAsync();
        }

        private async void RegenBtn_Click(object sender, RoutedEventArgs e)
        {
            await RegenerateLastAnswerAsync();
        }

        /// <summary>
        /// Re-answers the most recent question, instructing the model to keep the same answer
        /// but explain it differently (fresh wording / angle). Routes to whichever view the
        /// original answer was shown in.
        /// </summary>
        private async Task RegenerateLastAnswerAsync()
        {
            try
            {
                if (string.IsNullOrWhiteSpace(lastAnsweredQuestion))
                {
                    ShowInlineError("Ask something first, then tap Answer again.");
                    return;
                }

                ExpandSessionIfCollapsed();
                ClearInlineError();


                string question = lastAnsweredQuestion!;
                AlphaRegion.BeginStreamingResponse(question);

                string regenInstruction = question +
                    "\n\nAnswer the SAME question again, but give a DIFFERENT explanation: " +
                    "use different wording, a fresh angle or example, and do not repeat your previous phrasing.";

                await StreamUserDirectedLlmAsync(regenInstruction);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[UI] Regenerate error: {ex.Message}");
                AlphaRegion.ShowStreamingError(ex.Message);
            }
        }

        /// <summary>
        /// Captures the screen. In Play mode: OCR + stream immediately.
        /// In Pause (batch) mode: buffer the PNG only until the user clicks Play.
        /// </summary>
        private async Task CaptureScreenAndRouteAsync()
        {
            // Atomic 0->1 swap. If a capture is already running, bail out so we don't
            // double-fire OCR + LLM streams from a rapid double-click.
            if (Interlocked.Exchange(ref captureInFlight, 1) == 1)
                return;

            // Disable the camera button while capture is in flight so the user can't
            // visibly click it again and wonder why nothing's happening.
            var cameraBtn = CameraBtn;
            if (cameraBtn != null) cameraBtn.IsEnabled = false;

            try
            {
                ExpandSessionIfCollapsed();
                ClearInlineError();

                if (IsBatchCaptureMode)
                {
                    await BufferCaptureForBatchAsync().ConfigureAwait(true);
                    return;
                }

                // Capture the monitor SnapEye is currently on (not always the primary),
                // and persist the PNG under %AppData%\SnapEye\captures.
                string? ocrText = await screenCaptureService.CaptureAndOcrAsync(this);
                string displayText = string.IsNullOrWhiteSpace(ocrText)
                    ? "(No text could be extracted from the current screen.)"
                    : ocrText!;

                if (!string.IsNullOrEmpty(screenCaptureService.LastSavedCapturePath))
                    Console.WriteLine($"[Screen] Local capture: {screenCaptureService.LastSavedCapturePath}");

                AlphaRegion.AddScreenCaptureToChat(displayText);

                if (!string.IsNullOrWhiteSpace(ocrText))
                {
                    lastScreenOcr = ocrText;
                    if (IsListening && transcriptionService.IsConnected)
                        await transcriptionService.SendScreenContextAsync(ocrText);
                }

                // The model reads the screenshot itself, so answer even when OCR found nothing.
                string? imageBase64 = screenCaptureService.LastCaptureBase64;
                if (!string.IsNullOrWhiteSpace(ocrText) || !string.IsNullOrEmpty(imageBase64))
                    await StreamAnswerForScreenCaptureAsync(ocrText ?? "", imageBase64);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Screen] Capture error: {ex.Message}");
                ShowInlineError($"Capture error: {ex.Message}");
            }
            finally
            {
                if (cameraBtn != null) cameraBtn.IsEnabled = true;
                Interlocked.Exchange(ref captureInFlight, 0);
                // User clicked Play while this capture was still running — start OCR now.
                TryStartPendingBatchFinalize();
            }
        }

        private async Task BufferCaptureForBatchAsync()
        {
            if (pendingCaptures.Count >= MaxPendingCaptures)
            {
                ShowInlineError($"Batch full ({MaxPendingCaptures}). Click Play to send, or remove a thumbnail.");
                return;
            }

            string? base64 = await screenCaptureService.CaptureActiveWindowAsync(this).ConfigureAwait(true);
            if (string.IsNullOrEmpty(base64))
            {
                ShowInlineError("Capture failed — nothing was added to the batch.");
                return;
            }

            pendingCaptures.Add(new PendingCapture
            {
                Base64 = base64,
                SavedPath = screenCaptureService.LastSavedCapturePath,
                Thumbnail = TryCreateThumbnail(base64),
            });
            Console.WriteLine($"[Screen] Batched capture {pendingCaptures.Count}/{MaxPendingCaptures}: {screenCaptureService.LastSavedCapturePath}");
            RefreshPendingCapturesUi();
            ClearInlineError();
        }

        /// <summary>
        /// Flip Pause/Play capture mode (same as the toolbar toggle / Ctrl+Shift+P).
        /// Pausing buffers camera shots; unpausing starts concurrent OCR + send.
        /// </summary>
        private void ToggleCaptureBatchMode()
        {
            if (CaptureBatchToggle == null) return;
            CaptureBatchToggle.IsChecked = CaptureBatchToggle.IsChecked != true;
        }

        private void CaptureBatchToggle_Changed(object sender, RoutedEventArgs e)
        {
            try
            {
                if (IsBatchCaptureMode)
                {
                    // Entered Pause / collect mode.
                    finalizeBatchWhenIdle = false;
                    RefreshPendingCapturesUi();
                    ShowInlineError("Capture paused — camera buffers screenshots. Ctrl+Shift+P or Play to extract.");
                    return;
                }

                // Left Pause → Play: start extraction immediately on any buffered screenshots.
                if (pendingCaptures.Count > 0)
                    StartBatchExtractionNow();
                else
                {
                    RefreshPendingCapturesUi();
                    ClearInlineError();
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Screen] Batch toggle error: {ex.Message}");
                ShowInlineError($"Batch mode error: {ex.Message}");
            }
        }

        /// <summary>
        /// Kick off concurrent OCR + LLM as soon as the user leaves Pause (Play).
        /// If a camera capture is still in flight, queue finalize for when it finishes.
        /// </summary>
        private void StartBatchExtractionNow()
        {
            if (pendingCaptures.Count == 0) return;

            ShowInlineError($"Extracting text from {pendingCaptures.Count} screen{(pendingCaptures.Count == 1 ? "" : "s")}…");

            if (Volatile.Read(ref captureInFlight) == 1)
            {
                finalizeBatchWhenIdle = true;
                return;
            }

            finalizeBatchWhenIdle = false;
            _ = FinalizeBatchCaptureAsync();
        }

        private void TryStartPendingBatchFinalize()
        {
            if (!finalizeBatchWhenIdle) return;
            if (IsBatchCaptureMode) { finalizeBatchWhenIdle = false; return; }
            if (pendingCaptures.Count == 0) { finalizeBatchWhenIdle = false; return; }
            if (Volatile.Read(ref captureInFlight) == 1) return;

            finalizeBatchWhenIdle = false;
            ShowInlineError($"Extracting text from {pendingCaptures.Count} screen{(pendingCaptures.Count == 1 ? "" : "s")}…");
            _ = FinalizeBatchCaptureAsync();
        }

        private async Task FinalizeBatchCaptureAsync()
        {
            if (pendingCaptures.Count == 0) return;
            if (Interlocked.Exchange(ref captureInFlight, 1) == 1)
            {
                // Another capture/finalize owns the lock — retry when it releases.
                finalizeBatchWhenIdle = true;
                return;
            }

            var cameraBtn = CameraBtn;
            if (cameraBtn != null) cameraBtn.IsEnabled = false;
            if (CaptureBatchToggle != null) CaptureBatchToggle.IsEnabled = false;

            var batch = pendingCaptures.ToList();
            try
            {
                ExpandSessionIfCollapsed();

                // Sequential OCR: parallel jobs thrash Tesseract in the container (60–90s+)
                // and used to hit the old 30s client timeout → false "OCR found no text".
                var ocrTexts = new string?[batch.Count];
                var ocrErrors = new string?[batch.Count];
                for (int i = 0; i < batch.Count; i++)
                {
                    ShowInlineError($"Extracting text from screen {i + 1}/{batch.Count}…");
                    ocrTexts[i] = await screenCaptureService
                        .SendToOcrAsync(batch[i].Base64, batch[i].SavedPath)
                        .ConfigureAwait(true);
                    ocrErrors[i] = screenCaptureService.LastOcrError;
                }

                var displayParts = new List<string>();
                for (int i = 0; i < batch.Count; i++)
                {
                    string ocr = FormatOcrDisplay(ocrTexts[i], ocrErrors[i]);
                    displayParts.Add($"Screen {i + 1}:\n{ocr}");
                }

                string combinedDisplay = string.Join("\n\n---\n\n", displayParts);
                AlphaRegion.AddScreenCaptureToChat(combinedDisplay);

                lastScreenOcr = string.Join("\n\n", ocrTexts.Where(t => !string.IsNullOrWhiteSpace(t)));
                if (!string.IsNullOrWhiteSpace(lastScreenOcr) && IsListening && transcriptionService.IsConnected)
                    await transcriptionService.SendScreenContextAsync(lastScreenOcr);

                ClearInlineError();
                pendingCaptures.Clear();
                RefreshPendingCapturesUi();

                await StreamAnswerForScreenCaptureBatchAsync(batch, ocrTexts).ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Screen] Batch finalize error: {ex.Message}");
                ShowInlineError($"Batch send error: {ex.Message}");
            }
            finally
            {
                if (cameraBtn != null) cameraBtn.IsEnabled = true;
                if (CaptureBatchToggle != null) CaptureBatchToggle.IsEnabled = true;
                Interlocked.Exchange(ref captureInFlight, 0);
                TryStartPendingBatchFinalize();
            }
        }

        private static string FormatOcrDisplay(string? ocrText, string? error)
        {
            if (ocrText == null)
                return string.IsNullOrWhiteSpace(error)
                    ? "(OCR failed — see status bar)"
                    : $"(OCR failed: {error})";
            if (string.IsNullOrWhiteSpace(ocrText))
                return "(OCR found no text)";
            return ocrText;
        }

        private async Task StreamAnswerForScreenCaptureAsync(string ocrText, string? imageBase64)
        {
            if (!await EnsureAuthTokenFreshAsync().ConfigureAwait(true))
                return;

            SetLastQuestion("Based on the captured screen, explain what would help me right now.", inTranscript: false);

            const string displayLabel = "Screen capture";
            AlphaRegion.BeginStreamingResponse(displayLabel);

            manualStreaming = true;
            UpdateStopButton();

            string systemPrompt = GetCurrentSystemPrompt();
            string context = BuildConversationContext();

            var parts = new StringBuilder();
            if (!string.IsNullOrEmpty(context))
                parts.AppendLine(context);
            bool hasImage = !string.IsNullOrEmpty(imageBase64);
            parts.AppendLine(hasImage ? "SCREEN CAPTURE (OCR, rough backup only):" : "SCREEN CAPTURE (OCR):");
            parts.AppendLine("---");
            parts.AppendLine(FormatOcrDisplay(ocrText, null));
            parts.AppendLine("---");
            parts.AppendLine(hasImage
                ? "USER REQUEST: The attached image is my screen. Read it directly; the OCR text above is only a rough backup. Respond exactly as your system instructions say (for example, if it mentions LLD, HLD or system design, follow that section)."
                : "USER REQUEST: This is the text captured from my screen. Respond to it exactly as your system instructions say (for example, if it mentions LLD, HLD or system design, follow that section).");

            await streamingService.StreamResponseAsync(
                parts.ToString(), systemPrompt, history: history.GetRecentDialogue(), imageBase64: imageBase64);
        }

        private async Task StreamAnswerForScreenCaptureBatchAsync(
            IReadOnlyList<PendingCapture> batch,
            IReadOnlyList<string?> ocrTexts)
        {
            if (!await EnsureAuthTokenFreshAsync().ConfigureAwait(true))
                return;

            SetLastQuestion($"Based on {batch.Count} captured screens, explain what would help me right now.", inTranscript: false);

            string displayLabel = batch.Count == 1 ? "Screen capture" : $"{batch.Count} screen captures";
            AlphaRegion.BeginStreamingResponse(displayLabel);

            manualStreaming = true;
            UpdateStopButton();

            string systemPrompt = GetCurrentSystemPrompt();
            string context = BuildConversationContext();

            var wireParts = new List<StreamingResponseService.MessagePart>();
            if (!string.IsNullOrEmpty(context))
                wireParts.Add(StreamingResponseService.MessagePart.TextBlock(context.TrimEnd()));

            for (int i = 0; i < batch.Count; i++)
            {
                string ocr = FormatOcrDisplay(ocrTexts[i], null);
                wireParts.Add(StreamingResponseService.MessagePart.TextBlock(
                    $"SCREEN CAPTURE {i + 1} of {batch.Count} (OCR, rough backup only):\n---\n{ocr}\n---"));
                wireParts.Add(StreamingResponseService.MessagePart.ImagePng(batch[i].Base64));
            }

            wireParts.Add(StreamingResponseService.MessagePart.TextBlock(
                "USER REQUEST: The attached images are my screens in order. Read each image directly; " +
                "the OCR text above each image is only a rough backup. Respond exactly as your system " +
                "instructions say (for example, if it mentions LLD, HLD or system design, follow that section)."));

            await streamingService.StreamResponseAsync(
                $"Multi-screen capture ({batch.Count} images).",
                systemPrompt,
                history: history.GetRecentDialogue(),
                parts: wireParts);
        }

        private void RefreshPendingCapturesUi()
        {
            try
            {
                if (PendingCapturesHost == null || PendingCapturesPanel == null) return;

                PendingCapturesPanel.Children.Clear();
                bool show = pendingCaptures.Count > 0;
                PendingCapturesHost.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
                if (!show) return;

                for (int i = 0; i < pendingCaptures.Count; i++)
                {
                    int index = i;
                    var item = pendingCaptures[i];

                    var thumb = new Border
                    {
                        Width = 96,
                        Height = 60,
                        CornerRadius = new CornerRadius(8),
                        BorderBrush = new SolidColorBrush(Color.FromRgb(0x52, 0x52, 0x5B)),
                        BorderThickness = new Thickness(1),
                        Margin = new Thickness(0, 0, 8, 0),
                        Background = new SolidColorBrush(Color.FromRgb(0x27, 0x27, 0x2A)),
                        ClipToBounds = true,
                    };

                    var grid = new Grid();
                    if (item.Thumbnail != null)
                    {
                        grid.Children.Add(new Image
                        {
                            Source = item.Thumbnail,
                            Stretch = Stretch.UniformToFill,
                        });
                    }

                    grid.Children.Add(new Border
                    {
                        Background = new SolidColorBrush(Color.FromArgb(0xB3, 0x00, 0x00, 0x00)),
                        HorizontalAlignment = HorizontalAlignment.Left,
                        VerticalAlignment = VerticalAlignment.Top,
                        Margin = new Thickness(4),
                        CornerRadius = new CornerRadius(4),
                        Padding = new Thickness(5, 1, 5, 1),
                        Child = new TextBlock
                        {
                            Text = (index + 1).ToString(),
                            Foreground = Brushes.White,
                            FontSize = 11,
                            FontWeight = FontWeights.SemiBold,
                        }
                    });

                    var remove = new Button
                    {
                        Content = "×",
                        Width = 18,
                        Height = 18,
                        FontSize = 12,
                        Padding = new Thickness(0),
                        Margin = new Thickness(0, 2, 2, 0),
                        HorizontalAlignment = HorizontalAlignment.Right,
                        VerticalAlignment = VerticalAlignment.Top,
                        Cursor = Cursors.Hand,
                        ToolTip = "Remove from batch",
                        Background = new SolidColorBrush(Color.FromArgb(0xCC, 0x00, 0x00, 0x00)),
                        Foreground = Brushes.White,
                        BorderThickness = new Thickness(0),
                    };
                    remove.Click += (_, _) =>
                    {
                        if (index >= 0 && index < pendingCaptures.Count)
                        {
                            pendingCaptures.RemoveAt(index);
                            RefreshPendingCapturesUi();
                        }
                    };
                    grid.Children.Add(remove);
                    thumb.Child = grid;
                    PendingCapturesPanel.Children.Add(thumb);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Screen] Pending UI error: {ex.Message}");
            }
        }

        private static ImageSource? TryCreateThumbnail(string base64)
        {
            try
            {
                byte[] bytes = Convert.FromBase64String(base64);
                using var ms = new MemoryStream(bytes);
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.StreamSource = ms;
                bmp.DecodePixelWidth = 160;
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.EndInit();
                bmp.Freeze();
                return bmp;
            }
            catch
            {
                return null;
            }
        }

        private void CopyAnswer_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                string md = AlphaRegion.GetChatMarkdown();
                if (string.IsNullOrWhiteSpace(md))
                {
                    ShowInlineError("Nothing to copy yet — wait for an answer or ask a question first.");
                    return;
                }
                Clipboard.SetText(md);
                ClearInlineError();
            }
            catch (Exception ex)
            {
                ShowInlineError($"Copy failed: {ex.Message}");
            }
        }

        private async Task StreamUserDirectedLlmAsync(string question)
        {
            if (!await EnsureAuthTokenFreshAsync().ConfigureAwait(true))
                return;
            // Show the Stop control immediately so the user can cancel even before the first
            // token arrives.
            manualStreaming = true;
            UpdateStopButton();
            string transcript = BuildConversationContext();
            string systemPrompt = GetCurrentSystemPrompt();
            await streamingService.StreamContextResponseAsync(
                question,
                transcript,
                "",
                lastScreenOcr ?? "",
                systemPrompt,
                null,
                jobDescription: "",
                company: "",
                role: "",
                mode: GetCurrentModeLabel(),
                history: history.GetRecentDialogue());
        }

        private string GetCurrentModeId()
        {
            if (selectedModeIndex >= 0 && selectedModeIndex < AppConfig.Modes.Count)
                return AppConfig.Modes[selectedModeIndex].Id ?? "";
            return "";
        }

        /// <summary>Mode name plus id for backend routing (behavioral length rules, interview
        /// grounding). Name is required because custom modes often use opaque GUID ids.</summary>
        private string GetCurrentModeLabel()
        {
            if (selectedModeIndex < 0 || selectedModeIndex >= AppConfig.Modes.Count)
                return "";
            var m = AppConfig.Modes[selectedModeIndex];
            string name = m.Name ?? "";
            string id = m.Id ?? "";
            if (!string.IsNullOrWhiteSpace(name) && !string.IsNullOrWhiteSpace(id))
                return $"{name}|{id}";
            return !string.IsNullOrWhiteSpace(name) ? name : id;
        }

        /// <summary>Raw template text of the currently-selected mode (no appended suffix),
        /// sent to the live session so auto-suggestions strictly follow the chosen mode.</summary>
        private string GetCurrentModeSystemPrompt()
        {
            if (selectedModeIndex >= 0 && selectedModeIndex < AppConfig.Modes.Count)
                return AppConfig.Modes[selectedModeIndex].SystemPrompt ?? "";
            return "";
        }

        private bool IsInterviewMode()
        {
            string label = GetCurrentModeLabel();
            return label.IndexOf("interview", StringComparison.OrdinalIgnoreCase) >= 0
                || label.IndexOf("behavio", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private string GetCurrentSystemPrompt()
        {
            string modePrompt = "You are SnapEye, a helpful AI assistant.";
            if (selectedModeIndex >= 0 && selectedModeIndex < AppConfig.Modes.Count)
                modePrompt = AppConfig.Modes[selectedModeIndex].SystemPrompt;

            return modePrompt +
                "\n\nIMPORTANT: You have access to the live conversation transcript provided in the user message. " +
                "Never say you don't have access to the conversation. " +
                "Put code, ASCII diagrams, and anything that depends on alignment inside ``` fenced code blocks.";
        }

        private string BuildConversationContext()
        {
            try
            {
                // Take a lock-protected snapshot of the latest 30 messages so we don't race
                // with the WebSocket receive thread appending to the list mid-iteration.
                var tail = transcriptionService.Conversation.GetTail(30);
                if (tail.Count == 0) return "";

                var sb = new StringBuilder(tail.Count * 48);
                sb.AppendLine("LIVE CONVERSATION TRANSCRIPT:");
                sb.AppendLine("---");
                foreach (var msg in tail)
                {
                    string speaker = msg.Source == MessageSource.Microphone ? "You" : "Other";
                    sb.Append('[').Append(speaker).Append("]: ").AppendLine(msg.Text);
                }
                sb.AppendLine("---");
                return sb.ToString();
            }
            catch
            {
                return "";
            }
        }

        #endregion

        #region Transcription Control

        /// <summary>
        /// Wait for the backend to become reachable, up to <paramref name="timeout"/>.
        /// BackendProcessService.EnsureBackendAsync() already does its own bounded /health
        /// polling internally (idempotent - reuses a start already in flight), so this is
        /// just a single wait against that one readiness signal instead of layering a
        /// second, redundant polling loop on top of it.
        /// </summary>
        private async Task<bool> WaitForBackendReadyAsync(TimeSpan timeout)
        {
            try
            {
                var readyTask = BackendProcessService.EnsureBackendAsync();
                var finished = await Task.WhenAny(readyTask, Task.Delay(timeout)).ConfigureAwait(false);
                return finished == readyTask && await readyTask.ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Transcription] Backend readiness wait error: {ex.Message}");
                return false;
            }
        }

        private async Task StartTranscriptionAsync()
        {
            try
            {
                ClearInlineError();

                // Backend readiness was kicked off at app startup; reuse that task instead of
                // probing again (saves a round-trip on every Listen click).
                bool reachable = BackendProcessService.Ready.IsCompletedSuccessfully
                                 && BackendProcessService.Ready.Result;

                if (!reachable)
                {
                    sessionPhase = OverlaySessionPhase.Connecting;
                    UpdateConnectingUI(true);
                    // Lighter copy — Podman auto-start (if needed) runs inside EnsureBackendAsync.
                    ShowInlineError(BackendProcessService.PodmanStackStarting
                        ? "Connecting to SnapEye…"
                        : "Connecting…");

                    reachable = await WaitForBackendReadyAsync(TimeSpan.FromSeconds(90));
                    UpdateConnectingUI(false);
                    ClearInlineError();
                }

                if (!reachable)
                {
                    sessionPhase = OverlaySessionPhase.Ready;
                    UpdateListeningUI(false);
                    string detail = BackendProcessService.LastStartError;
                    ShowInlineError(string.IsNullOrWhiteSpace(detail)
                        ? "Backend not reachable. Start the SnapEye backend and try again."
                        : $"Backend didn't start: {detail}");
                    return;
                }

                if (!await EnsureAuthTokenFreshAsync().ConfigureAwait(true))
                {
                    sessionPhase = OverlaySessionPhase.Ready;
                    UpdateListeningUI(false);
                    return;
                }

                sessionPhase = OverlaySessionPhase.Connecting;

                // Warm up the audio devices IN PARALLEL with the WebSocket handshake instead of
                // after it. WASAPI/WaveIn init can stall 100-1000ms while COM and the driver wake
                // up; overlapping it with the connect roundtrip removes that from the perceived
                // start latency. Any audio produced before the socket is open is harmlessly
                // dropped by SendAudioAsync (it checks the connection state first).
                var captureWarmup = Task.Run(() =>
                {
                    audioCaptureService.StartCapture();
                    speakerCaptureService.StartCapture();
                });

                bool connected = await transcriptionService.ConnectAsync(
                    AppConfig.DeepgramModel,
                    AppConfig.DeepgramLanguage,
                    AppConfig.EndpointingMs
                );

                if (connected)
                {
                    sessionPhase = OverlaySessionPhase.Listening;

                    // Make sure the capture warm-up finished (usually already done by now).
                    await captureWarmup;

                    UpdateListeningUI(true);
                    StartSessionTimer();

                    if (!string.IsNullOrWhiteSpace(lastScreenOcr))
                        await transcriptionService.SendScreenContextAsync(lastScreenOcr);

                    // If interview mode is already active, hand the session the JD/mode so the
                    // live auto-suggestion pipeline tailors answers from the first utterance.
                    PushInterviewContextToSession();

                    ExpandSessionIfCollapsed();

                    AlphaRegion.SwitchToTranscriptionTab();
                    TranscriptToggle.IsChecked = true;
                }
                else
                {
                    sessionPhase = OverlaySessionPhase.Ready;
                    // Connect failed after we warmed the mic/speaker — release the devices.
                    await captureWarmup;
                    await Task.Run(() =>
                    {
                        audioCaptureService.StopCapture();
                        speakerCaptureService.StopCapture();
                    });
                    UpdateListeningUI(false);
                    ShowInlineError("Failed to connect to transcription service.");
                }
            }
            catch (Exception ex)
            {
                sessionPhase = OverlaySessionPhase.Ready;
                UpdateListeningUI(false);
                Console.WriteLine($"[Transcription] Start error: {ex.Message}");
                ShowInlineError($"Connection error: {ex.Message}");
            }
        }

        private async Task StopTranscriptionAsync()
        {
            try
            {
                streamingService.CancelCurrentStream();
                sessionPhase = OverlaySessionPhase.Ready;

                // Symmetric with StartTranscriptionAsync: NAudio's StopRecording can also
                // hitch briefly when releasing the WASAPI client, so push it off the UI.
                await Task.Run(() =>
                {
                    audioCaptureService.StopCapture();
                    speakerCaptureService.StopCapture();
                });

                await transcriptionService.DisconnectAsync();

                UpdateListeningUI(false);
                StopSessionTimer();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Transcription] Stop error: {ex.Message}");
            }
        }

        private void UpdateListeningUI(bool listening)
        {
            // Avoid the cross-thread roundtrip when we're already on the UI thread
            // (most callers are). If we're off-thread, marshal asynchronously so the
            // caller doesn't block waiting for the dispatcher.
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke(new Action(() => UpdateListeningUI(listening)));
                return;
            }
            // Inline body (no nested Invoke):
            {
                var liveDot = FindLiveDot();
                var listenBorder = FindListenBtnBorder();
                var listenIcon = FindListenIcon();

                if (listening)
                {
                    if (liveDot != null) liveDot.Visibility = Visibility.Visible;

                    if (listenIcon != null)
                        listenIcon.Fill = new SolidColorBrush(Color.FromRgb(0xFC, 0xA5, 0xA5));

                    if (listenBorder != null)
                    {
                        listenBorder.Background = new SolidColorBrush(Color.FromArgb(0x55, 0xDC, 0x26, 0x26));
                        listenBorder.BorderBrush = new SolidColorBrush(Color.FromRgb(0xDC, 0x26, 0x26));
                        listenBorder.Effect = new System.Windows.Media.Effects.DropShadowEffect
                        {
                            Color = Color.FromRgb(0xDC, 0x26, 0x26),
                            BlurRadius = 12,
                            ShadowDepth = 0,
                            Opacity = 0.5
                        };
                    }

                    StartLiveDotPulse();
                }
                else
                {
                    if (liveDot != null) liveDot.Visibility = Visibility.Collapsed;

                    if (listenIcon != null)
                        listenIcon.Fill = new SolidColorBrush(Color.FromRgb(0x93, 0xC5, 0xFD));

                    if (listenBorder != null)
                    {
                        listenBorder.Background = new SolidColorBrush(Color.FromArgb(0x33, 0x25, 0x63, 0xEB));
                        listenBorder.BorderBrush = new SolidColorBrush(Color.FromRgb(0x25, 0x63, 0xEB));
                        listenBorder.Effect = new System.Windows.Media.Effects.DropShadowEffect
                        {
                            Color = Color.FromRgb(0x25, 0x63, 0xEB),
                            BlurRadius = 10,
                            ShadowDepth = 0,
                            Opacity = 0.35
                        };
                    }

                    StopLiveDotPulse();
                }
            }
        }

        /// <summary>
        /// Lightweight "connecting" cue while the backend is starting up: tints the listen
        /// icon amber and pulses it. Cleared when connecting resolves (listening or error).
        /// </summary>
        private void UpdateConnectingUI(bool connecting)
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke(new Action(() => UpdateConnectingUI(connecting)));
                return;
            }

            var listenIcon = FindListenIcon();
            var listenBorder = FindListenBtnBorder();

            if (connecting)
            {
                if (listenIcon != null)
                    listenIcon.Fill = new SolidColorBrush(Color.FromRgb(0xFD, 0xE0, 0x47)); // amber
                if (listenBorder != null)
                    listenBorder.BorderBrush = new SolidColorBrush(Color.FromRgb(0xF5, 0x9E, 0x0B));
                StartLiveDotPulse();
            }
            else
            {
                StopLiveDotPulse();
                // Restore the idle look; StartTranscriptionAsync will set the listening
                // look via UpdateListeningUI when it connects, or leave idle on failure.
                UpdateListeningUI(false);
            }
        }

        private void StartLiveDotPulse()
        {
            liveDotPulseTimer?.Stop();
            liveDotPulseTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
            liveDotPulseTimer.Tick += (s, e) =>
            {
                var liveDot = FindLiveDot();
                if (liveDot != null)
                {
                    liveDotVisible = !liveDotVisible;
                    liveDot.Opacity = liveDotVisible ? 1.0 : 0.3;
                }
            };
            liveDotPulseTimer.Start();
        }

        private void StopLiveDotPulse()
        {
            liveDotPulseTimer?.Stop();
            liveDotPulseTimer = null;
        }

        private Ellipse? FindLiveDot()
        {
            return FindTemplateChild<Ellipse>(MicToggleBtn, "LiveDot");
        }

        private Border? FindListenBtnBorder()
        {
            return FindTemplateChild<Border>(MicToggleBtn, "ListenBtnBorder");
        }

        private System.Windows.Shapes.Path? FindListenIcon()
        {
            return FindTemplateChild<System.Windows.Shapes.Path>(MicToggleBtn, "ListenIcon");
        }

        private static T? FindTemplateChild<T>(Control parent, string name) where T : FrameworkElement
        {
            if (parent.Template == null) return null;
            return parent.Template.FindName(name, parent) as T;
        }

        #endregion

        #region Inline Toolbar Error

        /// <summary>
        /// Show a small inline red pill next to the toolbar (right side) instead of plastering
        /// the chat/transcript view with large red error cards. The pill auto-clears after 6s,
        /// and is also cleared whenever a new action starts.
        /// </summary>
        private void ShowInlineError(string? message)
        {
            // CheckAccess: callers from the UI thread (Send_Click, button handlers) avoid
            // a needless cross-thread roundtrip; service-thread callers go through
            // BeginInvoke so they don't block waiting for the dispatcher.
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke(new Action(() => ShowInlineError(message)));
                return;
            }

            if (InlineErrorPill == null || InlineErrorText == null) return;
            if (string.IsNullOrWhiteSpace(message))
            {
                InlineErrorPill.Visibility = Visibility.Collapsed;
                return;
            }

            InlineErrorText.Text = message!.Length > 140 ? message.Substring(0, 137) + "..." : message;
            InlineErrorPill.ToolTip = message;
            InlineErrorPill.Visibility = Visibility.Visible;

            inlineErrorDismissTimer?.Stop();
            inlineErrorDismissTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(6) };
            inlineErrorDismissTimer.Tick += (s, e) =>
            {
                inlineErrorDismissTimer?.Stop();
                inlineErrorDismissTimer = null;
                if (InlineErrorPill != null) InlineErrorPill.Visibility = Visibility.Collapsed;
            };
            inlineErrorDismissTimer.Start();
        }

        private void ClearInlineError()
        {
            if (InlineErrorPill != null) InlineErrorPill.Visibility = Visibility.Collapsed;
            inlineErrorDismissTimer?.Stop();
            inlineErrorDismissTimer = null;
        }

        #endregion

        #region Session Timer

        private void StartSessionTimer()
        {
            // Drive the HH:MM:SS / MM:SS pill between the Listen and End buttons. Format adapts:
            // under one hour we use MM:SS; at or past one hour we switch to HH:MM:SS so the user
            // always knows how long the live session has been running.
            sessionStartTime = DateTime.Now;
            StopSessionTimer();

            if (LiveTimerPill != null)
            {
                LiveTimerPill.Visibility = Visibility.Visible;
                if (LiveTimerText != null) LiveTimerText.Text = "00:00";
            }

            sessionTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            sessionTimer.Tick += (s, e) => UpdateLiveTimerText();
            sessionTimer.Start();
            UpdateLiveTimerText();
        }

        private void StopSessionTimer()
        {
            if (sessionTimer != null)
            {
                sessionTimer.Stop();
                sessionTimer = null;
            }
            if (LiveTimerPill != null)
                LiveTimerPill.Visibility = Visibility.Collapsed;
        }

        private void UpdateLiveTimerText()
        {
            if (LiveTimerText == null) return;
            TimeSpan elapsed = DateTime.Now - sessionStartTime;
            if (elapsed.TotalSeconds < 0) elapsed = TimeSpan.Zero;
            LiveTimerText.Text = elapsed.TotalHours >= 1
                ? $"{(int)elapsed.TotalHours:D2}:{elapsed.Minutes:D2}:{elapsed.Seconds:D2}"
                : $"{elapsed.Minutes:D2}:{elapsed.Seconds:D2}";
        }

        #endregion

        #region Transcription Events

        private void OnTranscriptionReceived(object? sender, TranscriptionMessage msg)
        {
            // BeginInvoke (async): the WebSocket receive thread fires this many times per
            // second. A synchronous Dispatcher.Invoke would block the receive loop until
            // the UI processed each message, building back-pressure on the socket and
            // freezing both ends. BeginInvoke queues and returns immediately.
            try
            {
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    // Each event carries the FULL utterance text with a stable message id:
                    // same id updates the existing bubble/history entry in place (no
                    // duplicated words/sentences), a new id starts a new one.
                    if (msg.Source == MessageSource.Microphone)
                    {
                        AlphaRegion.AddUserTranscription(msg.Text, msg.IsNewSegment, msg.MessageId);
                        history.UpsertSpoken(ConversationMessageKind.UserSpoken, msg.MessageId, msg.Text, msg.IsFinal);
                    }
                    else
                    {
                        AlphaRegion.AddSystemTranscription(msg.Text, msg.IsNewSegment, msg.MessageId);
                        history.UpsertSpoken(ConversationMessageKind.OtherSpoken, msg.MessageId, msg.Text, msg.IsFinal);
                    }
                }));
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[UI] Transcription display error: {ex.Message}");
            }
        }

        private void OnTranscriptionConnected(object? sender, EventArgs e)
        {
            Console.WriteLine("[Transcription] Connected");
        }

        private void OnTranscriptionDisconnected(object? sender, EventArgs e)
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (IsListening)
                {
                    sessionPhase = OverlaySessionPhase.Ready;
                    streamingService.CancelCurrentStream();
                    // StopCapture wraps NAudio's StopRecording which can hitch briefly;
                    // fire-and-forget on the pool keeps the dispatcher snappy here.
                    Task.Run(() =>
                    {
                        try { audioCaptureService.StopCapture(); } catch { }
                        try { speakerCaptureService.StopCapture(); } catch { }
                    });
                    UpdateListeningUI(false);
                    StopSessionTimer();
                }
            }));
        }

        private void OnSessionCreated(object? sender, string sessionId)
        {
            Console.WriteLine($"[Transcription] Session: {sessionId}");
            // Backend message ids restart per WebSocket session; drop stale id mappings
            // so new utterances never merge into entries from a previous connection.
            history.ResetLiveTranscripts();
        }

        private void OnTranscriptionError(object? sender, string error)
        {
            Dispatcher.BeginInvoke(new Action(() => ShowInlineError($"Transcription: {error}")));
        }

        private async void OnMicrophoneDataAvailable(object? sender, AudioDataEventArgs e)
        {
            try
            {
                // Drop mic while user-muted, or while system audio (Other / O) is playing —
                // otherwise speakers leak into the mic and get transcribed as "You", and can
                // confuse live answers. Backend KeepAlive keeps the mic Deepgram stream open.
                if (!IsListening || !transcriptionService.IsConnected || micMuted)
                    return;
                if (speakerCaptureService.IsSystemAudioActive())
                    return;

                await transcriptionService.SendAudioAsync(e.AudioData, MessageSource.Microphone);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Audio] Mic send error: {ex.Message}");
            }
        }

        private async void OnSpeakerDataAvailable(object? sender, SpeakerAudioEventArgs e)
        {
            try
            {
                // Other / system audio only — this stream drives live auto-answers after
                // ~2.5s silence when the utterance has >= 4 words (backend).
                if (IsListening && transcriptionService.IsConnected)
                    await transcriptionService.SendAudioAsync(e.AudioData, MessageSource.Speaker);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Audio] Speaker send error: {ex.Message}");
            }
        }

        private void OnAudioError(object? sender, string error)
        {
            Console.WriteLine($"[Audio] Error: {error}");
        }

        private void OnServiceError(object? sender, string error)
        {
            Console.WriteLine($"[Service] Error: {error}");
        }

        #endregion

        #region AI Suggestion Events

        private void OnAISuggestionStarted(object? sender, string question)
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                // A new auto answer is starting — clear any prior cancel suppression.
                suppressAutoSuggestion = false;
                autoStreaming = true;
                UpdateStopButton();
                AlphaRegion.SwitchToChatTab();
                SetLastQuestion(question, inTranscript: false);
                AlphaRegion.BeginStreamingResponse(question);
            }));
        }

        private void OnAISuggestionToken(object? sender, string token)
        {
            // Dropped once the user cancels this answer (backend may still be generating).
            if (suppressAutoSuggestion) return;
            // BeginInvoke: tokens stream at 50+/sec from the WebSocket receive thread.
            // Synchronous Invoke would block that thread per token, building latency and
            // potentially starving the dispatcher under load.
            Dispatcher.BeginInvoke(new Action(() => AlphaRegion.AppendStreamingToken(token)));
        }

        private void OnAISuggestionConfidence(object? sender, double confidence)
        {
            pendingAutoConfidence = confidence;
        }

        private void OnAISuggestionCompleted(object? sender, string response)
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                autoStreaming = false;
                UpdateStopButton();
                // If the user cancelled, the bubble was already finalized — don't overwrite it.
                if (suppressAutoSuggestion)
                {
                    suppressAutoSuggestion = false;
                    return;
                }
                double? conf = pendingAutoConfidence;
                pendingAutoConfidence = null;
                AlphaRegion.EndStreamingResponse(
                    string.IsNullOrEmpty(response) ? null : response, conf);
                if (!string.IsNullOrWhiteSpace(response))
                    history.Append(ConversationMessageKind.AssistantAnswer, response);
            }));
        }

        private void OnAISuggestionError(object? sender, string error)
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                autoStreaming = false;
                UpdateStopButton();
                if (suppressAutoSuggestion) { suppressAutoSuggestion = false; return; }
                AlphaRegion.ShowStreamingError(error);
            }));
        }

        /// <summary>
        /// Cancels whatever response is currently generating (manual HTTP stream and/or the
        /// live WebSocket auto-suggestion) and finalizes the on-screen answer immediately.
        /// Safe to call when nothing is streaming.
        /// </summary>
        private void CancelActiveResponse()
        {
            try
            {
                // Manual HTTP SSE: cancels the request; its ResponseComplete("" ) will fire.
                streamingService.CancelCurrentStream();

                // Auto WebSocket suggestion: stop rendering further tokens, tell the backend
                // to abort generation, and close the bubble.
                if (autoStreaming)
                {
                    suppressAutoSuggestion = true;
                    _ = transcriptionService.SendCancelAiAsync();
                }

                manualStreaming = false;
                autoStreaming = false;
                UpdateStopButton();
                AlphaRegion.CancelStreaming();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[UI] Cancel response error: {ex.Message}");
            }
        }

        private void StopBtn_Click(object sender, RoutedEventArgs e) => CancelActiveResponse();

        /// <summary>Shows the Stop button only while a response is generating.</summary>
        private void UpdateStopButton()
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke(new Action(UpdateStopButton));
                return;
            }
            if (StopBtn != null)
                StopBtn.Visibility = IsResponseStreaming ? Visibility.Visible : Visibility.Collapsed;
        }

        #endregion

        #region Cleanup

        protected override void OnClosed(EventArgs e)
        {
            base.OnClosed(e);
            try
            {
                StopSessionTimer();
                StopLiveDotPulse();

                // Unsubscribe events we added in SubscribeToEvents so the services can be
                // disposed without lingering callbacks.
                try { transcriptionService.TranscriptionReceived -= OnTranscriptionReceived; } catch { }
                try { transcriptionService.ErrorOccurred -= OnTranscriptionError; } catch { }
                try { transcriptionService.Connected -= OnTranscriptionConnected; } catch { }
                try { transcriptionService.Disconnected -= OnTranscriptionDisconnected; } catch { }
                try { transcriptionService.SessionCreated -= OnSessionCreated; } catch { }
                try { transcriptionService.AISuggestionStarted -= OnAISuggestionStarted; } catch { }
                try { transcriptionService.AISuggestionToken -= OnAISuggestionToken; } catch { }
                try { transcriptionService.AISuggestionCompleted -= OnAISuggestionCompleted; } catch { }
                try { transcriptionService.AISuggestionConfidence -= OnAISuggestionConfidence; } catch { }
                try { transcriptionService.AISuggestionError -= OnAISuggestionError; } catch { }
                try { audioCaptureService.MicrophoneDataAvailable -= OnMicrophoneDataAvailable; } catch { }
                try { audioCaptureService.ErrorOccurred -= OnAudioError; } catch { }
                try { speakerCaptureService.AudioDataAvailable -= OnSpeakerDataAvailable; } catch { }
                try { speakerCaptureService.ErrorOccurred -= OnAudioError; } catch { }
                try { screenCaptureService.ErrorOccurred -= OnServiceError; } catch { }
                try { authService.ErrorOccurred -= OnServiceError; } catch { }

                // Persist the ongoing thread without archiving it — closing the overlay is
                // not the same as ending the conversation.
                history?.SaveCurrentNow();

                audioCaptureService?.Dispose();
                speakerCaptureService?.Dispose();
                transcriptionService?.Dispose();
                screenCaptureService?.Dispose();
                streamingService?.Dispose();
                authService?.Dispose();
                titleService?.Dispose();

                // Release the system hotkey slots so other apps can reuse Ctrl+Shift+L/S/C/H.
                try { hotkeyService?.Dispose(); } catch { }
                hotkeyService = null;

                // Stop the click-through poller and restore normal hit-testing.
                try { clickThroughService?.Dispose(); } catch { }
                clickThroughService = null;

                // Remove the global Backtick+Space keyboard hook.
                try { micMuteHook?.Dispose(); } catch { }
                micMuteHook = null;

                try { whatShouldISayHook?.Dispose(); } catch { }
                whatShouldISayHook = null;

                // Remove the global mouse-wheel hook.
                try { wheelHook?.Dispose(); } catch { }
                wheelHook = null;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Cleanup] Error: {ex.Message}");
            }
        }

        #endregion
    }
}
