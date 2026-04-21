using System;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Markdig.Wpf;
using SnapEye.Models;

namespace SnapEye.SolutionRegion
{
    public partial class SolutionRegionAlpha : UserControl
    {
        // Streaming state
        private readonly StringBuilder streamingBuffer = new();
        private bool isStreamingResponse;
        private DispatcherTimer? typingTimer;
        private MarkdownViewer? currentStreamingViewer;
        private Border? currentStreamingCard;
        private string lastFinalAnswer = "";

        // Transcription state
        private Border? lastUserBubble;
        private TextBlock? lastUserText;
        private Border? lastSystemBubble;
        private TextBlock? lastSystemText;
        private MessageSource? lastBubbleSource;
        private DateTime lastUserActivity;
        private DateTime lastSystemActivity;
        private bool hasPlaceholder = true;
        private bool chatHasPlaceholder = true;
        private double bubbleMaxWidth = 360;
        private double chatBubbleMaxWidth = 420;

        // Chat sticky-bottom
        private bool chatStickyBottom = true;

        // Force-new-bubble after this much silence on the same source.
        private const double GapSecondsForNewBubble = 5.0;

        // Theme colors (You = blue, Other = gray, AI = green for transcript / dark card for chat)
        private static readonly Color UserBubbleColor   = (Color)ColorConverter.ConvertFromString("#2563EB");
        private static readonly Color UserBorderColor   = (Color)ColorConverter.ConvertFromString("#1D4ED8");
        private static readonly Color SystemBubbleColor = (Color)ColorConverter.ConvertFromString("#4B5563");
        private static readonly Color SystemBorderColor = (Color)ColorConverter.ConvertFromString("#374151");
        private static readonly Color AIBubbleColor     = (Color)ColorConverter.ConvertFromString("#16A34A");
        private static readonly Color AIBorderColor     = (Color)ColorConverter.ConvertFromString("#15803D");
        private static readonly Color BubbleTextColor   = (Color)ColorConverter.ConvertFromString("#F8FAFC");

        // Chat answer card (dark subtle background, not green — reserved for richer markdown)
        private static readonly Color AnswerCardFill   = (Color)ColorConverter.ConvertFromString("#1F2937");
        private static readonly Color AnswerCardBorder = (Color)ColorConverter.ConvertFromString("#334155");
        private static readonly Color AnswerAccent     = (Color)ColorConverter.ConvertFromString("#60A5FA");

        public SolutionRegionAlpha()
        {
            InitializeComponent();
            Loaded += (_, __) =>
            {
                if (TranscriptionView != null)
                {
                    UpdateBubbleMaxWidth(TranscriptionView.ActualWidth);
                    TranscriptionView.SizeChanged += (_, e) => UpdateBubbleMaxWidth(e.NewSize.Width);
                }
                if (ChatView != null)
                {
                    UpdateChatBubbleMaxWidth(ChatView.ActualWidth);
                    ChatView.SizeChanged += (_, e) => UpdateChatBubbleMaxWidth(e.NewSize.Width);
                    ChatView.ScrollChanged += ChatView_ScrollChanged;
                }
            };
        }

        private void UpdateBubbleMaxWidth(double viewWidth)
        {
            bubbleMaxWidth = Math.Max(180, (viewWidth * 0.75) - 40);
        }

        private void UpdateChatBubbleMaxWidth(double viewWidth)
        {
            // Answer cards take most of the width; user prompt bubbles ~80%.
            chatBubbleMaxWidth = Math.Max(200, (viewWidth * 0.82) - 20);
        }

        #region Tab Switching

        public void SwitchToChatTab()
        {
            SafeInvoke(() =>
            {
                ChatViewRoot.Visibility = Visibility.Visible;
                TranscriptionView.Visibility = Visibility.Collapsed;
            });
        }

        public void SwitchToTranscriptionTab()
        {
            SafeInvoke(() =>
            {
                ChatViewRoot.Visibility = Visibility.Collapsed;
                TranscriptionView.Visibility = Visibility.Visible;
            });
        }

        #endregion

        #region Chat / AI Content (Q&A thread)

        private void RemoveChatPlaceholder()
        {
            if (chatHasPlaceholder && ChatPlaceholder != null)
            {
                ChatPlaceholder.Visibility = Visibility.Collapsed;
                chatHasPlaceholder = false;
            }
        }

        /// <summary>Adds a plain info card to the chat history (used for one-off system messages / screen captures / errors).</summary>
        public void SetMarkdownContent(string markdown)
        {
            SafeInvoke(() =>
            {
                if (string.IsNullOrWhiteSpace(markdown)) return;
                RemoveChatPlaceholder();
                var card = BuildAnswerCard(markdown);
                ChatHistoryPanel.Children.Add(card);
                MaybeAutoScroll();
            });
        }

        public void AppendMarkdownContent(string markdown) => SetMarkdownContent(markdown);

        public void ClearContent()
        {
            SafeInvoke(() =>
            {
                ChatHistoryPanel.Children.Clear();
                chatHasPlaceholder = false;
                isStreamingResponse = false;
                HideTypingIndicator();
                streamingBuffer.Clear();
                currentStreamingViewer = null;
                currentStreamingCard = null;
                lastFinalAnswer = "";

                // Re-add placeholder
                if (ChatPlaceholder != null)
                {
                    ChatHistoryPanel.Children.Add(ChatPlaceholder);
                    ChatPlaceholder.Visibility = Visibility.Visible;
                    chatHasPlaceholder = true;
                }
                HideJumpToLatest();
                chatStickyBottom = true;
            });
        }

        public void ShowAIResponse(string response) => SetMarkdownContent(response);

        public void ShowLoading()
        {
            SafeInvoke(() =>
            {
                RemoveChatPlaceholder();
                var card = BuildAnswerCard("**Processing...**\n\nPlease wait while the AI generates a response.");
                ChatHistoryPanel.Children.Add(card);
                MaybeAutoScroll();
            });
        }

        public void ShowError(string error)
        {
            SafeInvoke(() =>
            {
                RemoveChatPlaceholder();
                var card = BuildErrorCard(error);
                ChatHistoryPanel.Children.Add(card);
                MaybeAutoScroll();
            });
        }

        /// <summary>Plain markdown of the most recent finalized AI answer (for copy/export).</summary>
        public string GetChatMarkdown()
        {
            return Dispatcher.CheckAccess()
                ? lastFinalAnswer
                : Dispatcher.Invoke(() => lastFinalAnswer);
        }

        /// <summary>Adds a "You" prompt bubble to the chat thread (typed input or quick-action label).</summary>
        public void AddUserPromptBubble(string text)
        {
            SafeInvoke(() =>
            {
                if (string.IsNullOrWhiteSpace(text)) return;
                RemoveChatPlaceholder();
                var row = BuildUserPromptRow(text.Trim());
                ChatHistoryPanel.Children.Add(row);
                MaybeAutoScroll();
            });
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
                RemoveChatPlaceholder();

                if (!string.IsNullOrWhiteSpace(question))
                    ChatHistoryPanel.Children.Add(BuildUserPromptRow(question.Trim()));

                currentStreamingCard = BuildAnswerCard("...");
                currentStreamingViewer = FindMarkdownViewer(currentStreamingCard);
                ChatHistoryPanel.Children.Add(currentStreamingCard);

                ShowTypingIndicator();
                MaybeAutoScroll();
            });
        }

        public void AppendStreamingToken(string token)
        {
            SafeInvoke(() =>
            {
                if (!isStreamingResponse) return;
                streamingBuffer.Append(token);
                if (currentStreamingViewer != null)
                    currentStreamingViewer.Markdown = streamingBuffer.ToString();
                MaybeAutoScroll();
            });
        }

        public void EndStreamingResponse(string? fullResponse = null)
        {
            SafeInvoke(() =>
            {
                isStreamingResponse = false;
                HideTypingIndicator();

                string finalText = !string.IsNullOrEmpty(fullResponse)
                    ? fullResponse!
                    : streamingBuffer.ToString();

                if (currentStreamingViewer != null)
                    currentStreamingViewer.Markdown = finalText;

                lastFinalAnswer = finalText;
                streamingBuffer.Clear();
                currentStreamingCard = null;
                currentStreamingViewer = null;

                if (!string.IsNullOrWhiteSpace(finalText))
                    AddAIAnswer(finalText);

                MaybeAutoScroll();
            });
        }

        public void ShowStreamingError(string error)
        {
            SafeInvoke(() =>
            {
                isStreamingResponse = false;
                HideTypingIndicator();
                if (currentStreamingViewer != null)
                    currentStreamingViewer.Markdown = $"**Error:** {error}";
                streamingBuffer.Clear();
                currentStreamingCard = null;
                currentStreamingViewer = null;
                MaybeAutoScroll();
            });
        }

        private void ShowTypingIndicator()
        {
            typingTimer?.Stop();
            typingTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
            int dots = 0;
            typingTimer.Tick += (s, e) =>
            {
                if (!isStreamingResponse || currentStreamingViewer == null) { typingTimer?.Stop(); return; }
                dots = (dots + 1) % 4;
                currentStreamingViewer.Markdown = streamingBuffer.ToString() + new string('.', dots);
            };
            typingTimer.Start();
        }

        private void HideTypingIndicator()
        {
            typingTimer?.Stop();
            typingTimer = null;
        }

        #endregion

        #region Chat Card Builders

        private Border BuildAnswerCard(string markdown)
        {
            var viewer = new MarkdownViewer
            {
                Background = Brushes.Transparent,
                Foreground = new SolidColorBrush(BubbleTextColor),
                Markdown = markdown ?? ""
            };

            var accent = new Border
            {
                Width = 3,
                Background = new SolidColorBrush(AnswerAccent),
                CornerRadius = new CornerRadius(2),
                Margin = new Thickness(0, 2, 10, 2)
            };

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            Grid.SetColumn(accent, 0);
            Grid.SetColumn(viewer, 1);
            grid.Children.Add(accent);
            grid.Children.Add(viewer);

            var card = new Border
            {
                Background = new SolidColorBrush(AnswerCardFill),
                BorderBrush = new SolidColorBrush(AnswerCardBorder),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(12),
                Padding = new Thickness(12, 10, 12, 10),
                Margin = new Thickness(0, 4, 28, 8),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Child = grid
            };
            return card;
        }

        private Border BuildErrorCard(string error)
        {
            return new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(0x33, 0xDC, 0x26, 0x26)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(0xDC, 0x26, 0x26)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(12),
                Padding = new Thickness(12, 10, 12, 10),
                Margin = new Thickness(0, 4, 28, 8),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Child = new TextBlock
                {
                    Text = "⚠ " + error,
                    Foreground = new SolidColorBrush(Color.FromRgb(0xFE, 0xCA, 0xCA)),
                    FontSize = 13,
                    TextWrapping = TextWrapping.Wrap,
                    FontFamily = new FontFamily("Segoe UI")
                }
            };
        }

        private Grid BuildUserPromptRow(string text)
        {
            var bubble = new Border
            {
                Background = new SolidColorBrush(UserBubbleColor),
                BorderBrush = new SolidColorBrush(UserBorderColor),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(14),
                Padding = new Thickness(12, 8, 12, 8),
                MaxWidth = chatBubbleMaxWidth,
                HorizontalAlignment = HorizontalAlignment.Right,
                Child = new TextBlock
                {
                    Text = text,
                    Foreground = new SolidColorBrush(BubbleTextColor),
                    FontSize = 13,
                    LineHeight = 18,
                    TextWrapping = TextWrapping.Wrap,
                    FontFamily = new FontFamily("Segoe UI")
                }
            };

            var row = new Grid { Margin = new Thickness(28, 6, 0, 4) };
            row.Children.Add(bubble);
            return row;
        }

        private static MarkdownViewer? FindMarkdownViewer(DependencyObject root)
        {
            if (root is MarkdownViewer mv) return mv;
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            {
                var found = FindMarkdownViewer(VisualTreeHelper.GetChild(root, i));
                if (found != null) return found;
            }
            return null;
        }

        #endregion

        #region Sticky-bottom Auto-Scroll

        private void ChatView_ScrollChanged(object sender, ScrollChangedEventArgs e)
        {
            if (ChatView == null) return;
            double distanceFromBottom = ChatView.ScrollableHeight - ChatView.VerticalOffset;
            bool atBottom = distanceFromBottom < 24; // tolerance in px

            // User-initiated scroll changes vertical offset without extent change.
            if (Math.Abs(e.VerticalChange) > 0.5 && Math.Abs(e.ExtentHeightChange) < 0.5)
            {
                chatStickyBottom = atBottom;
                if (atBottom) HideJumpToLatest();
            }

            // New content arrived and user is not at bottom → show jump button.
            if (e.ExtentHeightChange > 0.5 && !chatStickyBottom)
                ShowJumpToLatest();
            else if (atBottom)
                HideJumpToLatest();
        }

        private void MaybeAutoScroll()
        {
            if (ChatView == null) return;
            if (chatStickyBottom)
            {
                ChatView.Dispatcher.BeginInvoke(new Action(() => ChatView.ScrollToEnd()), DispatcherPriority.Background);
                HideJumpToLatest();
            }
            else
            {
                ShowJumpToLatest();
            }
        }

        private void JumpToLatest_Click(object sender, RoutedEventArgs e)
        {
            chatStickyBottom = true;
            ChatView?.ScrollToEnd();
            HideJumpToLatest();
        }

        private void ShowJumpToLatest()
        {
            if (JumpToLatestBtn != null) JumpToLatestBtn.Visibility = Visibility.Visible;
        }

        private void HideJumpToLatest()
        {
            if (JumpToLatestBtn != null) JumpToLatestBtn.Visibility = Visibility.Collapsed;
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
            AddTranscriptBubble(text, MessageSource.Microphone, isNewSegment);
        }

        public void AddSystemTranscription(string text, bool isNewSegment = false)
        {
            AddTranscriptBubble(text, MessageSource.Speaker, isNewSegment);
        }

        public void AddAIAnswer(string text)
        {
            SafeInvoke(() =>
            {
                if (TranscriptionPanel == null || string.IsNullOrWhiteSpace(text)) return;
                RemovePlaceholder();

                var (row, _, _) = CreateChatRow(text.Trim(), source: null);
                TranscriptionPanel.Children.Add(row);

                lastBubbleSource = null;
                lastUserBubble = lastSystemBubble = null;
                lastUserText = lastSystemText = null;

                TranscriptionView?.ScrollToEnd();
            });
        }

        public void MarkNextAsNewSegment()
        {
            lastBubbleSource = null;
            lastUserActivity = default;
            lastSystemActivity = default;
        }

        public void ClearTranscription()
        {
            SafeInvoke(() =>
            {
                if (TranscriptionPanel == null) return;
                TranscriptionPanel.Children.Clear();
                lastUserBubble = null; lastUserText = null;
                lastSystemBubble = null; lastSystemText = null;
                lastBubbleSource = null;
                lastUserActivity = default;
                lastSystemActivity = default;

                if (TranscriptPlaceholder != null)
                {
                    TranscriptionPanel.Children.Add(TranscriptPlaceholder);
                    TranscriptPlaceholder.Visibility = Visibility.Visible;
                }
                hasPlaceholder = true;
            });
        }

        /// <summary>Clears both the chat thread and the transcription (used on End Session / new meeting).</summary>
        public void ClearAll()
        {
            ClearContent();
            ClearTranscription();
        }

        private void AddTranscriptBubble(string text, MessageSource source, bool isNewSegment)
        {
            SafeInvoke(() =>
            {
                if (TranscriptionPanel == null || string.IsNullOrWhiteSpace(text)) return;
                RemovePlaceholder();

                bool isUser = source == MessageSource.Microphone;
                DateTime now = DateTime.Now;
                DateTime lastActivity = isUser ? lastUserActivity : lastSystemActivity;
                Border? lastBubble = isUser ? lastUserBubble : lastSystemBubble;
                TextBlock? lastText = isUser ? lastUserText : lastSystemText;

                bool flipped = lastBubbleSource.HasValue && lastBubbleSource.Value != source;
                bool gapped = lastActivity != default
                              && (now - lastActivity).TotalSeconds > GapSecondsForNewBubble;
                bool startNew = isNewSegment || flipped || gapped || lastBubble == null;

                if (startNew)
                {
                    var (row, border, textBlock) = CreateChatRow(text.Trim(), source);
                    TranscriptionPanel.Children.Add(row);
                    if (isUser) { lastUserBubble = border; lastUserText = textBlock; }
                    else        { lastSystemBubble = border; lastSystemText = textBlock; }
                }
                else if (lastText != null)
                {
                    lastText.Text = (lastText.Text + " " + text).Trim();
                }

                if (isUser) lastUserActivity = now; else lastSystemActivity = now;
                lastBubbleSource = source;
                TranscriptionView?.ScrollToEnd();
            });
        }

        private (Grid row, Border bubble, TextBlock textBlock) CreateChatRow(string text, MessageSource? source)
        {
            bool isAI   = !source.HasValue;
            bool isUser = source == MessageSource.Microphone;

            Color fill, edge;
            string letter;
            HorizontalAlignment align;

            if (isAI)        { fill = AIBubbleColor;     edge = AIBorderColor;     letter = "A"; align = HorizontalAlignment.Left;  }
            else if (isUser) { fill = UserBubbleColor;   edge = UserBorderColor;   letter = "Y"; align = HorizontalAlignment.Right; }
            else             { fill = SystemBubbleColor; edge = SystemBorderColor; letter = "O"; align = HorizontalAlignment.Left;  }

            var row = new Grid { Margin = new Thickness(0, 4, 0, 4) };

            var stack = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = align,
            };

            var avatar = new Border
            {
                Width = 26,
                Height = 26,
                CornerRadius = new CornerRadius(13),
                Background = new SolidColorBrush(fill),
                BorderBrush = new SolidColorBrush(edge),
                BorderThickness = new Thickness(1),
                VerticalAlignment = VerticalAlignment.Bottom,
                Margin = isUser ? new Thickness(8, 0, 0, 0) : new Thickness(0, 0, 8, 0),
                Child = new TextBlock
                {
                    Text = letter,
                    Foreground = new SolidColorBrush(Colors.White),
                    FontWeight = FontWeights.Bold,
                    FontSize = 11,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    FontFamily = new FontFamily("Segoe UI"),
                },
            };

            var bubble = new Border
            {
                Background = new SolidColorBrush(fill),
                BorderBrush = new SolidColorBrush(edge),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(12),
                Padding = new Thickness(12, 8, 12, 8),
                MaxWidth = bubbleMaxWidth,
            };

            var textBlock = new TextBlock
            {
                Text = text,
                Foreground = new SolidColorBrush(BubbleTextColor),
                FontSize = 13,
                LineHeight = 18,
                TextWrapping = TextWrapping.Wrap,
                FontFamily = new FontFamily("Segoe UI"),
            };

            bubble.Child = textBlock;

            if (isUser)
            {
                stack.Children.Add(bubble);
                stack.Children.Add(avatar);
            }
            else
            {
                stack.Children.Add(avatar);
                stack.Children.Add(bubble);
            }

            row.Children.Add(stack);
            return (row, bubble, textBlock);
        }

        #endregion

        #region Opacity

        public void UpdateOpacity(double value)
        {
            // Opacity controlled at window level
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
