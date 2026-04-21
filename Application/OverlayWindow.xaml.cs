using System;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using SnapEye.Services;
using SnapEye.Models;
using SnapEye.Config;

namespace SnapEye
{
    public partial class OverlayWindow : Window
    {
        // Win32 API for screen share invisibility
        [DllImport("user32.dll")]
        private static extern bool SetWindowDisplayAffinity(IntPtr hWnd, uint dwAffinity);
        private const uint WDA_NONE = 0x00000000;
        private const uint WDA_EXCLUDEFROMCAPTURE = 0x00000011;

        // Services
        private readonly AuthService authService;
        private readonly RealtimeTranscriptionService transcriptionService;
        private readonly AudioCaptureService audioCaptureService;
        private readonly SpeakerCaptureService speakerCaptureService;
        private readonly ScreenCaptureService screenCaptureService;
        private readonly StreamingResponseService streamingService;
        private readonly ConversationHistoryService history;
        private readonly ConversationTitleService titleService;

        // State
        private OverlaySessionPhase sessionPhase = OverlaySessionPhase.Ready;
        private string? lastScreenOcr;
        private bool isInvisibleToCapture;
        private bool isExpanded = true;
        private SessionManager.SessionData? currentSession;
        private int selectedModeIndex;
        private DispatcherTimer? sessionTimer;
        private DateTime sessionStartTime;

        // Pulse animation for live dot
        private DispatcherTimer? liveDotPulseTimer;
        private bool liveDotVisible = true;

        private bool IsListening => sessionPhase == OverlaySessionPhase.Listening;

        public OverlayWindow()
        {
            InitializeComponent();
            AppConfig.LoadConfiguration();

            // Initialize services
            authService = new AuthService(AppConfig.BackendHttpUrl);
            transcriptionService = new RealtimeTranscriptionService(AppConfig.BackendWebSocketUrl);
            audioCaptureService = new AudioCaptureService();
            speakerCaptureService = new SpeakerCaptureService();
            screenCaptureService = new ScreenCaptureService(AppConfig.BackendHttpUrl);
            streamingService = new StreamingResponseService(AppConfig.BackendHttpUrl);
            history = new ConversationHistoryService();
            titleService = new ConversationTitleService(AppConfig.BackendHttpUrl);

            InitializeWindowPosition();
            SubscribeToEvents();
            PopulateModeSelector();
            PopulateQuickActions();
        }

        public void SetSession(SessionManager.SessionData session)
        {
            currentSession = session;
            transcriptionService.SetAuthToken(session.AccessToken);
            screenCaptureService.SetAuthToken(session.AccessToken);
            streamingService.SetAuthToken(session.AccessToken);
            titleService.SetAuthToken(session.AccessToken);
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

            this.Opacity = AppConfig.DefaultOpacity;
            OpacitySlider.Value = AppConfig.DefaultOpacity;
        }

        private void InitializeWindowPosition()
        {
            // Position at top center of screen
            this.Left = (SystemParameters.PrimaryScreenWidth - this.Width) / 2;
            this.Top = 20;
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
            transcriptionService.AISuggestionError += OnAISuggestionError;

            // Audio capture events
            audioCaptureService.MicrophoneDataAvailable += OnMicrophoneDataAvailable;
            audioCaptureService.ErrorOccurred += OnAudioError;
            speakerCaptureService.AudioDataAvailable += OnSpeakerDataAvailable;
            speakerCaptureService.ErrorOccurred += OnAudioError;

            // Screen capture events
            screenCaptureService.ErrorOccurred += OnServiceError;

            // Streaming response events
            streamingService.StreamStarted += (s, _) => Dispatcher.Invoke(() => AlphaRegion.BeginStreamingResponse());
            streamingService.TokenReceived += (s, token) => Dispatcher.Invoke(() => AlphaRegion.AppendStreamingToken(token));
            streamingService.ResponseComplete += (s, full) => Dispatcher.Invoke(() =>
            {
                AlphaRegion.EndStreamingResponse(full);
                if (!string.IsNullOrWhiteSpace(full))
                    history.Append(ConversationMessageKind.AssistantAnswer, full);
            });
            streamingService.ErrorOccurred += (s, err) => Dispatcher.Invoke(() => AlphaRegion.ShowStreamingError(err));

            // Auth
            authService.ErrorOccurred += OnServiceError;
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
                var hwnd = new WindowInteropHelper(this).Handle;
                if (hwnd != IntPtr.Zero)
                {
                    bool success = SetWindowDisplayAffinity(hwnd, WDA_EXCLUDEFROMCAPTURE);
                    isInvisibleToCapture = success;
                    UpdateStealthIcon();
                }
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
                try { DragMove(); } catch { /* drag already in progress */ }
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
                if (IsListening)
                    await StopTranscriptionAsync();
                else
                {
                    streamingService.CancelCurrentStream();
                    UpdateListeningUI(false);
                    StopSessionTimer();
                }

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
                try { this.DragMove(); } catch { }
            }
        }

        private void Window_KeyDown(object sender, KeyEventArgs e)
        {
            try
            {
                // Ctrl+Shift+L: Toggle listen
                if (e.Key == Key.L && Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift))
                {
                    MicToggle_Click(null, null!);
                    e.Handled = true;
                }
                // Ctrl+Shift+S: Screenshot
                else if (e.Key == Key.S && Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift))
                {
                    Screenshot_Click(null, null!);
                    e.Handled = true;
                }
                // Ctrl+Shift+H: Hide/Show
                else if (e.Key == Key.C && Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift))
                {
                    CopyAnswer_Click(sender, e);
                    e.Handled = true;
                }
                else if (e.Key == Key.H && Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift))
                {
                    this.Visibility = this.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
                    e.Handled = true;
                }
                // Escape: Minimize/hide
                else if (e.Key == Key.Escape)
                {
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
                var hwnd = new WindowInteropHelper(this).Handle;
                if (hwnd == IntPtr.Zero) return;

                if (isInvisibleToCapture)
                {
                    SetWindowDisplayAffinity(hwnd, WDA_NONE);
                    isInvisibleToCapture = false;
                }
                else
                {
                    SetWindowDisplayAffinity(hwnd, WDA_EXCLUDEFROMCAPTURE);
                    isInvisibleToCapture = true;
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
                AlphaRegion.ShowError($"Transcription error: {ex.Message}");
            }
        }

        private void ExpandToggle_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                isExpanded = !isExpanded;
                SessionBody.Visibility = isExpanded ? Visibility.Visible : Visibility.Collapsed;

                if (isExpanded)
                    ChevronIcon.Data = Geometry.Parse("M12,8L6,14L7.41,15.41L12,10.83L16.59,15.41L18,14L12,8Z");
                else
                    ChevronIcon.Data = Geometry.Parse("M16.59,8.59L12,13.17L7.41,8.59L6,10L12,16L18,10L16.59,8.59Z");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[UI] Expand toggle error: {ex.Message}");
            }
        }

        private void Menu_Click(object sender, RoutedEventArgs e)
        {
            if (MenuBtn.ContextMenu != null)
            {
                MenuBtn.ContextMenu.PlacementTarget = MenuBtn;
                MenuBtn.ContextMenu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
                MenuBtn.ContextMenu.IsOpen = true;
            }
        }

        private void OpacitySlider_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            this.Opacity = e.NewValue;
        }

        private void Quit_Click(object sender, RoutedEventArgs e)
        {
            Application.Current.Shutdown();
        }

        private void Close_Click(object sender, RoutedEventArgs e)
        {
            this.Visibility = Visibility.Collapsed;
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
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[UI] Mode change error: {ex.Message}");
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
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[UI] Transcript toggle error: {ex.Message}");
            }
        }

        #endregion

        #region Quick Actions

        private async void QuickAction_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (sender is Button btn && btn.Tag is QuickActionConfig action)
                    await RunContextualPromptAsync(action.Label, action.Prompt);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[UI] Quick action error: {ex.Message}");
                AlphaRegion.ShowStreamingError(ex.Message);
            }
        }

        private async Task RunContextualPromptAsync(string displayLabel, string userPrompt)
        {
            ExpandSessionIfCollapsed();
            AlphaRegion.SwitchToChatTab();
            TranscriptToggle.IsChecked = false;

            // Log quick-action label as a user prompt in the chat thread + transcript view.
            AlphaRegion.AddUserTranscription(displayLabel);
            history.Append(ConversationMessageKind.QuickAction, userPrompt, displayLabel);

            AlphaRegion.BeginStreamingResponse(displayLabel);

            string systemPrompt = GetCurrentSystemPrompt();
            string context = BuildConversationContext();
            string fullMessage = string.IsNullOrEmpty(context)
                ? userPrompt
                : context + "\n\nUSER REQUEST: " + userPrompt;

            await streamingService.StreamResponseAsync(fullMessage, systemPrompt);
        }

        private void ExpandSessionIfCollapsed()
        {
            if (isExpanded) return;
            isExpanded = true;
            SessionBody.Visibility = Visibility.Visible;
            ChevronIcon.Data = Geometry.Parse("M12,8L6,14L7.41,15.41L12,10.83L16.59,15.41L18,14L12,8Z");
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

                AlphaRegion.SwitchToChatTab();
                TranscriptToggle.IsChecked = false;

                // Record typed prompt in chat history + transcription "You" bubble.
                AlphaRegion.AddUserTranscription(query);
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
            try
            {
                ExpandSessionIfCollapsed();

                AlphaRegion.ShowLoading();
                string? ocrText = await screenCaptureService.CaptureAndOcrAsync();
                if (!string.IsNullOrEmpty(ocrText))
                {
                    lastScreenOcr = ocrText;
                    AlphaRegion.SetMarkdownContent($"## Screen Capture\n\n{ocrText}");
                    if (IsListening && transcriptionService.IsConnected)
                        await transcriptionService.SendScreenContextAsync(ocrText);
                }
                else
                    AlphaRegion.ShowError("No text extracted from screen.");
            }
            catch (Exception ex)
            {
                AlphaRegion.ShowError($"Capture error: {ex.Message}");
            }
        }

        private void CopyAnswer_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                string md = AlphaRegion.GetChatMarkdown();
                if (string.IsNullOrWhiteSpace(md))
                    return;
                Clipboard.SetText(md);
            }
            catch (Exception ex)
            {
                AlphaRegion.ShowError($"Copy failed: {ex.Message}");
            }
        }

        private async Task StreamUserDirectedLlmAsync(string question)
        {
            string transcript = BuildConversationContext();
            string systemPrompt = GetCurrentSystemPrompt();
            await streamingService.StreamContextResponseAsync(
                question,
                transcript,
                "",
                lastScreenOcr ?? "",
                systemPrompt,
                null);
        }

        private string GetCurrentSystemPrompt()
        {
            string modePrompt = "You are SnapEye, a helpful AI assistant.";
            if (selectedModeIndex >= 0 && selectedModeIndex < AppConfig.Modes.Count)
                modePrompt = AppConfig.Modes[selectedModeIndex].SystemPrompt;

            return modePrompt +
                "\n\nIMPORTANT: You have access to the live conversation transcript provided in the user message. " +
                "Use it to give context-aware answers. Never say you don't have access to the conversation. " +
                "Be concise and use markdown for readability.";
        }

        private string BuildConversationContext()
        {
            try
            {
                var messages = transcriptionService.Conversation.Messages;
                if (messages == null || messages.Count == 0)
                    return "";

                var sb = new StringBuilder();
                sb.AppendLine("LIVE CONVERSATION TRANSCRIPT:");
                sb.AppendLine("---");
                int start = Math.Max(0, messages.Count - 30);
                for (int i = start; i < messages.Count; i++)
                {
                    var msg = messages[i];
                    string speaker = msg.Source == MessageSource.Microphone ? "You" : "Other";
                    sb.AppendLine($"[{speaker}]: {msg.Text}");
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

        private async Task StartTranscriptionAsync()
        {
            try
            {
                sessionPhase = OverlaySessionPhase.Connecting;
                bool connected = await transcriptionService.ConnectAsync(
                    AppConfig.DeepgramModel,
                    AppConfig.DeepgramLanguage,
                    AppConfig.EndpointingMs
                );

                if (connected)
                {
                    sessionPhase = OverlaySessionPhase.Listening;
                    audioCaptureService.StartCapture();
                    speakerCaptureService.StartCapture();

                    UpdateListeningUI(true);
                    StartSessionTimer();

                    if (!string.IsNullOrWhiteSpace(lastScreenOcr))
                        await transcriptionService.SendScreenContextAsync(lastScreenOcr);

                    ExpandSessionIfCollapsed();

                    AlphaRegion.SwitchToTranscriptionTab();
                    TranscriptToggle.IsChecked = true;
                }
                else
                {
                    sessionPhase = OverlaySessionPhase.Ready;
                    AlphaRegion.ShowError("Failed to connect to transcription service.");
                }
            }
            catch (Exception ex)
            {
                sessionPhase = OverlaySessionPhase.Ready;
                Console.WriteLine($"[Transcription] Start error: {ex.Message}");
                AlphaRegion.ShowError($"Connection error: {ex.Message}");
            }
        }

        private async Task StopTranscriptionAsync()
        {
            try
            {
                streamingService.CancelCurrentStream();
                sessionPhase = OverlaySessionPhase.Ready;
                audioCaptureService.StopCapture();
                speakerCaptureService.StopCapture();
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
            Dispatcher.Invoke(() =>
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
            });
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

        // Helper to find named elements inside the MicToggleBtn's template
        private TextBlock? FindListenBtnText()
        {
            return FindTemplateChild<TextBlock>(MicToggleBtn, "ListenBtnText");
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

        #region Session Timer

        private void StartSessionTimer()
        {
            sessionStartTime = DateTime.Now;
            sessionTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            sessionTimer.Tick += (s, e) =>
            {
                var elapsed = DateTime.Now - sessionStartTime;
                var listenBtnText = FindListenBtnText();
                if (listenBtnText != null)
                    listenBtnText.Text = elapsed.ToString(@"mm\:ss");
            };
            sessionTimer.Start();
        }

        private void StopSessionTimer()
        {
            sessionTimer?.Stop();
            sessionTimer = null;
        }

        #endregion

        #region Transcription Events

        private void OnTranscriptionReceived(object? sender, TranscriptionMessage msg)
        {
            try
            {
                Dispatcher.Invoke(() =>
                {
                    if (msg.Source == MessageSource.Microphone)
                    {
                        AlphaRegion.AddUserTranscription(msg.Text, msg.IsNewSegment);
                        history.Append(ConversationMessageKind.UserSpoken, msg.Text);
                    }
                    else
                    {
                        AlphaRegion.AddSystemTranscription(msg.Text, msg.IsNewSegment);
                        history.Append(ConversationMessageKind.OtherSpoken, msg.Text);
                    }
                });
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
            Dispatcher.Invoke(() =>
            {
                if (IsListening)
                {
                    sessionPhase = OverlaySessionPhase.Ready;
                    streamingService.CancelCurrentStream();
                    audioCaptureService.StopCapture();
                    speakerCaptureService.StopCapture();
                    UpdateListeningUI(false);
                    StopSessionTimer();
                }
            });
        }

        private void OnSessionCreated(object? sender, string sessionId)
        {
            Console.WriteLine($"[Transcription] Session: {sessionId}");
        }

        private void OnTranscriptionError(object? sender, string error)
        {
            Dispatcher.Invoke(() => AlphaRegion.ShowError($"Transcription: {error}"));
        }

        private async void OnMicrophoneDataAvailable(object? sender, AudioDataEventArgs e)
        {
            try
            {
                if (IsListening && transcriptionService.IsConnected)
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
            Dispatcher.Invoke(() =>
            {
                TranscriptToggle.IsChecked = false;
                AlphaRegion.BeginStreamingResponse(question);
            });
        }

        private void OnAISuggestionToken(object? sender, string token)
        {
            Dispatcher.Invoke(() => AlphaRegion.AppendStreamingToken(token));
        }

        private void OnAISuggestionCompleted(object? sender, string response)
        {
            Dispatcher.Invoke(() =>
            {
                AlphaRegion.EndStreamingResponse(string.IsNullOrEmpty(response) ? null : response);
                if (!string.IsNullOrWhiteSpace(response))
                    history.Append(ConversationMessageKind.AssistantAnswer, response);
            });
        }

        private void OnAISuggestionError(object? sender, string error)
        {
            Dispatcher.Invoke(() => AlphaRegion.ShowStreamingError(error));
        }

        #endregion

        #region Cleanup

        protected override void OnClosed(EventArgs e)
        {
            base.OnClosed(e);
            try
            {
                sessionTimer?.Stop();
                liveDotPulseTimer?.Stop();
                var endedOnClose = history?.Current;
                history?.EndCurrentSession();
                if (endedOnClose != null)
                    _ = GenerateAndSaveTitleAsync(endedOnClose);
                audioCaptureService?.Dispose();
                speakerCaptureService?.Dispose();
                transcriptionService?.Dispose();
                screenCaptureService?.Dispose();
                streamingService?.Dispose();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Cleanup] Error: {ex.Message}");
            }
        }

        #endregion
    }
}
