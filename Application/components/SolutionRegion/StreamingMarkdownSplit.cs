using System;
using System.Linq;

namespace SnapEye.SolutionRegion
{
    /// <summary>
    /// Splits a streaming markdown buffer into a stable prefix (safe to render with Markdig)
    /// and a pending plain-text suffix (incomplete table, fence, or ASCII diagram).
    /// </summary>
    internal static class StreamingMarkdownSplit
    {
        public readonly struct Result
        {
            public Result(string stableMarkdown, string pendingPlain)
            {
                StableMarkdown = stableMarkdown ?? "";
                PendingPlain = pendingPlain ?? "";
            }

            public string StableMarkdown { get; }
            public string PendingPlain { get; }
        }

        /// <summary>
        /// Returns everything that can be rendered as markdown now, plus any trailing
        /// incomplete fence / GFM table / ASCII diagram held as plain text until it closes.
        /// </summary>
        public static Result Split(string? buffer)
        {
            if (string.IsNullOrEmpty(buffer))
                return new Result("", "");

            string normalized = buffer.Replace("\r\n", "\n");
            // Preserve a trailing newline so re-join does not drop the "line still open" state.
            bool endsWithNewline = normalized.EndsWith('\n');
            string[] lines = normalized.Split('\n');
            // Split("a\n") → ["a", ""]; treat the final empty as a real trailing blank line.
            if (endsWithNewline && lines.Length > 0 && lines[^1].Length == 0)
            {
                // keep the empty trailing entry
            }

            int pendingStart = FindPendingStart(lines);
            if (pendingStart < 0 || pendingStart >= lines.Length)
                return new Result(normalized, "");

            string stable = pendingStart == 0
                ? ""
                : string.Join("\n", lines.Take(pendingStart));
            string pending = string.Join("\n", lines.Skip(pendingStart));
            // If original ended with \n and pending consumed the last empty segment, keep it.
            return new Result(stable, pending);
        }

        /// <summary>Index of the first line of the trailing incomplete region, or -1.</summary>
        private static int FindPendingStart(string[] lines)
        {
            if (lines.Length == 0) return -1;

            int fenceStart = FindOpenFenceStart(lines);
            if (fenceStart >= 0) return fenceStart;

            int tableStart = FindOpenTableStart(lines);
            if (tableStart >= 0) return tableStart;

            int diagramStart = FindOpenDiagramStart(lines);
            if (diagramStart >= 0) return diagramStart;

            return -1;
        }

        private static int FindOpenFenceStart(string[] lines)
        {
            int fenceStart = -1;
            bool inFence = false;
            for (int i = 0; i < lines.Length; i++)
            {
                if (!MarkdownRendering.IsFenceLine(lines[i])) continue;
                if (!inFence)
                {
                    inFence = true;
                    fenceStart = i;
                }
                else
                {
                    inFence = false;
                    fenceStart = -1;
                }
            }
            return inFence ? fenceStart : -1;
        }

        /// <summary>
        /// Trailing GFM table that has not yet been closed by a non-table line.
        /// A blank line after the table counts as closed. Requires a separator (or 2+ rows)
        /// so a lone pipe fragment is not held back.
        /// </summary>
        private static int FindOpenTableStart(string[] lines)
        {
            int lastContent = LastNonWhitespaceIndex(lines);
            if (lastContent < 0) return -1;
            if (!MarkdownRendering.IsMarkdownTableLine(lines[lastContent]))
                return -1;

            // Blank line(s) after the table ⇒ GFM table is closed.
            if (lastContent < lines.Length - 1)
            {
                bool onlyBlanksAfter = true;
                for (int j = lastContent + 1; j < lines.Length; j++)
                {
                    if (!string.IsNullOrWhiteSpace(lines[j]))
                    {
                        onlyBlanksAfter = false;
                        break;
                    }
                }
                if (onlyBlanksAfter)
                    return -1; // closed by trailing blank
            }

            int start = lastContent;
            while (start > 0 && MarkdownRendering.IsMarkdownTableLine(lines[start - 1]))
                start--;

            int count = lastContent - start + 1;
            bool hasSeparator = false;
            for (int i = start; i <= lastContent; i++)
            {
                if (MarkdownRendering.IsTableSeparatorLine(lines[i]))
                {
                    hasSeparator = true;
                    break;
                }
            }

            // Established table in progress: separator present, or at least two pipe rows.
            if (hasSeparator || count >= 2)
                return start;

            return -1;
        }

        /// <summary>
        /// Trailing ASCII diagram run (same heuristics as <see cref="MarkdownRendering.Prepare"/>)
        /// that has not yet been terminated by a non-diagram line.
        /// </summary>
        private static int FindOpenDiagramStart(string[] lines)
        {
            int lastContent = LastNonWhitespaceIndex(lines);
            if (lastContent < 0) return -1;
            if (!MarkdownRendering.IsDiagramLine(lines[lastContent]))
                return -1;

            // Trailing blank after diagram content: still open (blank may belong inside the diagram).
            int end = lastContent;
            int start = lastContent;
            while (start > 0)
            {
                string prev = lines[start - 1];
                if (MarkdownRendering.IsFenceLine(prev)) break;
                if (MarkdownRendering.IsDiagramLine(prev))
                {
                    start--;
                    continue;
                }
                // Blank line only kept if the line before it is also diagram (inside block).
                if (string.IsNullOrWhiteSpace(prev)
                    && start >= 2
                    && MarkdownRendering.IsDiagramLine(lines[start - 2])
                    && !MarkdownRendering.IsFenceLine(lines[start - 2]))
                {
                    start--;
                    continue;
                }
                break;
            }

            var block = lines.Skip(start).Take(end - start + 1).Where(l => !string.IsNullOrWhiteSpace(l)).ToList();
            bool hasAnchor = block.Any(l => MarkdownRendering.DiagramAnchor.IsMatch(l));
            bool looksLikeMarkdownTable = block.Count(MarkdownRendering.IsMarkdownTableLine) >= 2
                && block.Any(MarkdownRendering.IsTableSeparatorLine);

            // Not a diagram yet (or is a table) — do not hold.
            if (looksLikeMarkdownTable) return -1;
            if (block.Count < 1) return -1;
            // Hold once we see an anchor, or 2+ diagram lines (in progress).
            if (!hasAnchor && block.Count < 2) return -1;

            return start;
        }

        private static int LastNonWhitespaceIndex(string[] lines)
        {
            for (int i = lines.Length - 1; i >= 0; i--)
            {
                if (!string.IsNullOrWhiteSpace(lines[i]))
                    return i;
            }
            return -1;
        }
    }
}
