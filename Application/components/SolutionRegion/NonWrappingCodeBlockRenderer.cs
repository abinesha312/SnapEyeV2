using System;
using System.Globalization;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using Markdig.Renderers;
using Markdig.Renderers.Wpf;
using Markdig.Syntax;

namespace SnapEye.SolutionRegion
{
    /// <summary>
    /// Renders fenced/indented code as a non-wrapping monospace block with horizontal
    /// scroll. Markdig.Wpf's default <see cref="CodeBlockRenderer"/> uses a FlowDocument
    /// <see cref="Paragraph"/>, and FlowDocument always wraps — which shreds ASCII
    /// system-design diagrams (boxes, arrows, alignment).
    /// </summary>
    internal sealed class NonWrappingCodeBlockRenderer : WpfObjectRenderer<CodeBlock>
    {
        internal const string CodeBlockScrollTag = "SnapEyeCodeBlockHScroll";

        private const double FontSizePx = 12.5;
        private const double LineHeightPx = 17.0;

        private static readonly Brush Bg = new SolidColorBrush(
            (Color)ColorConverter.ConvertFromString("#030712"));
        private static readonly Brush Fg = new SolidColorBrush(
            (Color)ColorConverter.ConvertFromString("#E5E7EB"));
        private static readonly Brush BorderBrush = new SolidColorBrush(
            (Color)ColorConverter.ConvertFromString("#1E293B"));
        private static readonly FontFamily Mono = new("Cascadia Code, Consolas, Courier New");
        private static readonly Typeface MonoTypeface = new(
            Mono, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);

        static NonWrappingCodeBlockRenderer()
        {
            Bg.Freeze();
            Fg.Freeze();
            BorderBrush.Freeze();
        }

        protected override void Write(WpfRenderer renderer, CodeBlock obj)
        {
            if (renderer == null) throw new ArgumentNullException(nameof(renderer));

            string raw = ExtractRawText(obj);
            var size = MeasureMonoSize(raw);

            var text = new TextBlock
            {
                Text = raw,
                FontFamily = Mono,
                FontSize = FontSizePx,
                LineHeight = LineHeightPx,
                Foreground = Fg,
                Background = Brushes.Transparent,
                TextWrapping = TextWrapping.NoWrap,
                TextAlignment = TextAlignment.Left,
                Width = size.Width,
                MinWidth = size.Width,
                Height = size.Height,
                MinHeight = size.Height,
            };

            var scroll = new ScrollViewer
            {
                Tag = CodeBlockScrollTag,
                Content = text,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
                // HorizontalOnly panning steals trackpad vertical swipes from the chat list.
                PanningMode = PanningMode.None,
                Focusable = false,
                CanContentScroll = false,
            };

            var host = new Border
            {
                Background = Bg,
                BorderBrush = BorderBrush,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(10, 8, 10, 8),
                Margin = new Thickness(0, 4, 0, 6),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                ClipToBounds = true,
                Child = scroll,
            };

            // Remeasure after DPI is known, then grow the ScrollViewer so a wide diagram's
            // H-scrollbar doesn't steal height and clip the bottom half of the ASCII art.
            host.Loaded += (_, _) =>
            {
                RemeasureContent(text, raw);
                ConstrainCodeScrollWidth(host, scroll);
                FitScrollViewerToContent(host, scroll, text);
            };
            host.SizeChanged += (_, _) =>
            {
                ConstrainCodeScrollWidth(host, scroll);
                FitScrollViewerToContent(host, scroll, text);
            };

            renderer.WriteBlock(new BlockUIContainer(host));
        }

        private static void RemeasureContent(TextBlock text, string raw)
        {
            try
            {
                var size = MeasureMonoSize(raw, text);
                // Prefer layout DesiredSize once DPI/fonts are realized — FormattedText can
                // under-count line box height for Cascadia and clip tall UML diagrams.
                text.Width = size.Width;
                text.MinWidth = size.Width;
                text.Measure(new Size(size.Width, double.PositiveInfinity));
                double layoutH = Math.Ceiling(text.DesiredSize.Height) + 2;
                double h = Math.Max(size.Height, layoutH);
                text.Height = h;
                text.MinHeight = h;
            }
            catch
            {
                /* best effort */
            }
        }

        /// <summary>
        /// Wide ASCII diagrams show an H-scrollbar. With vertical scroll disabled, that bar
        /// overlays the last lines unless the ScrollViewer itself is taller than the TextBlock.
        /// </summary>
        private static void FitScrollViewerToContent(Border host, ScrollViewer scroll, TextBlock text)
        {
            try
            {
                double h = text.Height > 0 ? text.Height : text.MinHeight;
                if (h <= 0) return;

                double innerW = host.ActualWidth - host.Padding.Left - host.Padding.Right
                                - host.BorderThickness.Left - host.BorderThickness.Right;
                bool needsHScroll = innerW > 1 && text.Width > innerW + 1;
                if (needsHScroll
                    || scroll.ComputedHorizontalScrollBarVisibility == Visibility.Visible)
                {
                    h += SystemParameters.HorizontalScrollBarHeight + 2;
                }

                if (Math.Abs(scroll.Height - h) > 0.5)
                {
                    scroll.Height = h;
                    scroll.MinHeight = h;
                }
            }
            catch
            {
                /* best effort */
            }
        }

        private static Size MeasureMonoSize(string text, Visual? dpiVisual = null)
        {
            double dpi = 1.0;
            try
            {
                if (dpiVisual != null)
                    dpi = VisualTreeHelper.GetDpi(dpiVisual).PixelsPerDip;
                else if (Application.Current?.MainWindow != null)
                    dpi = VisualTreeHelper.GetDpi(Application.Current.MainWindow).PixelsPerDip;
            }
            catch
            {
                dpi = 1.0;
            }

            var lines = (text ?? "").Replace("\r\n", "\n").Split('\n');
            double maxW = 0;
            foreach (string line in lines)
            {
                // Preserve trailing spaces — they matter for ASCII box right borders.
                string sample = line.Length == 0 ? " " : line;
                var ft = new FormattedText(
                    sample,
                    CultureInfo.CurrentCulture,
                    FlowDirection.LeftToRight,
                    MonoTypeface,
                    FontSizePx,
                    Fg,
                    dpi);
                maxW = Math.Max(maxW, ft.WidthIncludingTrailingWhitespace);
            }

            // Small pad so the last column of '+' / '|' is never clipped by sub-pixel rounding.
            double width = Math.Ceiling(maxW) + 8;
            double height = Math.Ceiling(Math.Max(1, lines.Length) * LineHeightPx) + 4;
            return new Size(Math.Max(width, 40), Math.Max(height, LineHeightPx));
        }

        private static void ConstrainCodeScrollWidth(Border host, ScrollViewer scroll)
        {
            try
            {
                double inner = host.ActualWidth - host.Padding.Left - host.Padding.Right
                               - host.BorderThickness.Left - host.BorderThickness.Right;
                if (inner <= 1) return;
                if (Math.Abs(scroll.Width - inner) > 0.5)
                    scroll.Width = inner;
            }
            catch
            {
                /* best effort */
            }
        }

        private static string ExtractRawText(CodeBlock block)
        {
            var sb = new StringBuilder();
            if (block.Lines.Lines == null) return "";

            var lines = block.Lines;
            for (int i = 0; i < lines.Count; i++)
            {
                if (i > 0) sb.Append('\n');
                var slice = lines.Lines[i].Slice;
                if (slice.Length > 0)
                    sb.Append(slice.Text, slice.Start, slice.Length);
            }
            return sb.ToString();
        }
    }
}
