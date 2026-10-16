using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Markdig;
using Markdig.Wpf;

namespace SnapEye.SolutionRegion
{
    /// <summary>
    /// Markdown pipeline and pre-processing shared by every answer viewer, so model output
    /// keeps its line structure the way ChatGPT/Claude display it.
    /// </summary>
    internal static class MarkdownRendering
    {
        /// <summary>
        /// Supported Markdig.Wpf extensions plus single newlines rendered as line breaks;
        /// plain CommonMark joins them with a space, flattening multi-line text into one line.
        /// </summary>
        public static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
            .UseSupportedExtensions()
            .UseSoftlineBreakAsHardlineBreak()
            .Build();

        // Box corners/edges ("+-----+", "|----->"), Unicode box drawing, or arrow connectors.
        // Includes single-dash arrows ("->", "<-") common in system-design sketches.
        private static readonly Regex DiagramAnchor = new(
            @"\+[-=]{2,}|[-=]{2,}\+|[│┃┌┐└┘├┤┬┴┼─━╔╗╚╝║═]|[-=]+>|<[-=]+|\|[-=]{3,}|\[[^\]]+\]\s*-+>",
            RegexOptions.Compiled);

        private static readonly Regex TableSeparator = new(
            @"^\s*\|?\s*:?-{3,}:?\s*(\|\s*:?-{3,}:?\s*)+\|?\s*$",
            RegexOptions.Compiled);

        private static readonly Regex Fence = new(@"^\s{0,3}(```|~~~)", RegexOptions.Compiled);

        /// <summary>
        /// Wraps ASCII diagrams the model sent without a code fence in one, so their spacing
        /// and alignment survive (proportional fonts and whitespace collapsing destroy them).
        /// </summary>
        public static string Prepare(string? markdown)
        {
            if (string.IsNullOrEmpty(markdown)) return "";

            var lines = markdown.Replace("\r\n", "\n").Split('\n');
            var output = new List<string>(lines.Length + 8);
            bool inFence = false;
            int i = 0;
            while (i < lines.Length)
            {
                string line = lines[i];
                if (Fence.IsMatch(line))
                {
                    inFence = !inFence;
                    output.Add(line);
                    i++;
                    continue;
                }
                if (inFence || !IsDiagramLine(line))
                {
                    output.Add(line);
                    i++;
                    continue;
                }

                int end = i;
                while (end + 1 < lines.Length)
                {
                    string next = lines[end + 1];
                    if (Fence.IsMatch(next)) break;
                    if (IsDiagramLine(next))
                    {
                        end++;
                        continue;
                    }

                    // Keep blank lines inside a diagram so multi-layer architectures
                    // (Client / blank / LB / blank / API) stay one fenced block.
                    if (string.IsNullOrWhiteSpace(next)
                        && end + 2 < lines.Length
                        && !Fence.IsMatch(lines[end + 2])
                        && IsDiagramLine(lines[end + 2]))
                    {
                        end++;
                        continue;
                    }
                    break;
                }

                var block = lines.Skip(i).Take(end - i + 1).ToList();
                bool hasAnchor = block.Any(l => DiagramAnchor.IsMatch(l));
                bool alreadyIndentedCode = i > 0 && string.IsNullOrWhiteSpace(lines[i - 1])
                    && block.All(l => l.StartsWith("    ") || l.StartsWith("\t") || string.IsNullOrWhiteSpace(l));
                if (block.Count < 2 || !hasAnchor || alreadyIndentedCode)
                {
                    output.AddRange(block);
                    i = end + 1;
                    continue;
                }

                int indent = Math.Min(3, block
                    .Where(l => !string.IsNullOrWhiteSpace(l))
                    .DefaultIfEmpty(block[0])
                    .Min(l => l.Length - l.TrimStart(' ').Length));
                string pad = new(' ', indent);
                output.Add(pad + "```");
                output.AddRange(block);
                output.Add(pad + "```");
                i = end + 1;
            }

            return string.Join("\n", output);
        }

        private static bool IsDiagramLine(string line)
        {
            if (string.IsNullOrWhiteSpace(line) || TableSeparator.IsMatch(line)) return false;
            if (DiagramAnchor.IsMatch(line)) return true;
            string t = line.Trim();
            // Box body rows ("| - id      |"); padded table rows also match, but a block is
            // only fenced when it contains a box corner or connector.
            int pipes = t.Count(c => c == '|');
            if (pipes >= 2 && Regex.IsMatch(line, @"\|\s{2,}|\s{2,}\|")) return true;
            // Connector rows between boxes: "^", "|", "| 1      v", "1..*".
            return Regex.IsMatch(t, @"^[|^vV<>*.\d:/\\\s-]+$") && Regex.IsMatch(t, @"[|^vV]");
        }
    }
}
