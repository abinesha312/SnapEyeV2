using System;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
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
        private Border? currentStreamingCard;
        // Live plain-text element shown WHILE a chat answer streams. Rendering markdown
        // on every token re-parses the whole document (O(n^2) for long answers) and makes
        // the chat feel laggy; we stream into a cheap TextBlock instead and swap in a
        // fully-rendered MarkdownViewer once the answer completes.
        private TextBlock? currentStreamingTextBlock;
        private string lastFinalAnswer = "";

        // Coalesces token updates so the markdown document is re-parsed at most ~12 fps
        // instead of on every single SSE token (which could arrive 50+ times/second).
        private DispatcherTimer? streamFlushTimer;
        private bool streamDirty;
        private static readonly TimeSpan StreamFlushInterval = TimeSpan.FromMilliseconds(40);

        // Transcription state
        private Border? lastUserBubble;
        private TextBlock? lastUserText;
        private Border? lastSystemBubble;
        private TextBlock? lastSystemText;
        private MessageSource? lastBubbleSource;
        // Backend message id shown in each live bubble. Every transcript event carries
        // the FULL text of its utterance, so events for the same id must REPLACE the
        // bubble text (appending would duplicate everything already displayed).
        private string? lastUserMessageId;
        private string? lastSystemMessageId;
        // Text of earlier utterances already merged into the current live bubble. Each
        // backend message carries the FULL text of only its own utterance, so when we
        // merge consecutive same-speaker utterances (gap < GapSecondsForNewBubble) into
        // one bubble we keep the finalized prefix here and append the current message to it.
        private string lastUserPrefix = "";
        private string lastSystemPrefix = "";
        private DateTime lastUserActivity;
        private DateTime lastSystemActivity;
        private bool hasPlaceholder = true;
        private bool chatHasPlaceholder = true;
        private double bubbleMaxWidth = 360;
        private double chatBubbleMaxWidth = 420;

        // Transcript-targeted AI streaming state
        private readonly StringBuilder transcriptStreamingBuffer = new();
        private bool isTranscriptStreaming;
        private TextBlock? transcriptStreamingTextBlock;
        // The bubble hosting the live transcript answer. On completion we swap its plain
        // TextBlock for a MarkdownViewer so **bold**, `code`, lists etc. render properly.
        private Border? transcriptStreamingBubble;
        private DispatcherTimer? transcriptTypingTimer;

        // Chat sticky-bottom
        private bool chatStickyBottom = true;
        // Transcript view auto-follow. Live transcription follows the newest line only while
        // the user is already at the bottom; once they scroll up they stay put. Streaming AI
        // answers never auto-scroll (the answer stays anchored where it began so the user can
        // read/scroll freely during generation).
        private bool transcriptStickyBottom = true;

        // Force-new-bubble after this much silence on the same source. Below this gap,
        // consecutive same-speaker utterances are merged into the same bubble so a brief
        // (~1s) pause no longer fragments speech into many bubbles.
        private const double GapSecondsForNewBubble = 3.0;

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
                if (TranscriptionView != null)
                {
                    TranscriptionView.ScrollChanged += TranscriptionView_ScrollChanged;
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
                TranscriptionViewRoot.Visibility = Visibility.Collapsed;
            });
        }

        public void SwitchToTranscriptionTab()
        {
            SafeInvoke(() =>
            {
                ChatViewRoot.Visibility = Visibility.Collapsed;
                TranscriptionViewRoot.Visibility = Visibility.Visible;
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
                StopStreamFlushTimer();
                streamingBuffer.Clear();
                currentStreamingCard = null;
                currentStreamingTextBlock = null;
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

                // Guard against duplicate stream starts: if a stream is already in progress
                // (e.g. cancel + restart race), clean it up before starting a new card.
                if (isStreamingResponse && currentStreamingCard != null)
                {
                    StopStreamFlushTimer();
                    streamingBuffer.Clear();
                    currentStreamingCard = null;
                    currentStreamingTextBlock = null;
                }

                isStreamingResponse = true;
                streamingBuffer.Clear();
                RemoveChatPlaceholder();

                if (!string.IsNullOrWhiteSpace(question))
                    ChatHistoryPanel.Children.Add(BuildUserPromptRow(question.Trim()));

                // Stream into a cheap live TextBlock; we render the rich MarkdownViewer once
                // the answer is complete (keeps token display instant even for long answers).
                currentStreamingCard = BuildStreamingCard(out currentStreamingTextBlock);
                ChatHistoryPanel.Children.Add(currentStreamingCard);

                StartStreamFlushTimer();
                MaybeAutoScroll();
            });
        }

        /// <summary>
        /// Called on the UI dispatcher for every streamed token. We ONLY append to the buffer
        /// and set a dirty flag; actual markdown re-rendering happens at a throttled rate
        /// (see <see cref="StreamFlushInterval"/>) to prevent the Markdig parser from being
        /// re-invoked on every single token.
        /// </summary>
        public void AppendStreamingToken(string token)
        {
            // Fast path: keep this extremely cheap — no allocations, no UI work.
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke(new Action<string>(AppendStreamingToken), DispatcherPriority.Input, token);
                return;
            }
            if (!isStreamingResponse) return;
            streamingBuffer.Append(token);
            streamDirty = true;
        }

        /// <summary>Answer card whose body is a fast plain-text block, used during streaming.</summary>
        private Border BuildStreamingCard(out TextBlock liveText)
        {
            liveText = new TextBlock
            {
                Text = "…",
                Foreground = new SolidColorBrush(BubbleTextColor),
                FontSize = 13,
                LineHeight = 18,
                TextWrapping = TextWrapping.Wrap,
                FontFamily = new FontFamily("Segoe UI"),
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
            Grid.SetColumn(liveText, 1);
            grid.Children.Add(accent);
            grid.Children.Add(liveText);

            return new Border
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
        }

        public void EndStreamingResponse(string? fullResponse = null, double? confidence = null)
        {
            SafeInvoke(() =>
            {
                isStreamingResponse = false;
                StopStreamFlushTimer();

                string finalText = !string.IsNullOrEmpty(fullResponse)
                    ? fullResponse!
                    : streamingBuffer.ToString();

                // Swap the live plain-text body for a fully-rendered markdown view so
                // code blocks, **bold**, lists etc. display correctly in the final answer.
                if (currentStreamingCard != null && !string.IsNullOrWhiteSpace(finalText))
                {
                    var content = BuildAnswerContent(finalText, out _);
                    if (confidence.HasValue)
                    {
                        var stack = new StackPanel();
                        stack.Children.Add(content);
                        stack.Children.Add(BuildConfidenceBadge(confidence.Value));
                        currentStreamingCard.Child = stack;
                    }
                    else
                    {
                        currentStreamingCard.Child = content;
                    }
                }
                else if (currentStreamingTextBlock != null)
                    currentStreamingTextBlock.Text = finalText;

                lastFinalAnswer = finalText;
                streamingBuffer.Clear();
                currentStreamingCard = null;
                currentStreamingTextBlock = null;

                MaybeAutoScroll();
            });
        }

        public void ShowStreamingError(string error)
        {
            SafeInvoke(() =>
            {
                isStreamingResponse = false;
                StopStreamFlushTimer();
                if (currentStreamingTextBlock != null)
                    currentStreamingTextBlock.Text = $"⚠ Error: {error}";
                streamingBuffer.Clear();
                currentStreamingCard = null;
                currentStreamingTextBlock = null;
                MaybeAutoScroll();
            });
        }

        private void StartStreamFlushTimer()
        {
            StopStreamFlushTimer();
            streamDirty = false;
            streamFlushTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = StreamFlushInterval };
            streamFlushTimer.Tick += (_, _) => FlushStreamingBuffer();
            streamFlushTimer.Start();
        }

        private void StopStreamFlushTimer()
        {
            if (streamFlushTimer != null)
            {
                streamFlushTimer.Stop();
                streamFlushTimer = null;
            }
            streamDirty = false;
        }

        /// <summary>Pushes the latest streaming buffer into the live text block, at most once per tick.</summary>
        private void FlushStreamingBuffer()
        {
            if (!streamDirty || !isStreamingResponse || currentStreamingTextBlock == null) return;
            streamDirty = false;
            // Snapshot under UI thread — StringBuilder is not thread-safe, but all callers hit this
            // via the dispatcher, so ToString() is safe here.
            string text = streamingBuffer.ToString();
            if (text.Length == 0) return;
            try
            {
                currentStreamingTextBlock.Text = text;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[SolutionRegion] stream flush error: {ex.Message}");
            }
            // No auto-scroll during generation: the answer stays anchored where it began so
            // the user can read and scroll freely. The "New" pill lets them jump to the latest.
        }

        #endregion

        #region Chat Card Builders

        private Border BuildAnswerCard(string markdown)
        {
            return new Border
            {
                Background = new SolidColorBrush(AnswerCardFill),
                BorderBrush = new SolidColorBrush(AnswerCardBorder),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(12),
                Padding = new Thickness(12, 10, 12, 10),
                Margin = new Thickness(0, 4, 28, 8),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Child = BuildAnswerContent(markdown, out _)
            };
        }

        /// <summary>
        /// Builds the accent-bar + MarkdownViewer content used inside answer cards (chat) and
        /// AI transcript bubbles. The viewer gets the dark code theme applied so fenced code
        /// blocks and inline code are readable instead of rendering on a near-white background.
        /// </summary>
        private Grid BuildAnswerContent(string markdown, out MarkdownViewer viewer)
        {
            viewer = new MarkdownViewer
            {
                Background = Brushes.Transparent,
                Foreground = new SolidColorBrush(BubbleTextColor),
                Markdown = markdown ?? ""
            };
            ApplyMarkdownTheme(viewer);

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
            return grid;
        }

        /// <summary>
        /// Small pill showing the backend-reported answer confidence (0..1). Grounded,
        /// high-confidence answers render green; softer ones amber.
        /// </summary>
        private static Border BuildConfidenceBadge(double confidence)
        {
            int pct = (int)Math.Round(Math.Max(0.0, Math.Min(1.0, confidence)) * 100.0);
            Color fill, edge, text;
            if (confidence >= 0.85)
            {
                fill = Color.FromRgb(0x1B, 0x3A, 0x2A); edge = Color.FromRgb(0x2E, 0x7D, 0x54); text = Color.FromRgb(0x86, 0xEF, 0xAC);
            }
            else if (confidence >= 0.7)
            {
                fill = Color.FromRgb(0x3A, 0x33, 0x1B); edge = Color.FromRgb(0x8A, 0x6D, 0x2E); text = Color.FromRgb(0xFD, 0xE0, 0x47);
            }
            else
            {
                fill = Color.FromRgb(0x2A, 0x2A, 0x33); edge = Color.FromRgb(0x55, 0x55, 0x63); text = Color.FromRgb(0xC7, 0xC3, 0xD1);
            }

            return new Border
            {
                Background = new SolidColorBrush(fill),
                BorderBrush = new SolidColorBrush(edge),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(999),
                Padding = new Thickness(8, 2, 8, 2),
                Margin = new Thickness(0, 8, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Left,
                Child = new TextBlock
                {
                    Text = $"Confidence {pct}%",
                    FontSize = 10.5,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = new SolidColorBrush(text),
                }
            };
        }

        // ── Markdown code theme ────────────────────────────────────────────────────────
        // Markdig.Wpf resolves block/inline styles via DynamicResource keys, and the
        // control's own (light) defaults sit closer in the resource tree than our
        // UserControl resources. The only reliable override point is each viewer's own
        // Resources dictionary (confirmed by the library author), so we inject a dark
        // code theme there. Styles are immutable once built, so we share single instances.
        private static readonly Color CodeBg       = (Color)ColorConverter.ConvertFromString("#0D1117");
        private static readonly Color CodeFg       = (Color)ColorConverter.ConvertFromString("#E6EDF3");
        private static readonly Color InlineCodeBg = (Color)ColorConverter.ConvertFromString("#30363D");
        private static readonly Color InlineCodeFg = (Color)ColorConverter.ConvertFromString("#93C5FD");
        private static readonly FontFamily MonoFont = new("Cascadia Code, Consolas, Courier New");

        private static Style? _codeBlockStyle;
        private static Style? _inlineCodeStyle;

        private static void ApplyMarkdownTheme(MarkdownViewer viewer)
        {
            try
            {
                if (_codeBlockStyle == null)
                {
                    var s = new Style(typeof(Paragraph));
                    s.Setters.Add(new Setter(TextElement.BackgroundProperty, new SolidColorBrush(CodeBg)));
                    s.Setters.Add(new Setter(TextElement.ForegroundProperty, new SolidColorBrush(CodeFg)));
                    s.Setters.Add(new Setter(TextElement.FontFamilyProperty, MonoFont));
                    s.Setters.Add(new Setter(TextElement.FontSizeProperty, 12.5));
                    s.Setters.Add(new Setter(Block.PaddingProperty, new Thickness(10, 8, 10, 8)));
                    s.Setters.Add(new Setter(Block.MarginProperty, new Thickness(0, 4, 0, 6)));
                    s.Setters.Add(new Setter(Block.BorderBrushProperty, new SolidColorBrush((Color)ColorConverter.ConvertFromString("#30363D"))));
                    s.Setters.Add(new Setter(Block.BorderThicknessProperty, new Thickness(1)));
                    s.Setters.Add(new Setter(Block.LineHeightProperty, 17.0));
                    s.Seal();
                    _codeBlockStyle = s;
                }
                if (_inlineCodeStyle == null)
                {
                    var s = new Style(typeof(Run));
                    s.Setters.Add(new Setter(TextElement.BackgroundProperty, new SolidColorBrush(InlineCodeBg)));
                    s.Setters.Add(new Setter(TextElement.ForegroundProperty, new SolidColorBrush(InlineCodeFg)));
                    s.Setters.Add(new Setter(TextElement.FontFamilyProperty, MonoFont));
                    s.Seal();
                    _inlineCodeStyle = s;
                }

                viewer.Resources[Styles.CodeBlockStyleKey] = _codeBlockStyle;
                viewer.Resources[Styles.CodeStyleKey] = _inlineCodeStyle;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[SolutionRegion] markdown theme error: {ex.Message}");
            }
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

            // New content arrived. While streaming we never auto-follow (frozen), so if the
            // fresh text pushed the bottom out of view, surface the jump button.
            if (e.ExtentHeightChange > 0.5)
            {
                bool following = !isStreamingResponse && chatStickyBottom;
                if (!following && !atBottom)
                    ShowJumpToLatest();
                else if (atBottom)
                    HideJumpToLatest();
            }
        }

        private void MaybeAutoScroll()
        {
            if (ChatView == null) return;
            // Frozen while a response streams: the view stays where it was when the answer
            // started and the user scrolls freely (the jump pill opts back into the latest).
            if (isStreamingResponse) return;
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

        /// <summary>
        /// Tracks whether the transcript view is pinned to the bottom. A user-initiated
        /// scroll up releases the pin so live transcription / answers no longer yank the
        /// viewport; scrolling back to the bottom re-enables follow.
        /// </summary>
        private void TranscriptionView_ScrollChanged(object sender, ScrollChangedEventArgs e)
        {
            if (TranscriptionView == null) return;
            double distanceFromBottom = TranscriptionView.ScrollableHeight - TranscriptionView.VerticalOffset;
            bool atBottom = distanceFromBottom < 24;

            // User-initiated scroll (offset moved without the extent growing).
            if (Math.Abs(e.VerticalChange) > 0.5 && Math.Abs(e.ExtentHeightChange) < 0.5)
            {
                transcriptStickyBottom = atBottom;
                if (atBottom) HideTranscriptJump();
            }

            // New content arrived. While an answer streams we freeze the viewport, so show the
            // jump button when the newest text is below the fold.
            if (e.ExtentHeightChange > 0.5)
            {
                bool following = !isTranscriptStreaming && transcriptStickyBottom;
                if (!following && !atBottom)
                    ShowTranscriptJump();
                else if (atBottom)
                    HideTranscriptJump();
            }
        }

        /// <summary>
        /// Follows the newest transcript content only while the user is at the bottom AND no
        /// answer is streaming. During streaming the view is frozen so the user reads/scrolls
        /// freely; the jump pill lets them opt back into the latest.
        /// </summary>
        private void MaybeAutoScrollTranscript()
        {
            if (TranscriptionView == null) return;
            if (isTranscriptStreaming) return;
            if (transcriptStickyBottom)
                TranscriptionView.Dispatcher.BeginInvoke(
                    new Action(() => TranscriptionView.ScrollToEnd()), DispatcherPriority.Background);
        }

        private void TranscriptJumpToLatest_Click(object sender, RoutedEventArgs e)
        {
            transcriptStickyBottom = true;
            TranscriptionView?.ScrollToEnd();
            HideTranscriptJump();
        }

        private void ShowTranscriptJump()
        {
            if (TranscriptJumpBtn != null) TranscriptJumpBtn.Visibility = Visibility.Visible;
        }

        private void HideTranscriptJump()
        {
            if (TranscriptJumpBtn != null) TranscriptJumpBtn.Visibility = Visibility.Collapsed;
        }

        /// <summary>
        /// Cancels/finalizes any in-progress streaming answer in both views without an error
        /// banner. Called when the user hits Stop. Shows a subtle "Canceled." note when nothing
        /// had streamed yet.
        /// </summary>
        public void CancelStreaming()
        {
            SafeInvoke(() =>
            {
                if (isStreamingResponse)
                {
                    string partial = streamingBuffer.ToString();
                    EndStreamingResponse(string.IsNullOrWhiteSpace(partial) ? "_Canceled._" : partial);
                }
                if (isTranscriptStreaming)
                {
                    string partial = transcriptStreamingBuffer.ToString();
                    EndStreamingAnswerInTranscript(string.IsNullOrWhiteSpace(partial) ? "_Canceled._" : partial);
                }
            });
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

        public void AddUserTranscription(string text, bool isNewSegment = false, string? messageId = null)
        {
            AddTranscriptBubble(text, MessageSource.Microphone, isNewSegment, messageId);
        }

        public void AddSystemTranscription(string text, bool isNewSegment = false, string? messageId = null)
        {
            AddTranscriptBubble(text, MessageSource.Speaker, isNewSegment, messageId);
        }

        public void AddAIAnswer(string text)
        {
            SafeInvoke(() =>
            {
                if (TranscriptionPanel == null || string.IsNullOrWhiteSpace(text)) return;
                RemovePlaceholder();

                var (row, bubble, _) = CreateChatRow(text.Trim(), source: null);
                // Render AI answers as markdown so formatting (bold, code, lists) shows.
                bubble.Child = BuildBubbleMarkdownViewer(text.Trim());
                TranscriptionPanel.Children.Add(row);

                lastBubbleSource = null;
                lastUserBubble = lastSystemBubble = null;
                lastUserText = lastSystemText = null;
                lastUserPrefix = lastSystemPrefix = "";

                TranscriptionView?.ScrollToEnd();
            });
        }

        /// <summary>Adds a user prompt bubble (typed or quick-action) to the transcript view.</summary>
        public void AddUserPromptToTranscript(string text)
        {
            SafeInvoke(() =>
            {
                if (TranscriptionPanel == null || string.IsNullOrWhiteSpace(text)) return;
                RemovePlaceholder();

                var (row, _, _) = CreateChatRow(text.Trim(), MessageSource.Microphone);
                TranscriptionPanel.Children.Add(row);

                lastBubbleSource = MessageSource.Microphone;
                lastUserBubble = lastSystemBubble = null;
                lastUserText = lastSystemText = null;
                lastUserPrefix = lastSystemPrefix = "";
                lastUserActivity = default;
                lastSystemActivity = default;

                TranscriptionView?.ScrollToEnd();
            });
        }

        /// <summary>Adds a screen capture OCR text entry to the chat view (no image shown).</summary>
        public void AddScreenCaptureToChat(string ocrText)
        {
            SafeInvoke(() =>
            {
                if (ChatHistoryPanel == null || string.IsNullOrWhiteSpace(ocrText)) return;
                if (ChatPlaceholder != null) ChatPlaceholder.Visibility = Visibility.Collapsed;

                var card = BuildScreenCaptureCard(ocrText.Trim());
                ChatHistoryPanel.Children.Add(card);
                ChatView?.ScrollToEnd();
            });
        }

        /// <summary>Adds a screen capture OCR text entry to the transcript view (no image shown).</summary>
        public void AddScreenCaptureToTranscript(string ocrText)
        {
            SafeInvoke(() =>
            {
                if (TranscriptionPanel == null || string.IsNullOrWhiteSpace(ocrText)) return;
                RemovePlaceholder();

                var card = BuildScreenCaptureCard(ocrText.Trim());
                TranscriptionPanel.Children.Add(card);

                lastBubbleSource = null;
                lastUserBubble = lastSystemBubble = null;
                lastUserText = lastSystemText = null;
                lastUserPrefix = lastSystemPrefix = "";

                TranscriptionView?.ScrollToEnd();
            });
        }

        private Border BuildScreenCaptureCard(string ocrText)
        {
            var header = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Margin = new Thickness(0, 0, 0, 6),
            };
            var iconBox = new Viewbox { Width = 12, Height = 12, Margin = new Thickness(0, 0, 6, 0) };
            var iconCanvas = new System.Windows.Controls.Canvas { Width = 24, Height = 24 };
            var iconPath = new System.Windows.Shapes.Path
            {
                Fill = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#93C5FD")),
                Data = Geometry.Parse("M4,4H7L9,2H15L17,4H20A2,2 0 0,1 22,6V18A2,2 0 0,1 20,20H4A2,2 0 0,1 2,18V6A2,2 0 0,1 4,4M12,7A5,5 0 0,0 7,12A5,5 0 0,0 12,17A5,5 0 0,0 17,12A5,5 0 0,0 12,7M12,9A3,3 0 0,1 15,12A3,3 0 0,1 12,15A3,3 0 0,1 9,12A3,3 0 0,1 12,9Z"),
            };
            iconCanvas.Children.Add(iconPath);
            iconBox.Child = iconCanvas;
            header.Children.Add(iconBox);
            header.Children.Add(new TextBlock
            {
                Text = "Screen Capture",
                Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#93C5FD")),
                FontSize = 11,
                FontWeight = FontWeights.SemiBold,
                FontFamily = new FontFamily("Segoe UI"),
                VerticalAlignment = VerticalAlignment.Center,
            });

            var body = new TextBlock
            {
                Text = ocrText,
                Foreground = new SolidColorBrush(BubbleTextColor),
                FontSize = 12,
                LineHeight = 17,
                TextWrapping = TextWrapping.Wrap,
                FontFamily = new FontFamily("Segoe UI"),
            };

            var stack = new StackPanel();
            stack.Children.Add(header);
            stack.Children.Add(body);

            return new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(0x33, 0x25, 0x63, 0xEB)),
                BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#2563EB")),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(10, 8, 10, 8),
                Margin = new Thickness(4, 6, 4, 6),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Child = stack,
            };
        }

        #endregion

        #region Transcript-Targeted AI Streaming

        /// <summary>Starts a streaming AI answer that renders directly inside the transcript view.</summary>
        public void BeginStreamingAnswerInTranscript(string? question)
        {
            SafeInvoke(() =>
            {
                if (TranscriptionPanel == null) return;
                RemovePlaceholder();

                if (!string.IsNullOrWhiteSpace(question))
                {
                    var (qRow, _, _) = CreateChatRow(question!.Trim(), MessageSource.Microphone);
                    TranscriptionPanel.Children.Add(qRow);
                }

                isTranscriptStreaming = true;
                transcriptStreamingBuffer.Clear();

                var (row, bubble, textBlock) = CreateChatRow("...", source: null);
                TranscriptionPanel.Children.Add(row);
                transcriptStreamingTextBlock = textBlock;
                transcriptStreamingBubble = bubble;

                lastBubbleSource = null;
                lastUserBubble = lastSystemBubble = null;
                lastUserText = lastSystemText = null;
                lastUserPrefix = lastSystemPrefix = "";

                StartTranscriptTypingIndicator();
                // Freeze the viewport where it was when the answer started — no auto-scroll
                // during streaming. If the new text lands below the fold, the ScrollChanged
                // handler surfaces the "New" jump pill so the user can opt in.
            });
        }

        public void AppendStreamingAnswerToken(string token)
        {
            SafeInvoke(() =>
            {
                if (!isTranscriptStreaming || transcriptStreamingTextBlock == null) return;
                transcriptStreamingBuffer.Append(token);
                transcriptStreamingTextBlock.Text = transcriptStreamingBuffer.ToString();
                // No auto-scroll while the answer streams — the user controls the scroll.
            });
        }

        public void EndStreamingAnswerInTranscript(string? fullResponse = null, double? confidence = null)
        {
            SafeInvoke(() =>
            {
                isTranscriptStreaming = false;
                StopTranscriptTypingIndicator();

                string finalText = !string.IsNullOrEmpty(fullResponse)
                    ? fullResponse!
                    : transcriptStreamingBuffer.ToString();

                // Replace the live plain text with rendered markdown so **bold**, `code`,
                // lists and code blocks display correctly in the transcript answer bubble.
                if (transcriptStreamingBubble != null && !string.IsNullOrWhiteSpace(finalText))
                {
                    if (confidence.HasValue)
                    {
                        var stack = new StackPanel();
                        stack.Children.Add(BuildBubbleMarkdownViewer(finalText));
                        stack.Children.Add(BuildConfidenceBadge(confidence.Value));
                        transcriptStreamingBubble.Child = stack;
                    }
                    else
                    {
                        transcriptStreamingBubble.Child = BuildBubbleMarkdownViewer(finalText);
                    }
                }
                else if (transcriptStreamingTextBlock != null)
                    transcriptStreamingTextBlock.Text = finalText;

                lastFinalAnswer = finalText;
                transcriptStreamingBuffer.Clear();
                transcriptStreamingTextBlock = null;
                transcriptStreamingBubble = null;

                // Keep the user's scroll position when the plain text swaps to rendered
                // markdown — don't jump to the end on completion.
            });
        }

        /// <summary>Themed MarkdownViewer sized to fit inside a transcript answer bubble.</summary>
        private MarkdownViewer BuildBubbleMarkdownViewer(string markdown)
        {
            var viewer = new MarkdownViewer
            {
                Background = Brushes.Transparent,
                Foreground = new SolidColorBrush(BubbleTextColor),
                Markdown = markdown ?? "",
                MaxWidth = bubbleMaxWidth,
            };
            ApplyMarkdownTheme(viewer);
            return viewer;
        }

        public void ShowStreamingAnswerError(string error)
        {
            SafeInvoke(() =>
            {
                isTranscriptStreaming = false;
                StopTranscriptTypingIndicator();
                if (transcriptStreamingTextBlock != null)
                    transcriptStreamingTextBlock.Text = "⚠ Error: " + error;
                transcriptStreamingBuffer.Clear();
                transcriptStreamingTextBlock = null;
                transcriptStreamingBubble = null;
                TranscriptionView?.ScrollToEnd();
            });
        }

        private void StartTranscriptTypingIndicator()
        {
            transcriptTypingTimer?.Stop();
            transcriptTypingTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
            int dots = 0;
            transcriptTypingTimer.Tick += (s, e) =>
            {
                if (!isTranscriptStreaming || transcriptStreamingTextBlock == null)
                {
                    transcriptTypingTimer?.Stop();
                    return;
                }
                if (transcriptStreamingBuffer.Length == 0)
                {
                    dots = (dots + 1) % 4;
                    transcriptStreamingTextBlock.Text = new string('.', Math.Max(1, dots));
                }
            };
            transcriptTypingTimer.Start();
        }

        private void StopTranscriptTypingIndicator()
        {
            transcriptTypingTimer?.Stop();
            transcriptTypingTimer = null;
        }

        public void MarkNextAsNewSegment()
        {
            lastBubbleSource = null;
            lastUserBubble = lastSystemBubble = null;
            lastUserText = lastSystemText = null;
            lastUserPrefix = lastSystemPrefix = "";
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
                lastUserMessageId = lastSystemMessageId = null;
                lastUserPrefix = lastSystemPrefix = "";
                lastBubbleSource = null;
                lastUserActivity = default;
                lastSystemActivity = default;

                isTranscriptStreaming = false;
                StopTranscriptTypingIndicator();
                transcriptStreamingBuffer.Clear();
                transcriptStreamingTextBlock = null;
                transcriptStreamingBubble = null;

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

        private void AddTranscriptBubble(string text, MessageSource source, bool isNewSegment, string? messageId = null)
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
                string? lastId = isUser ? lastUserMessageId : lastSystemMessageId;
                string prefix = isUser ? lastUserPrefix : lastSystemPrefix;
                string trimmed = text.Trim();

                static string Combine(string p, string t) =>
                    string.IsNullOrEmpty(p) ? t : (p + " " + t).Trim();

                // Same backend message updating its transcript: the event carries the
                // FULL utterance text, so REPLACE this message's portion (appending would
                // duplicate every word/sentence already shown), keeping any merged prefix.
                bool sameMessage = !string.IsNullOrEmpty(messageId)
                                   && messageId == lastId
                                   && lastText != null;

                if (sameMessage)
                {
                    lastText!.Text = Combine(prefix, trimmed);
                }
                else
                {
                    bool flipped = lastBubbleSource.HasValue && lastBubbleSource.Value != source;
                    // A new utterance starts a fresh bubble only when the speaker changed or
                    // there was more than GapSecondsForNewBubble of silence. Otherwise we MERGE
                    // it into the current bubble so brief (~1s) pauses don't fragment speech.
                    bool gapped = lastActivity != default
                                  && (now - lastActivity).TotalSeconds > GapSecondsForNewBubble;

                    // A DIFFERENT backend message (the id changed) is a distinct, already
                    // finalized utterance — give it its own bubble instead of merging it
                    // into the previous one. Merging distinct messages via the prefix path
                    // was a source of duplicated text; keeping messages separate is both
                    // clearer and duplicate-proof. The legacy no-id path still merges by gap.
                    bool differentMessage = !string.IsNullOrEmpty(messageId)
                                            && !string.IsNullOrEmpty(lastId)
                                            && messageId != lastId;

                    bool startNew = flipped || gapped || differentMessage || isNewSegment
                                    || lastBubble == null || lastText == null;

                    if (startNew)
                    {
                        var (row, border, textBlock) = CreateChatRow(trimmed, source);
                        TranscriptionPanel.Children.Add(row);
                        if (isUser)
                        {
                            lastUserBubble = border; lastUserText = textBlock;
                            lastUserMessageId = messageId; lastUserPrefix = "";
                        }
                        else
                        {
                            lastSystemBubble = border; lastSystemText = textBlock;
                            lastSystemMessageId = messageId; lastSystemPrefix = "";
                        }
                    }
                    else
                    {
                        // Merge: promote the text already shown to the prefix, then append
                        // this new utterance after it within the SAME bubble.
                        string newPrefix = lastText!.Text;
                        lastText.Text = Combine(newPrefix, trimmed);
                        if (isUser) { lastUserPrefix = newPrefix; lastUserMessageId = messageId; }
                        else        { lastSystemPrefix = newPrefix; lastSystemMessageId = messageId; }
                    }
                }

                if (isUser) lastUserActivity = now; else lastSystemActivity = now;
                lastBubbleSource = source;
                MaybeAutoScrollTranscript();
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

        /// <summary>
        /// Forwards wheel events to the outer <see cref="ScrollViewer"/> so that inner scroll-aware
        /// controls (e.g. Markdig's <c>MarkdownViewer</c> / <c>FlowDocumentScrollViewer</c>) can't
        /// swallow the wheel event and stall the conversation list from scrolling.
        /// </summary>
        private void OuterScrollViewer_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (sender is not ScrollViewer sv) return;
            if (e.Delta == 0) return;

            // WPF's default wheel step is 48 DIPs per notch (≈120 delta units); match it.
            double step = -e.Delta / 120.0 * 48.0;
            double target = Math.Max(0, Math.Min(sv.ScrollableHeight, sv.VerticalOffset + step));
            sv.ScrollToVerticalOffset(target);
            e.Handled = true;
        }

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
