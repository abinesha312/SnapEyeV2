using System;
using System.Collections.Generic;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using Markdig.Extensions.Tables;
using Markdig.Renderers;
using Markdig.Renderers.Wpf;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using Markdig.Wpf;
using WpfTable = System.Windows.Documents.Table;
using WpfTableCell = System.Windows.Documents.TableCell;
using WpfTableColumn = System.Windows.Documents.TableColumn;
using WpfTableRow = System.Windows.Documents.TableRow;
using WpfTableRowGroup = System.Windows.Documents.TableRowGroup;
using MdTable = Markdig.Extensions.Tables.Table;
using MdTableCell = Markdig.Extensions.Tables.TableCell;
using MdTableRow = Markdig.Extensions.Tables.TableRow;

namespace SnapEye.SolutionRegion
{
    /// <summary>
    /// Renders markdown tables with absolute column widths and wraps every table in a
    /// horizontal ScrollViewer so wide tables scroll instead of squeezing.
    /// </summary>
    internal sealed class ContentAwareTableRenderer : WpfObjectRenderer<MdTable>
    {
        private const int CompactMaxChars = 5;

        // White grid lines on the dark Live Insights card.
        private static readonly Brush TableBorder = Freeze(Colors.White);
        private static readonly Brush HeaderBg = Freeze(Color.FromRgb(0x1E, 0x29, 0x3B));
        private static readonly Brush HeaderFg = Freeze(Color.FromRgb(0xF8, 0xFA, 0xFC));
        private static readonly Brush CellFg = Freeze(Color.FromRgb(0xE5, 0xE7, 0xEB));

        private static SolidColorBrush Freeze(Color c)
        {
            var b = new SolidColorBrush(c);
            b.Freeze();
            return b;
        }

        protected override void Write(WpfRenderer renderer, MdTable table)
        {
            if (renderer == null) throw new ArgumentNullException(nameof(renderer));
            if (table == null) throw new ArgumentNullException(nameof(table));

            int columnCount = Math.Max(table.ColumnDefinitions.Count, MaxCellsInAnyRow(table));
            if (columnCount <= 0) columnCount = 1;

            var maxChars = MeasureColumnMaxChars(table, columnCount);
            var wpfTable = new WpfTable
            {
                CellSpacing = 0,
                BorderBrush = TableBorder,
                BorderThickness = new Thickness(1),
                Margin = new Thickness(0, 6, 0, 10),
            };

            for (int c = 0; c < columnCount; c++)
            {
                int len = c < maxChars.Length ? maxChars[c] : 1;
                wpfTable.Columns.Add(new WpfTableColumn { Width = PixelForContentLength(len) });
            }

            var wpfRowGroup = new WpfTableRowGroup();
            renderer.Push(wpfTable);
            renderer.Push(wpfRowGroup);

            foreach (var rowObj in table)
            {
                var row = (MdTableRow)rowObj;
                var wpfRow = new WpfTableRow();
                renderer.Push(wpfRow);

                if (row.IsHeader)
                {
                    wpfRow.Background = HeaderBg;
                    wpfRow.FontWeight = FontWeights.SemiBold;
                    wpfRow.Foreground = HeaderFg;
                }

                for (var i = 0; i < row.Count; i++)
                {
                    var cell = (MdTableCell)row[i];
                    var wpfCell = new WpfTableCell
                    {
                        ColumnSpan = cell.ColumnSpan,
                        RowSpan = cell.RowSpan,
                        BorderBrush = TableBorder,
                        BorderThickness = new Thickness(1),
                        Padding = new Thickness(8, 6, 8, 6),
                        Foreground = row.IsHeader ? HeaderFg : CellFg,
                    };

                    renderer.Push(wpfCell);
                    renderer.Write(cell);
                    renderer.Pop();

                    ApplyAlignment(table, cell, i, wpfCell);
                }

                renderer.Pop();
            }

            renderer.Pop();
            renderer.Pop();
        }

        internal static GridLength PixelForContentLength(int maxChars)
        {
            maxChars = Math.Max(1, maxChars);
            if (maxChars <= CompactMaxChars)
                return new GridLength(Math.Clamp(maxChars * 10 + 16, 40, 64));

            double px = maxChars * 8.0 + 32;
            return new GridLength(Math.Clamp(px, 140, 520));
        }

        private static int MaxCellsInAnyRow(MdTable table)
        {
            int max = 0;
            foreach (var rowObj in table)
            {
                if (rowObj is MdTableRow row && row.Count > max)
                    max = row.Count;
            }
            return max;
        }

        private static int[] MeasureColumnMaxChars(MdTable table, int columnCount)
        {
            var max = new int[columnCount];
            for (int i = 0; i < columnCount; i++)
                max[i] = 1;

            foreach (var rowObj in table)
            {
                if (rowObj is not MdTableRow row) continue;
                for (int i = 0; i < row.Count && i < columnCount; i++)
                {
                    if (row[i] is not MdTableCell cell) continue;
                    int len = CellPlainText(cell).Length;
                    if (len > max[i]) max[i] = len;
                }
            }

            return max;
        }

        private static string CellPlainText(MdTableCell cell)
        {
            var sb = new StringBuilder();
            AppendBlockText(cell, sb);
            return sb.ToString().Trim();
        }

        private static void AppendBlockText(ContainerBlock container, StringBuilder sb)
        {
            foreach (var block in container)
            {
                if (block is LeafBlock leaf && leaf.Inline != null)
                    AppendInline(leaf.Inline, sb);
                else if (block is ContainerBlock nested)
                    AppendBlockText(nested, sb);
            }
        }

        private static void AppendInline(Markdig.Syntax.Inlines.Inline inline, StringBuilder sb)
        {
            for (Markdig.Syntax.Inlines.Inline? cur = inline; cur != null; cur = cur.NextSibling)
            {
                switch (cur)
                {
                    case LiteralInline lit:
                        sb.Append(lit.Content.Text, lit.Content.Start, lit.Content.Length);
                        break;
                    case CodeInline code:
                        sb.Append(code.Content);
                        break;
                    case ContainerInline container:
                        AppendInline(container.FirstChild, sb);
                        break;
                    case LineBreakInline:
                        sb.Append(' ');
                        break;
                }
            }
        }

        private static void ApplyAlignment(MdTable table, MdTableCell cell, int index, WpfTableCell wpfCell)
        {
            if (table.ColumnDefinitions.Count == 0) return;
            int columnIndex = cell.ColumnIndex < 0 || cell.ColumnIndex >= table.ColumnDefinitions.Count
                ? index
                : cell.ColumnIndex;
            columnIndex = Math.Clamp(columnIndex, 0, table.ColumnDefinitions.Count - 1);
            var alignment = table.ColumnDefinitions[columnIndex].Alignment;
            if (!alignment.HasValue) return;

            wpfCell.TextAlignment = alignment.Value switch
            {
                TableColumnAlign.Center => TextAlignment.Center,
                TableColumnAlign.Right => TextAlignment.Right,
                _ => TextAlignment.Left,
            };
        }
    }

    /// <summary>
    /// Remeasures columns from rendered cell text, then wraps every table in a
    /// horizontal ScrollViewer (white-bordered table content stays at natural width).
    /// </summary>
    internal static class TableColumnFitter
    {
        internal const string TableScrollTag = "SnapEyeTableHScroll";

        public static void Apply(FlowDocument? document)
        {
            if (document == null) return;

            var tables = new List<WpfTable>();
            foreach (var block in document.Blocks)
            {
                if (block is WpfTable t)
                    tables.Add(t);
            }

            foreach (var table in tables)
            {
                Fit(table);
                EnsureWhiteBorders(table);
                double contentWidth = SumColumnPixels(table) + 4;
                if (contentWidth < 160) contentWidth = 160;

                var next = table.NextBlock;
                var prev = table.PreviousBlock;
                document.Blocks.Remove(table);

                var host = CreateScrollHost(table, contentWidth);
                if (next != null)
                    document.Blocks.InsertBefore(next, host);
                else if (prev != null)
                    document.Blocks.InsertAfter(prev, host);
                else
                    document.Blocks.Add(host);
            }
        }

        private static void EnsureWhiteBorders(WpfTable table)
        {
            table.BorderBrush = Brushes.White;
            table.BorderThickness = new Thickness(1);
            table.CellSpacing = 0;
            foreach (var group in table.RowGroups)
            {
                foreach (var row in group.Rows)
                {
                    foreach (var cell in row.Cells)
                    {
                        cell.BorderBrush = Brushes.White;
                        cell.BorderThickness = new Thickness(1);
                        if (cell.Padding.Left < 1)
                            cell.Padding = new Thickness(8, 6, 8, 6);
                    }
                }
            }
        }

        private static void Fit(WpfTable table)
        {
            int n = table.Columns.Count;
            if (n == 0) return;

            var max = new int[n];
            for (int i = 0; i < n; i++) max[i] = 1;

            foreach (var group in table.RowGroups)
            {
                foreach (var row in group.Rows)
                {
                    int col = 0;
                    foreach (var cell in row.Cells)
                    {
                        if (col >= n) break;
                        int len = GetPlainText(cell).Length;
                        if (len > max[col]) max[col] = len;
                        col += Math.Max(1, cell.ColumnSpan);
                    }
                }
            }

            for (int i = 0; i < n; i++)
                table.Columns[i].Width = ContentAwareTableRenderer.PixelForContentLength(max[i]);
        }

        private static double SumColumnPixels(WpfTable table)
        {
            double sum = 0;
            foreach (var col in table.Columns)
                sum += col.Width.IsAbsolute ? col.Width.Value : 160;
            return sum;
        }

        private static BlockUIContainer CreateScrollHost(WpfTable table, double contentWidth)
        {
            var innerDoc = new FlowDocument(table)
            {
                PageWidth = contentWidth,
                PagePadding = new Thickness(0),
                Background = Brushes.Transparent,
                TextAlignment = TextAlignment.Left,
                FontFamily = new FontFamily("Segoe UI"),
                FontSize = 12.5,
                Foreground = Brushes.White,
                IsHyphenationEnabled = false,
            };

            // RichTextBox measures height reliably inside BlockUIContainer.
            var rtb = new RichTextBox
            {
                Document = innerDoc,
                IsReadOnly = true,
                IsDocumentEnabled = true,
                Focusable = false,
                BorderThickness = new Thickness(0),
                Background = Brushes.Transparent,
                Padding = new Thickness(0),
                CaretBrush = Brushes.Transparent,
                Width = contentWidth,
                MinWidth = contentWidth,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
                IsUndoEnabled = false,
            };

            rtb.Measure(new Size(contentWidth, double.PositiveInfinity));
            double h = Math.Ceiling(rtb.DesiredSize.Height);
            if (h > 1)
            {
                rtb.Height = h;
                rtb.MinHeight = h;
            }

            var scroll = new ScrollViewer
            {
                Tag = TableScrollTag,
                Content = rtb,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
                PanningMode = PanningMode.None,
                Focusable = false,
                CanContentScroll = false,
                Background = Brushes.Transparent,
            };

            var host = new Border
            {
                Background = Brushes.Transparent,
                Margin = new Thickness(0, 4, 0, 8),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                ClipToBounds = true,
                Child = scroll,
            };

            host.Loaded += (_, _) => ConstrainTableScrollWidth(host, scroll);
            host.SizeChanged += (_, _) => ConstrainTableScrollWidth(host, scroll);

            return new BlockUIContainer(host);
        }

        private static void ConstrainTableScrollWidth(Border host, ScrollViewer scroll)
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

        private static string GetPlainText(WpfTableCell cell)
        {
            var sb = new StringBuilder();
            foreach (var block in cell.Blocks)
                AppendBlock(block, sb);
            return sb.ToString().Trim();
        }

        private static void AppendBlock(System.Windows.Documents.Block block, StringBuilder sb)
        {
            switch (block)
            {
                case Paragraph p:
                    AppendInlines(p.Inlines, sb);
                    break;
                case Section s:
                    foreach (var b in s.Blocks) AppendBlock(b, sb);
                    break;
                case List list:
                    foreach (var item in list.ListItems)
                        foreach (var b in item.Blocks)
                            AppendBlock(b, sb);
                    break;
            }
        }

        private static void AppendInlines(InlineCollection inlines, StringBuilder sb)
        {
            foreach (var inline in inlines)
            {
                switch (inline)
                {
                    case Run run:
                        sb.Append(run.Text);
                        break;
                    case Span span:
                        AppendInlines(span.Inlines, sb);
                        break;
                    case LineBreak:
                        sb.Append(' ');
                        break;
                }
            }
        }
    }
}
