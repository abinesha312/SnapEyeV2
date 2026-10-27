using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using Markdig;
using Markdig.Renderers;
using Markdig.Renderers.Wpf;
using Markdig.Renderers.Wpf.Extensions;
using Markdig.Wpf;

namespace SnapEye.SolutionRegion
{
    /// <summary>
    /// MarkdownViewer with non-wrapping code blocks and horizontally scrollable tables.
    /// </summary>
    internal sealed class SnapEyeMarkdownViewer : MarkdownViewer
    {
        protected override void RefreshDocument()
        {
            if (Markdown == null)
            {
                Document = null;
                return;
            }

            var pipeline = Pipeline ?? new MarkdownPipelineBuilder().UseSupportedExtensions().Build();
            var doc = Markdig.Wpf.Markdown.ToFlowDocument(
                Markdown,
                pipeline,
                new SnapEyeWpfRenderer());
            doc.IsHyphenationEnabled = false;
            doc.PagePadding = new Thickness(0);
            // Remeasure columns and wrap every table in an H-scroll host.
            TableColumnFitter.Apply(doc);
            Document = doc;

            // Outer conversation ScrollViewer owns vertical scroll.
            if (Template?.FindName("PART_ContentHost", this) is not DependencyObject)
                Dispatcher.BeginInvoke(() => HardenInnerViewer(this), System.Windows.Threading.DispatcherPriority.Loaded);
            else
                HardenInnerViewer(this);
        }

        public override void OnApplyTemplate()
        {
            base.OnApplyTemplate();
            HardenInnerViewer(this);
        }

        /// <summary>
        /// Kill the default FlowDocument "Copy / Select All" context menu. That menu is a
        /// separate top-level HWND and shows up in screen share / capture even when the
        /// overlay itself is invisible. Copy still works via Ctrl+C (selection) and
        /// Ctrl+Shift+C (whole answer).
        /// </summary>
        private static void HardenInnerViewer(DependencyObject root)
        {
            try
            {
                var viewer = FindInnerViewer(root);
                if (viewer == null) return;
                viewer.VerticalScrollBarVisibility = ScrollBarVisibility.Disabled;
                viewer.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
                viewer.ContextMenu = null;
                viewer.ContextMenuOpening -= SuppressDefaultContextMenu;
                viewer.ContextMenuOpening += SuppressDefaultContextMenu;
            }
            catch
            {
                /* best effort */
            }
        }

        private static void SuppressDefaultContextMenu(object sender, ContextMenuEventArgs e)
        {
            e.Handled = true;
        }

        private static FlowDocumentScrollViewer? FindInnerViewer(DependencyObject root)
        {
            int count = VisualTreeHelper.GetChildrenCount(root);
            for (int i = 0; i < count; i++)
            {
                var child = VisualTreeHelper.GetChild(root, i);
                if (child is FlowDocumentScrollViewer viewer) return viewer;
                var nested = FindInnerViewer(child);
                if (nested != null) return nested;
            }
            return null;
        }
    }

    /// <summary>
    /// Swaps Markdig defaults for SnapEye code + table renderers.
    /// </summary>
    internal sealed class SnapEyeWpfRenderer : WpfRenderer
    {
        public SnapEyeWpfRenderer()
        {
        }

        public SnapEyeWpfRenderer(FlowDocument document) : base(document)
        {
        }

        protected override void LoadRenderers()
        {
            base.LoadRenderers();

            for (int i = ObjectRenderers.Count - 1; i >= 0; i--)
            {
                var r = ObjectRenderers[i];
                if (r is CodeBlockRenderer || r is TableRenderer)
                    ObjectRenderers.RemoveAt(i);
            }

            ObjectRenderers.Insert(0, new NonWrappingCodeBlockRenderer());
            ObjectRenderers.Insert(0, new ContentAwareTableRenderer());
        }
    }
}
