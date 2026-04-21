using System;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using SnapEye.Models;

namespace SnapEye.Services
{
    /// <summary>
    /// Generates a short one-line title/summary for a conversation session using the
    /// backend LLM endpoint with stream=false. Falls back to a local heuristic on failure.
    /// </summary>
    public class ConversationTitleService
    {
        private readonly HttpClient http;
        private readonly string backendUrl;
        private string? authToken;

        public ConversationTitleService(string backendHttpUrl)
        {
            backendUrl = backendHttpUrl.TrimEnd('/');
            http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        }

        public void SetAuthToken(string token) => authToken = token;

        public async Task<string> GenerateAsync(ConversationSession session, CancellationToken ct = default)
        {
            if (session == null || session.Messages.Count == 0)
                return Fallback(session);

            try
            {
                string transcriptSummary = BuildTranscriptForPrompt(session);
                string userPrompt =
                    "Summarize the following conversation in a concise, descriptive one-line title (max 8 words). " +
                    "Do not include quotes, punctuation-only output, or the word 'title'. Reply with only the title text.\n\n" +
                    "CONVERSATION:\n" + transcriptSummary;

                string systemPrompt =
                    "You write short, human-readable titles for meeting conversations. " +
                    "Output only the title text, nothing else. Keep it under 10 words.";

                var payload = new
                {
                    messages = new[] { new { role = "user", content = userPrompt } },
                    system_prompt = systemPrompt,
                    stream = false,
                    max_tokens = 40,
                    temperature = 0.3
                };

                string json = JsonSerializer.Serialize(payload);
                var req = new HttpRequestMessage(HttpMethod.Post, $"{backendUrl}/api/llm/stream")
                {
                    Content = new StringContent(json, Encoding.UTF8, "application/json"),
                };
                if (!string.IsNullOrEmpty(authToken))
                    req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", authToken);

                using var resp = await http.SendAsync(req, ct).ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode) return Fallback(session);

                string body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                using var doc = JsonDocument.Parse(body);
                if (doc.RootElement.TryGetProperty("response", out var respEl))
                {
                    string? text = respEl.GetString();
                    return Clean(text) ?? Fallback(session);
                }

                return Fallback(session);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[TitleService] Generation failed: {ex.Message}");
                return Fallback(session);
            }
        }

        private static string BuildTranscriptForPrompt(ConversationSession session)
        {
            var sb = new StringBuilder();
            // Cap to first ~30 messages and 2000 chars so prompts stay cheap.
            int max = Math.Min(session.Messages.Count, 30);
            int charBudget = 2000;
            for (int i = 0; i < max; i++)
            {
                var m = session.Messages[i];
                string who = m.Kind switch
                {
                    ConversationMessageKind.UserTyped       => "User",
                    ConversationMessageKind.UserSpoken      => "User",
                    ConversationMessageKind.OtherSpoken     => "Other",
                    ConversationMessageKind.AssistantAnswer => "Assistant",
                    ConversationMessageKind.QuickAction     => "User",
                    _ => "User"
                };
                string line = $"{who}: {m.Text.Trim()}\n";
                if (line.Length > charBudget) break;
                sb.Append(line);
                charBudget -= line.Length;
            }
            return sb.ToString();
        }

        /// <summary>Local fallback title when the LLM is unavailable.</summary>
        public static string Fallback(ConversationSession? session)
        {
            if (session == null || session.Messages.Count == 0)
                return $"Conversation • {DateTime.Now:MMM d, h:mm tt}";

            var firstPrompt = session.Messages.FirstOrDefault(m =>
                m.Kind == ConversationMessageKind.UserTyped ||
                m.Kind == ConversationMessageKind.QuickAction ||
                m.Kind == ConversationMessageKind.UserSpoken);

            string basis = firstPrompt?.Text?.Trim() ?? session.Messages[0].Text.Trim();
            if (string.IsNullOrWhiteSpace(basis))
                return $"Conversation • {session.StartedAt:MMM d, h:mm tt}";

            basis = basis.Replace("\r", " ").Replace("\n", " ");
            if (basis.Length > 60) basis = basis[..57] + "…";
            return basis;
        }

        private static string? Clean(string? text)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;
            string t = text.Trim().Trim('"', '\'', '`').Trim();
            // Collapse whitespace
            t = System.Text.RegularExpressions.Regex.Replace(t, @"\s+", " ");
            if (t.Length > 80) t = t[..77] + "…";
            return string.IsNullOrWhiteSpace(t) ? null : t;
        }
    }
}
