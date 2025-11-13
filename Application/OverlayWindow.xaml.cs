using System;
using System.Windows;
using System.Windows.Input;

namespace SnapEye
{
    /// <summary>
    /// Main overlay window for SnapEye AI Assistant
    /// Handles UI events and coordinates between navbar controls and solution region
    /// </summary>
    public partial class OverlayWindow : Window
    {
        public OverlayWindow()
        {
            InitializeComponent();
            
            InitializeWindowPosition();
            InitializeOpacity();
            SubscribeToEvents();
        }

        #region Initialization

        private void InitializeWindowPosition()
        {
            // Position window at top-center of screen
            this.Left = (SystemParameters.PrimaryScreenWidth - this.Width) / 2;
            this.Top = 50; // Top of screen with padding
        }

        private void InitializeOpacity()
        {
            // Set initial opacity (higher for toolbar visibility)
            double initialOpacity = 1.0;  // Full opacity
            this.Opacity = initialOpacity;
            
            // Sync with AlphaRegion
            AlphaRegion?.UpdateOpacity(initialOpacity);
        }

        private void SubscribeToEvents()
        {
            // Subscribe to navbar component events
            ListenBtn.ButtonClicked += OnListenButtonClicked;
            CameraBtn.ButtonClicked += OnCameraButtonClicked;
            OpacitySlider.ValueChanged += OnRangeSliderValueChanged;
            
            // Subscribe to settings events
            SettingsCtrl.VisibilityToggled += OnVisibilityToggled;
            SettingsCtrl.QuitRequested += OnQuitRequested;
        }

        #endregion

        #region Window Events

        private void Window_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton == MouseButton.Left)
            {
                this.DragMove();
            }
        }

        #endregion

        #region Event Handlers

        /// <summary>
        /// Handles Listen button click - starts voice transcription demo
        /// </summary>
        private void OnListenButtonClicked(object? sender, EventArgs e)
        {
            StartTranscriptionDemo();
            ShowVoiceRecognitionMessage();
        }

        /// <summary>
        /// Handles Camera button click - shows image analysis demo
        /// </summary>
        private void OnCameraButtonClicked(object? sender, EventArgs e)
        {
            AlphaRegion.ShowSampleResponse();
        }

        /// <summary>
        /// Handles slider value change - updates window and region opacity
        /// </summary>
        private void OnRangeSliderValueChanged(object? sender, double value)
        {
            // Convert slider value (0-100) to opacity (0.5-1.0)
            // Minimum opacity of 0.5 for better visibility
            double opacity = 0.5 + (value / 100.0) * 0.5;
            
            this.Opacity = opacity;
            AlphaRegion?.UpdateOpacity(opacity);
            
            Console.WriteLine($"SnapEye Slider value: {value}, Opacity: {opacity:F2}");
        }

        /// <summary>
        /// Handles visibility toggle - shows/hides the Alpha Region
        /// </summary>
        private void OnVisibilityToggled(object? sender, EventArgs e)
        {
            if (AlphaRegion != null)
            {
                bool isVisible = SettingsCtrl.IsRegionVisible;
                AlphaRegion.Visibility = isVisible ? Visibility.Visible : Visibility.Collapsed;
                Console.WriteLine($"SnapEye Alpha Region visibility: {isVisible}");
            }
        }

        /// <summary>
        /// Handles quit request - closes the application
        /// </summary>
        private void OnQuitRequested(object? sender, EventArgs e)
        {
            Console.WriteLine("SnapEye quit requested");
            Application.Current.Shutdown();
        }

        #endregion

        #region Demo Methods

        /// <summary>
        /// Simulates a transcription conversation for demo purposes
        /// </summary>
        private void StartTranscriptionDemo()
        {
            AlphaRegion.ClearTranscription();
            
            System.Threading.Tasks.Task.Run(async () =>
            {
                await System.Threading.Tasks.Task.Delay(500);
                Dispatcher.Invoke(() => 
                    AlphaRegion.AddUserTranscription("Hello, can you help me analyze this code?"));
                
                await System.Threading.Tasks.Task.Delay(1000);
                Dispatcher.Invoke(() => 
                    AlphaRegion.AddSystemTranscription("Of course! I'd be happy to help. Please share the code you'd like me to analyze."));
                
                await System.Threading.Tasks.Task.Delay(1500);
                Dispatcher.Invoke(() => 
                    AlphaRegion.AddUserTranscription("Here's a function I wrote. Is it optimized?"));
                
                await System.Threading.Tasks.Task.Delay(1200);
                Dispatcher.Invoke(() => 
                    AlphaRegion.AddSystemTranscription("Let me review your function. I'll check for performance optimization opportunities and best practices."));
            });
        }

        /// <summary>
        /// Shows voice recognition active message in chat view
        /// </summary>
        private void ShowVoiceRecognitionMessage()
        {
            AlphaRegion.ShowLoading();
            
            System.Threading.Tasks.Task.Delay(1500).ContinueWith(_ =>
            {
                Dispatcher.Invoke(() =>
                {
                    AlphaRegion.ShowAIResponse(
                        "🎤 **Voice Recognition Active**\n\n" +
                        "I'm listening to your audio input. The speech recognition feature will process your voice commands and convert them to text for AI analysis.\n\n" +
                        "### Features:\n" +
                        "- Real-time transcription\n" +
                        "- Multi-language support\n" +
                        "- Natural conversation flow\n\n" +
                        "*Switch to the Transcription tab to see the conversation history.*"
                    );
                });
            });
        }

        #endregion
    }
}
