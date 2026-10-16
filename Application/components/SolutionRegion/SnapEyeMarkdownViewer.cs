using System.Windows.Documents;
using Markdig;
using Markdig.Renderers;
using Markdig.Renderers.Wpf;
using Markdig.Wpf;

namespace SnapEye.SolutionRegion
{
    /// <summary>
    /// MarkdownViewer that renders code blocks without wrapping so ASCII system-design
    /// diagrams keep their alignment (with horizontal scroll when needed).
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
            Document = Markdig.Wpf.Markdown.ToFlowDocument(
                Markdown,
                pipeline,
                new SnapEyeWpfRenderer());
        }
    }

    /// <summary>
    /// WpfRenderer that swaps Markdig's wrapping <see cref="CodeBlockRenderer"/> for
    /// <see cref="NonWrappingCodeBlockRenderer"/>.
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
                if (ObjectRenderers[i] is CodeBlockRenderer)
                    ObjectRenderers.RemoveAt(i);
            }

            ObjectRenderers.Insert(0, new NonWrappingCodeBlockRenderer());
        }
    }
}
