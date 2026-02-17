using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
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

        // State
        private bool isTranscribing;
        private bool isInvisibleToCapture;
        private SessionManager.SessionData? currentSession;
        private int selectedModeIndex;
        private DispatcherTimer? sessionTimer;
        private DateTime sessionStartTime;

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
        }

        #region Initialization

        private void Window_Loaded(object sender, RoutedEventArgs e)
        {
            if (AppConfig.InvisibleToCapture)
                EnableScreenShareInvisibility();

            this.Opacity = AppConfig.DefaultOpacity;
        }

        private void InitializeWindowPosition()
        {
            this.Left = SystemParameters.PrimaryScreenWidth - this.Width - 20;
            this.Top = 60;
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
            streamingService.ResponseComplete += (s, full) => Dispatcher.Invoke(() => AlphaRegion.EndStreamingResponse(full));
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
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[UI] Invisibility error: {ex.Message}");
            }
        }

        #endregion

        #region Window Events

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

        #region Navbar Events

        private async void MicToggle_Click(object? sender, RoutedEventArgs e)
        {
            try
            {
                if (!isTranscribing)
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

        private async void EndSession_Click(object sender, MouseButtonEventArgs e)
        {
            try
            {
                await StopTranscriptionAsync();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[UI] End session error: {ex.Message}");
            }
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
                {
                    AlphaRegion.SwitchToChatTab();
                    TranscriptToggle.IsChecked = false;
                    AlphaRegion.BeginStreamingResponse(action.Label);

                    string systemPrompt = GetCurrentSystemPrompt();
                    await streamingService.StreamResponseAsync(action.Prompt, systemPrompt);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[UI] Quick action error: {ex.Message}");
                AlphaRegion.ShowStreamingError(ex.Message);
            }
        }

        #endregion

        #region Chat Input

        private void ChatInput_KeyDown(object sender, KeyEventArgs e)
        {
            // Ctrl+Enter or just Enter to send
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
                AlphaRegion.SwitchToChatTab();
                TranscriptToggle.IsChecked = false;
                AlphaRegion.BeginStreamingResponse(query);

                string systemPrompt = GetCurrentSystemPrompt();
                await streamingService.StreamResponseAsync(query, systemPrompt);
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
                AlphaRegion.ShowLoading();
                string? ocrText = await screenCaptureService.CaptureAndOcrAsync();
                if (!string.IsNullOrEmpty(ocrText))
                    AlphaRegion.SetMarkdownContent($"## Screen Capture\n\n{ocrText}");
                else
                    AlphaRegion.ShowError("No text extracted from screen.");
            }
            catch (Exception ex)
            {
                AlphaRegion.ShowError($"Capture error: {ex.Message}");
            }
        }

        private string GetCurrentSystemPrompt()
        {
            if (selectedModeIndex >= 0 && selectedModeIndex < AppConfig.Modes.Count)
                return AppConfig.Modes[selectedModeIndex].SystemPrompt;
            return "You are SnapEye, a helpful AI assistant.";
        }

        #endregion

        #region Transcription Control

        private async System.Threading.Tasks.Task StartTranscriptionAsync()
        {
            try
            {
                bool connected = await transcriptionService.ConnectAsync(
                    AppConfig.DeepgramModel,
                    AppConfig.DeepgramLanguage,
                    AppConfig.EndpointingMs
                );

                if (connected)
                {
                    isTranscribing = true;
                    audioCaptureService.StartCapture();
                    speakerCaptureService.StartCapture();

                    UpdateListeningUI(true);
                    StartSessionTimer();

                    AlphaRegion.SwitchToTranscriptionTab();
                    TranscriptToggle.IsChecked = true;
                }
                else
                {
                    AlphaRegion.ShowError("Failed to connect to transcription service.");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Transcription] Start error: {ex.Message}");
                AlphaRegion.ShowError($"Connection error: {ex.Message}");
            }
        }

        private async System.Threading.Tasks.Task StopTranscriptionAsync()
        {
            try
            {
                isTranscribing = false;
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
                if (listening)
                {
                    MicIcon.Text = "\U0001F534";  // Red circle = recording
                    NotListeningBadge.Visibility = Visibility.Collapsed;
                    SessionTimerBorder.Visibility = Visibility.Visible;
                }
                else
                {
                    MicIcon.Text = "\U0001F399";   // Microphone
                    NotListeningBadge.Visibility = Visibility.Visible;
                    SessionTimerBorder.Visibility = Visibility.Collapsed;
                }
            });
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
                SessionTimerText.Text = elapsed.ToString(@"mm\:ss");
            };
            sessionTimer.Start();
        }

        private void StopSessionTimer()
        {
            sessionTimer?.Stop();
            sessionTimer = null;
            Dispatcher.Invoke(() => SessionTimerText.Text = "00:00");
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
                        AlphaRegion.AddUserTranscription(msg.Text, msg.IsNewSegment);
                    else
                        AlphaRegion.AddSystemTranscription(msg.Text, msg.IsNewSegment);
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
                if (isTranscribing)
                {
                    isTranscribing = false;
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
                if (isTranscribing && transcriptionService.IsConnected)
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
                if (isTranscribing && transcriptionService.IsConnected)
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
            Dispatcher.Invoke(() => AlphaRegion.EndStreamingResponse(
                string.IsNullOrEmpty(response) ? null : response));
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
