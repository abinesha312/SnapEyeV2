using System;
using System.Collections.Generic;
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
        // Live markdown viewer for the stable (complete) prefix while streaming; incomplete
        // trailing tables / fences / diagrams stay in currentStreamingPending as plain text.
        private MarkdownViewer? currentStreamingViewer;
        private TextBlock? currentStreamingPending;
        private string lastStreamedStableMarkdown = "";
        private string lastFinalAnswer = "";

        // Coalesces token updates so Markdig re-parses at most ~25 Hz, and only when the
        // stable markdown prefix grows (pending-only growth updates the TextBlock alone).
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
        private bool chatHasPlaceholder = true;
        private double bubbleMaxWidth = 360;
        private double chatBubbleMaxWidth = 420;

        // Chat sticky-bottom
        private bool chatStickyBottom = true;
        // Transcript view auto-follow. Live transcription follows the newest line only while
        // the user is already at the bottom; once they scroll up they stay put.
        private bool transcriptStickyBottom = true;

        // Force-new-bubble after this much silence on the same source. Below this gap,
        // consecutive same-speaker utterances are merged into the same bubble so a brief
        // (~1s) pause no longer fragments speech into many bubbles.
        private const double GapSecondsForNewBubble = 3.0;

        // Theme colors (slate/sky palette). Text contrast on each fill is >= 4.5:1.
        // You: blue fill. The brighter #3B82F6 is the border - as a fill it would only give
        // white text a 3.7:1 contrast.
        private static readonly Color UserBubbleColor   = (Color)ColorConverter.ConvertFromString("#2563EB");
        private static readonly Color UserBorderColor   = (Color)ColorConverter.ConvertFromString("#3B82F6");
        private static readonly Color UserTextColor     = (Color)ColorConverter.ConvertFromString("#FFFFFF");
        // Other speaker (system audio)
        private static readonly Color SystemBubbleColor = (Color)ColorConverter.ConvertFromString("#1E293B");
        private static readonly Color SystemBorderColor = (Color)ColorConverter.ConvertFromString("#334155");
        private static readonly Color SystemTextColor   = (Color)ColorConverter.ConvertFromString("#E2E8F0");
        // AI answer bubble
        private static readonly Color AIBubbleColor     = (Color)ColorConverter.ConvertFromString("#0F172A");
        private static readonly Color AIBorderColor     = (Color)ColorConverter.ConvertFromString("#334155");
        private static readonly Color BubbleTextColor   = (Color)ColorConverter.ConvertFromString("#F8FAFC");

        // AI answer card (chat view and AI transcript bubbles)
        private static readonly Color AnswerCardFill   = (Color)ColorConverter.ConvertFromString("#0F172A");
        private static readonly Color AnswerCardBorder = (Color)ColorConverter.ConvertFromString("#1E293B");
        private static readonly Color AnswerAccent     = (Color)ColorConverter.ConvertFromString("#38BDF8");

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
            // Capture chat and live-voice answers share one session panel (ChatView).
            ShowUnifiedSessionView();
        }

        public void SwitchToTranscriptionTab()
        {
            // Same panel as chat — switching Listen/Transcript must not open an empty
            // second history. Speech bubbles and AI answers append to this session.
            ShowUnifiedSessionView();
        }

        private void ShowUnifiedSessionView()
        {
            SafeInvoke(() =>
            {
                ChatViewRoot.Visibility = Visibility.Visible;
                TranscriptionViewRoot.Visibility = Visibility.Collapsed;
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
                currentStreamingViewer = null;
                currentStreamingPending = null;
                lastStreamedStableMarkdown = "";
                lastFinalAnswer = "";

                // Speech bubbles are on this same panel — drop merge pointers.
                lastUserBubble = null; lastUserText = null;
                lastSystemBubble = null; lastSystemText = null;
                lastUserMessageId = lastSystemMessageId = null;
                lastUserPrefix = lastSystemPrefix = "";
                lastBubbleSource = null;
                lastUserActivity = default;
                lastSystemActivity = default;

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

        /// <summary>
        /// Rebuild the shared session panel from a persisted conversation so reopening
        /// the overlay shows the same thread (typed, spoken, and assistant answers).
        /// </summary>
        public void ReplayConversation(IReadOnlyList<ConversationMessage> messages)
        {
            SafeInvoke(() =>
            {
                ClearContent();
                if (messages == null || messages.Count == 0) return;

                ShowUnifiedSessionView();
                RemoveChatPlaceholder();

                string lastAnswer = "";
                foreach (var m in messages)
                {
                    if (string.IsNullOrWhiteSpace(m.Text)) continue;

                    switch (m.Kind)
                    {
                        case ConversationMessageKind.UserTyped:
                            ChatHistoryPanel.Children.Add(BuildUserPromptRow(m.Text.Trim()));
                            break;

                        case ConversationMessageKind.QuickAction:
                            string prompt = !string.IsNullOrWhiteSpace(m.Label) ? m.Label!.Trim() : m.Text.Trim();
                            ChatHistoryPanel.Children.Add(BuildUserPromptRow(prompt));
                            break;

                        case ConversationMessageKind.UserSpoken:
                        {
                            var (row, border, textBlock) = CreateChatRow(m.Text.Trim(), MessageSource.Microphone);
                            ChatHistoryPanel.Children.Add(row);
                            lastUserBubble = border;
                            lastUserText = textBlock;
                            lastUserMessageId = null;
                            lastUserPrefix = "";
                            lastBubbleSource = MessageSource.Microphone;
                            break;
                        }

                        case ConversationMessageKind.OtherSpoken:
                        {
                            var (row, border, textBlock) = CreateChatRow(m.Text.Trim(), MessageSource.Speaker);
                            ChatHistoryPanel.Children.Add(row);
                            lastSystemBubble = border;
                            lastSystemText = textBlock;
                            lastSystemMessageId = null;
                            lastSystemPrefix = "";
                            lastBubbleSource = MessageSource.Speaker;
                            break;
                        }

                        case ConversationMessageKind.AssistantAnswer:
                            ChatHistoryPanel.Children.Add(BuildAnswerCard(m.Text.Trim()));
                            lastAnswer = m.Text.Trim();
                            // Break speech-merge chain after an AI answer.
                            lastBubbleSource = null;
                            lastUserBubble = lastSystemBubble = null;
                            lastUserText = lastSystemText = null;
                            lastUserPrefix = lastSystemPrefix = "";
                            break;
                    }
                }

                lastFinalAnswer = lastAnswer;
                chatStickyBottom = true;
                ChatView?.ScrollToEnd();
                HideJumpToLatest();
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
                    currentStreamingViewer = null;
                    currentStreamingPending = null;
                    lastStreamedStableMarkdown = "";
                }

                isStreamingResponse = true;
                streamingBuffer.Clear();
                lastStreamedStableMarkdown = "";
                RemoveChatPlaceholder();

                if (!string.IsNullOrWhiteSpace(question))
                    ChatHistoryPanel.Children.Add(BuildUserPromptRow(question.Trim()));

                currentStreamingCard = BuildStreamingCard(out currentStreamingViewer, out currentStreamingPending);
                ChatHistoryPanel.Children.Add(currentStreamingCard);

                StartStreamFlushTimer();
                MaybeAutoScroll();
            });
        }

        /// <summary>
        /// Called on the UI dispatcher for every streamed token. Appends to the buffer and
        /// sets a dirty flag; live markdown flush is throttled (see <see cref="StreamFlushInterval"/>).
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

        /// <summary>
        /// Answer card with live markdown (stable prefix) + plain pending suffix for incomplete
        /// tables / fences / diagrams.
        /// </summary>
        private Border BuildStreamingCard(out MarkdownViewer viewer, out TextBlock pending)
        {
            viewer = new SnapEyeMarkdownViewer
            {
                Background = Brushes.Transparent,
                Foreground = new SolidColorBrush(BubbleTextColor),
                Pipeline = MarkdownRendering.Pipeline,
            };
            ApplyMarkdownTheme(viewer);
            viewer.Markdown = "";

            pending = new TextBlock
            {
                Text = "…",
                Foreground = new SolidColorBrush(BubbleTextColor),
                FontSize = 13,
                LineHeight = 18,
                TextWrapping = TextWrapping.Wrap,
                FontFamily = new FontFamily("Cascadia Code, Consolas, Courier New"),
                Visibility = Visibility.Visible,
            };

            var body = new StackPanel();
            body.Children.Add(viewer);
            body.Children.Add(pending);

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
            Grid.SetColumn(body, 1);
            grid.Children.Add(accent);
            grid.Children.Add(body);

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

                // Swap the live split body for a fully-rendered markdown view so
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
                else if (currentStreamingPending != null)
                    currentStreamingPending.Text = finalText;

                lastFinalAnswer = finalText;
                streamingBuffer.Clear();
                currentStreamingCard = null;
                currentStreamingViewer = null;
                currentStreamingPending = null;
                lastStreamedStableMarkdown = "";

                MaybeAutoScroll();
            });
        }

        public void ShowStreamingError(string error)
        {
            SafeInvoke(() =>
            {
                isStreamingResponse = false;
                StopStreamFlushTimer();
                if (currentStreamingPending != null)
                {
                    currentStreamingPending.Text = $"⚠ Error: {error}";
                    currentStreamingPending.Visibility = Visibility.Visible;
                }
                if (currentStreamingViewer != null)
                    currentStreamingViewer.Markdown = "";
                streamingBuffer.Clear();
                currentStreamingCard = null;
                currentStreamingViewer = null;
                currentStreamingPending = null;
                lastStreamedStableMarkdown = "";
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

        /// <summary>
        /// Pushes the latest streaming buffer into the live markdown viewer (stable prefix)
        /// and pending plain TextBlock, at most once per tick.
        /// </summary>
        private void FlushStreamingBuffer()
        {
            if (!streamDirty || !isStreamingResponse) return;
            if (currentStreamingViewer == null && currentStreamingPending == null) return;
            streamDirty = false;

            string text = streamingBuffer.ToString();
            if (text.Length == 0) return;
            try
            {
                ApplyLiveMarkdownSplit(
                    text,
                    currentStreamingViewer,
                    currentStreamingPending,
                    ref lastStreamedStableMarkdown);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[SolutionRegion] stream flush error: {ex.Message}");
            }
            // No auto-scroll during generation: the answer stays anchored where it began so
            // the user can read and scroll freely. The "New" pill lets them jump to the latest.
        }

        /// <summary>
        /// Renders complete markdown live; holds incomplete trailing tables / fences / diagrams
        /// as monospace plain text until those blocks close.
        /// </summary>
        private void ApplyLiveMarkdownSplit(
            string fullText,
            MarkdownViewer? viewer,
            TextBlock? pending,
            ref string lastStable)
        {
            var split = StreamingMarkdownSplit.Split(fullText);

            if (viewer != null && !string.Equals(split.StableMarkdown, lastStable, StringComparison.Ordinal))
            {
                lastStable = split.StableMarkdown;
                viewer.Markdown = string.IsNullOrEmpty(split.StableMarkdown)
                    ? ""
                    : MarkdownRendering.Prepare(split.StableMarkdown);
            }

            if (pending != null)
            {
                if (string.IsNullOrEmpty(split.PendingPlain))
                {
                    // Keep a caret-like ellipsis only before any content arrives.
                    if (string.IsNullOrEmpty(split.StableMarkdown) && fullText.Length == 0)
                    {
                        pending.Text = "…";
                        pending.Visibility = Visibility.Visible;
                    }
                    else
                    {
                        pending.Text = "";
                        pending.Visibility = Visibility.Collapsed;
                    }
                }
                else
                {
                    pending.Text = split.PendingPlain;
                    pending.Visibility = Visibility.Visible;
                }
            }
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
            viewer = new SnapEyeMarkdownViewer
            {
                Background = Brushes.Transparent,
                Foreground = new SolidColorBrush(BubbleTextColor),
                Pipeline = MarkdownRendering.Pipeline,
            };
            ApplyMarkdownTheme(viewer);
            viewer.Markdown = MarkdownRendering.Prepare(markdown);

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
        /// high-confidence answers render green, medium amber, low red (12% fill / 30% border).
        /// </summary>
        private static Border BuildConfidenceBadge(double confidence)
        {
            int pct = (int)Math.Round(Math.Max(0.0, Math.Min(1.0, confidence)) * 100.0);
            Color fill, edge, text;
            if (confidence >= 0.85)
            {
                fill = Color.FromArgb(0x1F, 0x10, 0xB9, 0x81); edge = Color.FromArgb(0x4D, 0x10, 0xB9, 0x81); text = Color.FromRgb(0x34, 0xD3, 0x99);
            }
            else if (confidence >= 0.7)
            {
                fill = Color.FromArgb(0x1F, 0xF5, 0x9E, 0x0B); edge = Color.FromArgb(0x4D, 0xF5, 0x9E, 0x0B); text = Color.FromRgb(0xFB, 0xBF, 0x24);
            }
            else
            {
                fill = Color.FromArgb(0x1F, 0xEF, 0x44, 0x44); edge = Color.FromArgb(0x4D, 0xEF, 0x44, 0x44); text = Color.FromRgb(0xF8, 0x71, 0x71);
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
        private static readonly Color CodeBg       = (Color)ColorConverter.ConvertFromString("#030712");
        private static readonly Color CodeFg       = (Color)ColorConverter.ConvertFromString("#E5E7EB");
        private static readonly Color CodeBorder   = (Color)ColorConverter.ConvertFromString("#1E293B");
        private static readonly Color InlineCodeBg = (Color)ColorConverter.ConvertFromString("#1E293B");
        private static readonly Color InlineCodeFg = (Color)ColorConverter.ConvertFromString("#38BDF8");
        private static readonly FontFamily MonoFont = new("Cascadia Code, Consolas, Courier New");

        private static Style? _codeBlockStyle;
        private static Style? _inlineCodeStyle;
        private static Style? _tableStyle;
        private static Style? _tableCellStyle;
        private static Style? _tableHeaderStyle;
        private static Style? _heading2Style;

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
                    s.Setters.Add(new Setter(Block.BorderBrushProperty, new SolidColorBrush(CodeBorder)));
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
                if (_tableStyle == null)
                {
                    var s = new Style(typeof(Table));
                    s.Setters.Add(new Setter(Block.BorderBrushProperty, Brushes.White));
                    s.Setters.Add(new Setter(Block.BorderThicknessProperty, new Thickness(1)));
                    s.Setters.Add(new Setter(Table.CellSpacingProperty, 0.0));
                    s.Setters.Add(new Setter(TextElement.ForegroundProperty, Brushes.White));
                    s.Seal();
                    _tableStyle = s;
                }
                if (_tableCellStyle == null)
                {
                    var s = new Style(typeof(TableCell));
                    s.Setters.Add(new Setter(Block.BorderBrushProperty, Brushes.White));
                    s.Setters.Add(new Setter(Block.BorderThicknessProperty, new Thickness(1)));
                    s.Setters.Add(new Setter(TableCell.PaddingProperty, new Thickness(8, 6, 8, 6)));
                    s.Setters.Add(new Setter(TextElement.ForegroundProperty, new SolidColorBrush(Color.FromRgb(0xE5, 0xE7, 0xEB))));
                    s.Seal();
                    _tableCellStyle = s;
                }
                if (_tableHeaderStyle == null)
                {
                    var s = new Style(typeof(TableRow));
                    s.Setters.Add(new Setter(TextElement.FontWeightProperty, FontWeights.SemiBold));
                    s.Setters.Add(new Setter(TextElement.ForegroundProperty, Brushes.White));
                    s.Setters.Add(new Setter(TextElement.BackgroundProperty, new SolidColorBrush(Color.FromRgb(0x1E, 0x29, 0x3B))));
                    s.Seal();
                    _tableHeaderStyle = s;
                }
                if (_heading2Style == null)
                {
                    var s = new Style(typeof(Paragraph));
                    s.Setters.Add(new Setter(TextElement.FontSizeProperty, 18.0));
                    s.Setters.Add(new Setter(TextElement.FontWeightProperty, FontWeights.SemiBold));
                    s.Setters.Add(new Setter(TextElement.ForegroundProperty, Brushes.White));
                    s.Setters.Add(new Setter(Block.MarginProperty, new Thickness(0, 10, 0, 6)));
                    s.Seal();
                    _heading2Style = s;
                }

                viewer.Resources[Styles.CodeBlockStyleKey] = _codeBlockStyle;
                viewer.Resources[Styles.CodeStyleKey] = _inlineCodeStyle;
                viewer.Resources[Styles.TableStyleKey] = _tableStyle;
                viewer.Resources[Styles.TableCellStyleKey] = _tableCellStyle;
                viewer.Resources[Styles.TableHeaderStyleKey] = _tableHeaderStyle;
                viewer.Resources[Styles.Heading2StyleKey] = _heading2Style;
                viewer.Resources[Styles.Heading1StyleKey] = _heading2Style;
                viewer.Resources[Styles.Heading3StyleKey] = _heading2Style;
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
                Background = new SolidColorBrush(Color.FromArgb(0x26, 0xEF, 0x44, 0x44)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(0xEF, 0x44, 0x44)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(12),
                Padding = new Thickness(12, 10, 12, 10),
                Margin = new Thickness(0, 4, 28, 8),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Child = new TextBlock
                {
                    Text = "⚠ " + error,
                    Foreground = new SolidColorBrush(Color.FromRgb(0xFC, 0xA5, 0xA5)),
                    FontSize = 13,
                    TextWrapping = TextWrapping.Wrap,
                    FontFamily = new FontFamily("Segoe UI")
                }
            };
        }

        // Spoken "You" (Y) bubbles stay compact: 3 visible lines, scroll for the rest.
        private const double YouBubbleLineHeight = 18;
        private const int YouBubbleMaxVisibleLines = 3;

        private static FrameworkElement WrapYouBubbleText(TextBlock textBlock)
        {
            return new ScrollViewer
            {
                MaxHeight = YouBubbleLineHeight * YouBubbleMaxVisibleLines,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Focusable = false,
                Padding = new Thickness(0),
                Content = textBlock,
            };
        }

        private Grid BuildUserPromptRow(string text)
        {
            var textBlock = new TextBlock
            {
                Text = text,
                Foreground = new SolidColorBrush(UserTextColor),
                FontSize = 13,
                LineHeight = YouBubbleLineHeight,
                TextWrapping = TextWrapping.Wrap,
                FontFamily = new FontFamily("Segoe UI")
            };

            var bubble = new Border
            {
                Background = new SolidColorBrush(UserBubbleColor),
                BorderBrush = new SolidColorBrush(UserBorderColor),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(14),
                Padding = new Thickness(12, 8, 12, 8),
                MaxWidth = chatBubbleMaxWidth,
                HorizontalAlignment = HorizontalAlignment.Right,
                Child = WrapYouBubbleText(textBlock),
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

            // New content arrived — show jump pill when the user is not already following.
            if (e.ExtentHeightChange > 0.5)
            {
                if (!transcriptStickyBottom && !atBottom)
                    ShowTranscriptJump();
                else if (atBottom)
                    HideTranscriptJump();
            }
        }

        /// <summary>
        /// Follows the newest transcript content only while the user is at the bottom.
        /// </summary>
        private void MaybeAutoScrollTranscript()
        {
            if (TranscriptionView == null) return;
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
        /// Cancels/finalizes any in-progress streaming answer without an error banner.
        /// Called when the user hits Stop. Shows a subtle "Canceled." note when nothing
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

        private void RemovePlaceholder() => RemoveChatPlaceholder();

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
            // Unified session: AI answers always land on the shared chat panel.
            SafeInvoke(() =>
            {
                if (ChatHistoryPanel == null || string.IsNullOrWhiteSpace(text)) return;
                RemoveChatPlaceholder();
                ChatHistoryPanel.Children.Add(BuildAnswerCard(text.Trim()));

                lastBubbleSource = null;
                lastUserBubble = lastSystemBubble = null;
                lastUserText = lastSystemText = null;
                lastUserPrefix = lastSystemPrefix = "";

                MaybeAutoScroll();
            });
        }

        /// <summary>Adds a user prompt bubble to the shared session (same as chat).</summary>
        public void AddUserPromptToTranscript(string text) => AddUserPromptBubble(text);

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

        /// <summary>Adds a screen capture OCR entry to the shared session (same as chat).</summary>
        public void AddScreenCaptureToTranscript(string ocrText) => AddScreenCaptureToChat(ocrText);

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
                Background = new SolidColorBrush(Color.FromArgb(0x26, 0x3B, 0x82, 0xF6)),
                BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#3B82F6")),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(10, 8, 10, 8),
                Margin = new Thickness(4, 6, 4, 6),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Child = stack,
            };
        }

        #endregion

        #region Transcript Display Helpers

        /// <summary>Live-voice AI answers use the same streaming card as capture/chat.</summary>
        public void BeginStreamingAnswerInTranscript(string? question) => BeginStreamingResponse(question);

        public void AppendStreamingAnswerToken(string token) => AppendStreamingToken(token);

        public void EndStreamingAnswerInTranscript(string? fullResponse = null, double? confidence = null)
            => EndStreamingResponse(fullResponse, confidence);

        public void ShowStreamingAnswerError(string error) => ShowStreamingError(error);

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
            // Speech bubbles live on the shared ChatHistoryPanel. End Session clears that
            // via ClearContent(); here we only reset live-merge state so a new listen
            // doesn't append into stale You/O bubbles.
            SafeInvoke(() =>
            {
                lastUserBubble = null; lastUserText = null;
                lastSystemBubble = null; lastSystemText = null;
                lastUserMessageId = lastSystemMessageId = null;
                lastUserPrefix = lastSystemPrefix = "";
                lastBubbleSource = null;
                lastUserActivity = default;
                lastSystemActivity = default;
            });
        }

        /// <summary>Clears the shared session thread (used on End Session / new meeting).</summary>
        public void ClearAll()
        {
            ClearContent();
            ClearTranscription();
        }

        private void AddTranscriptBubble(string text, MessageSource source, bool isNewSegment, string? messageId = null)
        {
            SafeInvoke(() =>
            {
                if (ChatHistoryPanel == null || string.IsNullOrWhiteSpace(text)) return;
                RemoveChatPlaceholder();

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
                        ChatHistoryPanel.Children.Add(row);
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
                MaybeAutoScroll();
            });
        }

        private (Grid row, Border bubble, TextBlock textBlock) CreateChatRow(string text, MessageSource? source)
        {
            bool isAI   = !source.HasValue;
            bool isUser = source == MessageSource.Microphone;

            Color fill, edge, textColor;
            string letter;
            HorizontalAlignment align;

            if (isAI)        { fill = AIBubbleColor;     edge = AIBorderColor;     textColor = BubbleTextColor; letter = "A"; align = HorizontalAlignment.Left;  }
            else if (isUser) { fill = UserBubbleColor;   edge = UserBorderColor;   textColor = UserTextColor;   letter = "Y"; align = HorizontalAlignment.Right; }
            else             { fill = SystemBubbleColor; edge = SystemBorderColor; textColor = SystemTextColor; letter = "O"; align = HorizontalAlignment.Left;  }

            // The AI avatar gets the sky accent so it stays distinct from the other speaker's
            // (similarly dark) avatar.
            Color avatarEdge = isAI ? AnswerAccent : edge;

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
                BorderBrush = new SolidColorBrush(avatarEdge),
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
                Foreground = new SolidColorBrush(textColor),
                FontSize = 13,
                LineHeight = isUser ? YouBubbleLineHeight : 18,
                TextWrapping = TextWrapping.Wrap,
                FontFamily = new FontFamily("Segoe UI"),
            };

            // "You" (mic) bubbles: fixed ~3-line viewport with internal scroll.
            bubble.Child = isUser ? WrapYouBubbleText(textBlock) : textBlock;

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
        /// Shift+wheel over a code-block H-scroller pans that block horizontally; plain wheel
        /// always scrolls the conversation vertically so wide code blocks don't trap the cursor.
        /// (Trackpad sideways swipe is handled by <see cref="Services.WheelCaptureHook"/> via
        /// <see cref="TryScrollCodeBlockAt"/> — WPF has no MouseHWheel routed event.)
        /// </summary>
        private void OuterScrollViewer_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (sender is not ScrollViewer sv) return;
            if (e.Delta == 0) return;

            double step = -e.Delta / 120.0 * 48.0;

            bool shift = (Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift;
            if (shift)
            {
                var hScroll = FindInlineHScrollViewer(e.OriginalSource as DependencyObject);
                if (hScroll != null && hScroll.ScrollableWidth > 0)
                {
                    hScroll.ScrollToHorizontalOffset(
                        Math.Max(0, Math.Min(hScroll.ScrollableWidth, hScroll.HorizontalOffset + step)));
                    e.Handled = true;
                    return;
                }
            }

            // WPF's default wheel step is 48 DIPs per notch (≈120 delta units); match it.
            double target = Math.Max(0, Math.Min(sv.ScrollableHeight, sv.VerticalOffset + step));
            sv.ScrollToVerticalOffset(target);
            e.Handled = true;
        }

        private static ScrollViewer? FindTaggedScrollViewer(DependencyObject? start, string tag)
        {
            for (DependencyObject? d = start; d != null; d = GetParentSafe(d))
            {
                if (d is ScrollViewer sc && Equals(sc.Tag, tag))
                    return sc;
            }
            return null;
        }

        private static ScrollViewer? FindInlineHScrollViewer(DependencyObject? start)
        {
            return FindTaggedScrollViewer(start, NonWrappingCodeBlockRenderer.CodeBlockScrollTag)
                   ?? FindTaggedScrollViewer(start, TableColumnFitter.TableScrollTag);
        }

        private static DependencyObject? GetParentSafe(DependencyObject current)
        {
            try
            {
                if (current is Visual or System.Windows.Media.Media3D.Visual3D)
                    return VisualTreeHelper.GetParent(current);
                if (current is FrameworkContentElement content)
                    return content.Parent ?? content.TemplatedParent;
                return LogicalTreeHelper.GetParent(current);
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Scrolls whichever view is showing (chat or transcript) by <paramref name="wheelDelta"/>
        /// (120 per notch, positive = up). Used when the overlay is click-through, where
        /// wheel events never reach WPF and have to be forwarded by a global hook.
        /// </summary>
        public void ScrollActiveView(int wheelDelta)
        {
            var sv = TranscriptionViewRoot.Visibility == Visibility.Visible ? TranscriptionView : ChatView;
            double step = -wheelDelta / 120.0 * 48.0;
            sv.ScrollToVerticalOffset(Math.Max(0, Math.Min(sv.ScrollableHeight, sv.VerticalOffset + step)));
        }

        /// <summary>
        /// If the screen point is over a wide fenced-code <see cref="ScrollViewer"/>, pan it
        /// horizontally by <paramref name="wheelDelta"/> (120 per notch). Used for trackpad
        /// <c>WM_MOUSEHWHEEL</c> and Shift+wheel from the global wheel hook.
        /// </summary>
        public bool TryScrollCodeBlockAt(int screenX, int screenY, int wheelDelta)
        {
            try
            {
                Point local = PointFromScreen(new Point(screenX, screenY));
                if (local.X < 0 || local.Y < 0 || local.X > ActualWidth || local.Y > ActualHeight)
                    return false;

                var hit = VisualTreeHelper.HitTest(this, local);
                var hScroll = FindInlineHScrollViewer(hit?.VisualHit);
                if (hScroll == null || hScroll.ScrollableWidth <= 0)
                    return false;

                double step = -wheelDelta / 120.0 * 48.0;
                hScroll.ScrollToHorizontalOffset(
                    Math.Max(0, Math.Min(hScroll.ScrollableWidth, hScroll.HorizontalOffset + step)));
                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[SolutionRegion] Inline H-scroll error: {ex.Message}");
                return false;
            }
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
