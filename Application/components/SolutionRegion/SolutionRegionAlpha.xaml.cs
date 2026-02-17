using System;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace SnapEye.SolutionRegion
{
    public partial class SolutionRegionAlpha : UserControl
    {
        // Streaming state
        private readonly StringBuilder streamingBuffer = new();
        private bool isStreamingResponse;
        private DispatcherTimer? typingTimer;

        // Transcription state
        private Border? lastUserBubble;
        private TextBlock? lastUserText;
        private Border? lastSystemBubble;
        private TextBlock? lastSystemText;
        private bool newUserSegment = true;
        private bool newSystemSegment = true;
        private bool hasPlaceholder = true;

        // Theme colors (from config or defaults)
        private static readonly Color AccentColor = (Color)ColorConverter.ConvertFromString("#6C5CE7");
        private static readonly Color UserBubbleColor = (Color)ColorConverter.ConvertFromString("#6C5CE7");
        private static readonly Color SystemBubbleColor = (Color)ColorConverter.ConvertFromString("#2D2D4E");
        private static readonly Color TextColor = (Color)ColorConverter.ConvertFromString("#E0E0E0");

        public SolutionRegionAlpha()
        {
            InitializeComponent();
        }

        #region Tab Switching

        public void SwitchToChatTab()
        {
            SafeInvoke(() =>
            {
                ChatView.Visibility = Visibility.Visible;
                TranscriptionView.Visibility = Visibility.Collapsed;
            });
        }

        public void SwitchToTranscriptionTab()
        {
            SafeInvoke(() =>
            {
                ChatView.Visibility = Visibility.Collapsed;
                TranscriptionView.Visibility = Visibility.Visible;
            });
        }

        #endregion

        #region Chat / AI Content

        public void SetMarkdownContent(string markdown)
        {
            SafeInvoke(() =>
            {
                if (MarkdownViewer != null)
                    MarkdownViewer.Markdown = markdown ?? "";
            });
        }

        public void AppendMarkdownContent(string markdown)
        {
            SafeInvoke(() =>
            {
                if (MarkdownViewer != null)
                    MarkdownViewer.Markdown += "\n\n" + markdown;
            });
        }

        public void ClearContent()
        {
            SafeInvoke(() =>
            {
                if (MarkdownViewer != null)
                    MarkdownViewer.Markdown = "";
            });
        }

        public void ShowAIResponse(string response)
        {
            SafeInvoke(() => SetMarkdownContent($"## AI Response\n\n{response}"));
        }

        public void ShowLoading()
        {
            SafeInvoke(() => SetMarkdownContent("**Processing...**\n\nPlease wait while the AI generates a response."));
        }

        public void ShowError(string error)
        {
            SafeInvoke(() => SetMarkdownContent($"**Error**\n\n{error}"));
        }

        #endregion

        #region Streaming AI Response

        public void BeginStreamingResponse(string? question = null)
        {
            SafeInvoke(() =>
            {
                SwitchToChatTab();
                isStreamingResponse = true;
                streamingBuffer.Clear();

                string header = "## AI Suggestion";
                if (!string.IsNullOrEmpty(question))
                    header += $"\n> {question}";
                header += "\n\n";

                streamingBuffer.Append(header);
                SetMarkdownContent(streamingBuffer.ToString() + "...");
                ShowTypingIndicator();
            });
        }

        public void AppendStreamingToken(string token)
        {
            SafeInvoke(() =>
            {
                if (!isStreamingResponse) return;
                streamingBuffer.Append(token);
                SetMarkdownContent(streamingBuffer.ToString());
            });
        }

        public void EndStreamingResponse(string? fullResponse = null)
        {
            SafeInvoke(() =>
            {
                isStreamingResponse = false;
                HideTypingIndicator();

                if (!string.IsNullOrEmpty(fullResponse))
                {
                    string header = streamingBuffer.ToString();
                    int idx = header.IndexOf("\n\n");
                    if (idx > 0)
                        SetMarkdownContent(header.Substring(0, idx + 2) + fullResponse);
                    else
                        SetMarkdownContent("## AI Suggestion\n\n" + fullResponse);
                }
                else
                {
                    SetMarkdownContent(streamingBuffer.ToString());
                }
                streamingBuffer.Clear();
            });
        }

        public void ShowStreamingError(string error)
        {
            SafeInvoke(() =>
            {
                isStreamingResponse = false;
                HideTypingIndicator();
                SetMarkdownContent($"## AI Suggestion\n\n**Error:** {error}");
                streamingBuffer.Clear();
            });
        }

        private void ShowTypingIndicator()
        {
            typingTimer?.Stop();
            typingTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
            int dots = 0;
            typingTimer.Tick += (s, e) =>
            {
                if (!isStreamingResponse) { typingTimer.Stop(); return; }
                dots = (dots + 1) % 4;
                SetMarkdownContent(streamingBuffer.ToString() + new string('.', dots));
            };
            typingTimer.Start();
        }

        private void HideTypingIndicator()
        {
            typingTimer?.Stop();
            typingTimer = null;
        }

        #endregion

        #region Transcription Display

        private void RemovePlaceholder()
        {
            if (hasPlaceholder && TranscriptPlaceholder != null)
            {
                TranscriptPlaceholder.Visibility = Visibility.Collapsed;
                hasPlaceholder = false;
            }
        }

        public void AddUserTranscription(string text, bool isNewSegment = false)
        {
            SafeInvoke(() =>
            {
                if (TranscriptionPanel == null || string.IsNullOrWhiteSpace(text)) return;
                RemovePlaceholder();

                if (isNewSegment || newUserSegment || lastUserBubble == null)
                {
                    var (container, border, textBlock) = CreateBubble(text, isUser: true, highlight: isNewSegment);
                    TranscriptionPanel.Children.Add(container);
                    lastUserBubble = border;
                    lastUserText = textBlock;
                    newUserSegment = false;

                    if (isNewSegment)
                        FadeHighlight(border, isUser: true);
                }
                else if (lastUserText != null)
                {
                    lastUserText.Text += " " + text;
                }
                TranscriptionView?.ScrollToEnd();
            });
        }

        public void AddSystemTranscription(string text, bool isNewSegment = false)
        {
            SafeInvoke(() =>
            {
                if (TranscriptionPanel == null || string.IsNullOrWhiteSpace(text)) return;
                RemovePlaceholder();

                if (isNewSegment || newSystemSegment || lastSystemBubble == null)
                {
                    var (container, border, textBlock) = CreateBubble(text, isUser: false, highlight: isNewSegment);
                    TranscriptionPanel.Children.Add(container);
                    lastSystemBubble = border;
                    lastSystemText = textBlock;
                    newSystemSegment = false;

                    if (isNewSegment)
                        FadeHighlight(border, isUser: false);
                }
                else if (lastSystemText != null)
                {
                    lastSystemText.Text += " " + text;
                }
                TranscriptionView?.ScrollToEnd();
            });
        }

        public void MarkNextAsNewSegment()
        {
            newUserSegment = true;
            newSystemSegment = true;
        }

        public void ClearTranscription()
        {
            SafeInvoke(() =>
            {
                if (TranscriptionPanel == null) return;
                TranscriptionPanel.Children.Clear();
                lastUserBubble = null; lastUserText = null;
                lastSystemBubble = null; lastSystemText = null;
                newUserSegment = true;
                newSystemSegment = true;

                if (TranscriptPlaceholder != null)
                    TranscriptPlaceholder.Visibility = Visibility.Visible;
                hasPlaceholder = true;
            });
        }

        private (Grid container, Border border, TextBlock textBlock) CreateBubble(string text, bool isUser, bool highlight)
        {
            var container = new Grid { Margin = new Thickness(0, 3, 0, 3) };
            container.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            container.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2.5, GridUnitType.Star) });

            Color bubbleColor = highlight
                ? (isUser ? UserBubbleColor : SystemBubbleColor)
                : (Color)ColorConverter.ConvertFromString("#15FFFFFF");

            var border = new Border
            {
                Background = new SolidColorBrush(bubbleColor) { Opacity = highlight ? 0.6 : 1.0 },
                BorderBrush = new SolidColorBrush(isUser ? UserBubbleColor : SystemBubbleColor) { Opacity = 0.5 },
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(10, 6, 10, 6)
            };

            var textBlock = new TextBlock
            {
                Text = text,
                Foreground = new SolidColorBrush(TextColor),
                FontSize = 11,
                TextWrapping = TextWrapping.Wrap,
                FontFamily = new FontFamily("Segoe UI"),
            };

            border.Child = textBlock;

            // User = right column, System = left column
            Grid.SetColumn(border, isUser ? 1 : 0);
            container.Children.Add(border);

            // Source label
            var label = new TextBlock
            {
                Text = isUser ? "You" : "Other",
                FontSize = 9,
                Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#555555")),
                Margin = new Thickness(isUser ? 0 : 4, 0, isUser ? 4 : 0, 0),
                HorizontalAlignment = isUser ? HorizontalAlignment.Right : HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Bottom,
            };
            Grid.SetColumn(label, isUser ? 0 : 1);
            container.Children.Add(label);

            return (container, border, textBlock);
        }

        private void FadeHighlight(Border border, bool isUser)
        {
            var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            timer.Tick += (s, e) =>
            {
                border.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#15FFFFFF"));
                timer.Stop();
            };
            timer.Start();
        }

        #endregion

        #region Opacity

        public void UpdateOpacity(double value)
        {
            // Not used in new design (opacity controlled at window level)
        }

        #endregion

        #region Helpers

        private void SafeInvoke(Action action)
        {
            try
            {
                if (Dispatcher.CheckAccess())
                    action();
                else
                    Dispatcher.Invoke(action);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[SolutionRegion] UI error: {ex.Message}");
            }
        }

        #endregion
    }
}
