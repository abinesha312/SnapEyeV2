using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading.Tasks;
using SnapEye.Config;
using SnapEye.Models;

namespace SnapEye.Services
{
    /// <summary>
    /// Best-effort, fire-and-forget sync of conversation messages to the backend's
    /// server-side persistence (see backend/api/routes_conversations.py), so a single
    /// ongoing conversation survives app restarts instead of resetting per launch.
    ///
    /// The local JSON file written by <see cref="ConversationHistoryService"/> remains
    /// the source of truth when offline - a failed sync is logged and swallowed, never
    /// surfaced as a user-facing error, since conversation history syncing must never
    /// block or interrupt the live chat/transcription experience.
    /// </summary>
    public class ConversationSyncService
    {
        private readonly HttpClient httpClient;
        private readonly Func<string?> getAuthHeader;

        public ConversationSyncService(string backendUrl, Func<string?> getAuthHeader)
        {
            this.getAuthHeader = getAuthHeader;
            httpClient = new HttpClient
            {
                BaseAddress = new Uri(backendUrl),
                Timeout = TimeSpan.FromSeconds(10),
            };
        }

        /// <summary>Fire-and-forget: append one message to the caller's active conversation.</summary>
        public void SyncMessageAsync(ConversationMessage msg)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    var body = new
                    {
                        kind = msg.Kind.ToString(),
                        text = msg.Text,
                        label = msg.Label,
                        client_message_id = msg.Id,
                    };
                    var request = new HttpRequestMessage(HttpMethod.Post, "/api/conversations/messages")
                    {
                        Content = JsonContent.Create(body),
                    };
                    string? auth = getAuthHeader();
                    if (auth != null)
                        request.Headers.Add("Authorization", auth);

                    await httpClient.SendAsync(request).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    // Best-effort only - the local JSON history file already has this
                    // message; it'll sync next time a message triggers a fresh attempt.
                    Console.WriteLine($"[ConversationSync] Sync failed (offline?): {ex.Message}");
                }
            });
        }

        /// <summary>
        /// Fetch the caller's active conversation (id + messages so far), for resuming
        /// the view on app startup instead of always starting blank. Returns null on any
        /// failure (offline, unauthenticated, backend down) - callers should fall back to
        /// the local session as-is.
        /// </summary>
        public async Task<(string ConversationId, List<ConversationMessage> Messages)?> FetchActiveConversationAsync()
        {
            try
            {
                var request = new HttpRequestMessage(HttpMethod.Get, "/api/conversations/active");
                string? auth = getAuthHeader();
                if (auth == null)
                    return null;
                request.Headers.Add("Authorization", auth);

                var response = await httpClient.SendAsync(request).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                    return null;

                var payload = await response.Content.ReadFromJsonAsync<ActiveConversationResponse>().ConfigureAwait(false);
                if (payload?.conversation_id == null)
                    return null;

                var messages = new List<ConversationMessage>();
                foreach (var m in payload.messages ?? new List<ServerMessage>())
                {
                    if (!Enum.TryParse<ConversationMessageKind>(m.kind, out var kind))
                        continue;
                    messages.Add(new ConversationMessage
                    {
                        Id = m.client_message_id ?? Guid.NewGuid().ToString("N"),
                        Kind = kind,
                        Text = m.text ?? string.Empty,
                        Label = m.label,
                    });
                }
                return (payload.conversation_id, messages);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ConversationSync] Hydration failed (offline?): {ex.Message}");
                return null;
            }
        }

        /// <summary>Fire-and-forget: close the current conversation and open a fresh one.</summary>
        public void StartNewConversationAsync()
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    var request = new HttpRequestMessage(HttpMethod.Post, "/api/conversations/new");
                    string? auth = getAuthHeader();
                    if (auth != null)
                        request.Headers.Add("Authorization", auth);
                    await httpClient.SendAsync(request).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[ConversationSync] New-conversation sync failed: {ex.Message}");
                }
            });
        }

        private sealed class ActiveConversationResponse
        {
            public string? conversation_id { get; set; }
            public List<ServerMessage>? messages { get; set; }
        }

        private sealed class ServerMessage
        {
            public string? client_message_id { get; set; }
            public string? kind { get; set; }
            public string? text { get; set; }
            public string? label { get; set; }
        }
    }
}
