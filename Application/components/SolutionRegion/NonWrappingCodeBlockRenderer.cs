using System;
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

        private static readonly Brush Bg = new SolidColorBrush(
            (Color)ColorConverter.ConvertFromString("#030712"));
        private static readonly Brush Fg = new SolidColorBrush(
            (Color)ColorConverter.ConvertFromString("#E5E7EB"));
        private static readonly Brush Border = new SolidColorBrush(
            (Color)ColorConverter.ConvertFromString("#1E293B"));
        private static readonly FontFamily Mono = new("Cascadia Code, Consolas, Courier New");

        static NonWrappingCodeBlockRenderer()
        {
            Bg.Freeze();
            Fg.Freeze();
            Border.Freeze();
        }

        protected override void Write(WpfRenderer renderer, CodeBlock obj)
        {
            if (renderer == null) throw new ArgumentNullException(nameof(renderer));

            var text = new TextBlock
            {
                Text = ExtractRawText(obj),
                FontFamily = Mono,
                FontSize = 12.5,
                LineHeight = 17,
                Foreground = Fg,
                Background = Brushes.Transparent,
                TextWrapping = TextWrapping.NoWrap,
                TextAlignment = TextAlignment.Left,
            };

            // Pin intrinsic width so ScrollViewer gets a real ScrollableWidth inside
            // BlockUIContainer (otherwise content measures to the viewport and H-scroll dies).
            text.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            double contentWidth = Math.Ceiling(text.DesiredSize.Width);
            if (contentWidth > 0)
            {
                text.Width = contentWidth;
                text.MinWidth = contentWidth;
            }

            var scroll = new ScrollViewer
            {
                Tag = CodeBlockScrollTag,
                Content = text,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
                // Trackpad / touch: allow horizontal pan on the code surface itself.
                PanningMode = PanningMode.HorizontalOnly,
                Focusable = false,
                CanContentScroll = false,
            };

            var host = new Border
            {
                Background = Bg,
                BorderBrush = Border,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(10, 8, 10, 8),
                Margin = new Thickness(0, 4, 0, 6),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                ClipToBounds = true,
                Child = scroll,
            };

            // BlockUIContainer often measures children at infinite width, so the ScrollViewer
            // grows to fit the TextBlock and ScrollableWidth stays 0. Constrain the scroller
            // to the host's arranged width whenever layout settles.
            host.Loaded += (_, _) => ConstrainCodeScrollWidth(host, scroll);
            host.SizeChanged += (_, _) => ConstrainCodeScrollWidth(host, scroll);

            renderer.WriteBlock(new BlockUIContainer(host));
        }

        private static void ConstrainCodeScrollWidth(Border host, ScrollViewer scroll)
        {
            try
            {
                double inner = host.ActualWidth - host.Padding.Left - host.Padding.Right
                               - host.BorderThickness.Left - host.BorderThickness.Right;
                if (inner <= 1) return;
                // Cap the viewport; content TextBlock keeps its pinned wider Width.
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
