using System;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace SnapEye.Services
{
    /// <summary>
    /// SSE (Server-Sent Events) client for streaming LLM responses from the backend.
    /// Connects to /api/llm/stream and fires events for each token received.
    /// </summary>
    public class StreamingResponseService : IDisposable
    {
        private readonly HttpClient httpClient;
        private readonly string backendUrl;
        private string? authToken;
        private CancellationTokenSource? currentCts;

        /// <summary>Fired when streaming begins.</summary>
        public event EventHandler? StreamStarted;

        /// <summary>Fired for each token received.</summary>
        public event EventHandler<string>? TokenReceived;

        /// <summary>Fired when the stream completes with the full response.</summary>
        public event EventHandler<string>? ResponseComplete;

        /// <summary>Fired on error.</summary>
        public event EventHandler<string>? ErrorOccurred;

        public bool IsStreaming { get; private set; }

        public StreamingResponseService(string backendUrl)
        {
            this.backendUrl = backendUrl;
            httpClient = new HttpClient
            {
                BaseAddress = new Uri(backendUrl),
                Timeout = TimeSpan.FromMinutes(5) // Long timeout for streaming
            };
        }

        public void SetAuthToken(string token)
        {
            authToken = token;
            httpClient.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", token);
        }

        /// <summary>
        /// Stream a response from the LLM endpoint.
        /// </summary>
        public async Task StreamResponseAsync(
            string userMessage,
            string? systemPrompt = null,
            string? provider = null)
        {
            var messages = new[]
            {
                new { role = "user", content = userMessage }
            };

            var (activeProvider, activeModel, activeKey) = ResolveAiCredentials(provider);

            var payload = new
            {
                messages = messages,
                system_prompt = systemPrompt,
                stream = true,
                provider = activeProvider,
                model = activeModel,
                api_key = activeKey,
            };

            await StreamFromEndpointAsync("/api/llm/stream", payload).ConfigureAwait(false);
        }

        /// <summary>
        /// Stream a context-aware AI suggestion (triggered by question detection).
        /// </summary>
        public async Task StreamContextResponseAsync(
            string question,
            string transcriptContext = "",
            string ragContext = "",
            string screenContext = "",
            string? systemPrompt = null,
            string? provider = null)
        {
            var (activeProvider, activeModel, activeKey) = ResolveAiCredentials(provider);

            var payload = new
            {
                question = question,
                transcript_context = transcriptContext,
                rag_context = ragContext,
                screen_context = screenContext,
                system_prompt = systemPrompt,
                provider = activeProvider,
                model = activeModel,
                api_key = activeKey,
            };

            await StreamFromEndpointAsync("/api/llm/context-stream", payload).ConfigureAwait(false);
        }

        /// <summary>
        /// Reads the user's AI Models selection (provider + model + DPAPI-decrypted key) from
        /// <see cref="AiModelsService"/>. An explicit <paramref name="forcedProvider"/> wins
        /// over the stored one; any field the user hasn't configured is returned as null so
        /// the backend can fall back to its own env-var defaults.
        /// </summary>
        private static (string? provider, string? model, string? apiKey) ResolveAiCredentials(string? forcedProvider)
        {
            string? provider = forcedProvider ?? AiModelsService.GetActiveProvider();
            string? model = AiModelsService.GetActiveModel();
            string? apiKey = AiModelsService.GetActiveApiKeyDecrypted();

            // If the caller forced a provider the user hasn't configured, still send the provider
            // name so the backend can use its server-side key; just don't send someone else's key.
            if (!string.IsNullOrEmpty(forcedProvider)
                && !string.Equals(forcedProvider, AiModelsService.GetActiveProvider(), StringComparison.OrdinalIgnoreCase))
            {
                model = null;
                apiKey = null;
            }

            return (provider, model, apiKey);
        }

        /// <summary>
        /// Core streaming method: sends POST request and reads SSE response.
        /// </summary>
        private async Task StreamFromEndpointAsync(string endpoint, object payload)
        {
            if (IsStreaming)
            {
                CancelCurrentStream();
                await Task.Delay(100).ConfigureAwait(false); // Brief delay for cleanup
            }

            currentCts = new CancellationTokenSource();
            var ct = currentCts.Token;
            IsStreaming = true;

            try
            {
                // Serialize the payload off the UI thread. Context payloads include the
                // full transcript + screen-OCR text and can be 100s of KB; JsonSerializer
                // on that-sized object can stall the dispatcher for noticeable time.
                string json = await Task.Run(() => JsonSerializer.Serialize(payload), ct).ConfigureAwait(false);
                var content = new StringContent(json, Encoding.UTF8, "application/json");

                var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
                {
                    Content = content
                };

                var response = await httpClient.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    ct
                ).ConfigureAwait(false);

                if (!response.IsSuccessStatusCode)
                {
                    string error = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                    ErrorOccurred?.Invoke(this, $"LLM request failed ({response.StatusCode}): {error}");
                    return;
                }

                using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                using var reader = new StreamReader(stream);

                var fullResponseBuilder = new StringBuilder(1024);
                bool endEventSent = false;

                while (!reader.EndOfStream && !ct.IsCancellationRequested)
                {
                    string? line = await reader.ReadLineAsync(ct).ConfigureAwait(false);

                    if (string.IsNullOrEmpty(line))
                        continue;

                    if (!line.StartsWith("data: ", StringComparison.Ordinal))
                        continue;

                    string data = line.Substring(6);

                    try
                    {
                        using var doc = JsonDocument.Parse(data);
                        var root = doc.RootElement;

                        if (!root.TryGetProperty("type", out var typeElement))
                            continue;

                        string msgType = typeElement.GetString() ?? "";

                        switch (msgType)
                        {
                            case "start":
                                StreamStarted?.Invoke(this, EventArgs.Empty);
                                break;

                            case "token":
                                if (root.TryGetProperty("token", out var tokenElement))
                                {
                                    string token = tokenElement.GetString() ?? "";
                                    if (token.Length > 0)
                                    {
                                        fullResponseBuilder.Append(token);
                                        TokenReceived?.Invoke(this, token);
                                    }
                                }
                                break;

                            case "end":
                                {
                                    string full;
                                    if (root.TryGetProperty("full_response", out var fullRespElement))
                                        full = fullRespElement.GetString() ?? fullResponseBuilder.ToString();
                                    else
                                        full = fullResponseBuilder.ToString();
                                    endEventSent = true;
                                    ResponseComplete?.Invoke(this, full);
                                }
                                break;

                            case "error":
                                string errorMsg = "Unknown error";
                                if (root.TryGetProperty("error", out var errorElement))
                                    errorMsg = errorElement.GetString() ?? errorMsg;
                                ErrorOccurred?.Invoke(this, errorMsg);
                                break;
                        }
                    }
                    catch (JsonException)
                    {
                        // Skip malformed SSE data — happens occasionally when a frame is split mid-JSON.
                    }
                }

                // Fire a completion event on any clean exit that didn't already get one
                // (network close without "end" frame, or cancellation) so the UI clears its
                // streaming state instead of getting stuck with a half-rendered card.
                if (!endEventSent)
                    ResponseComplete?.Invoke(this, fullResponseBuilder.ToString());
            }
            catch (OperationCanceledException)
            {
                // User cancelled — still notify so UI resets.
                ResponseComplete?.Invoke(this, "");
            }
            catch (Exception ex)
            {
                ErrorOccurred?.Invoke(this, $"Streaming error: {ex.Message}");
            }
            finally
            {
                IsStreaming = false;
            }
        }

        /// <summary>
        /// Cancel the current streaming response.
        /// </summary>
        public void CancelCurrentStream()
        {
            try
            {
                currentCts?.Cancel();
            }
            catch (ObjectDisposedException) { }
        }

        public void Dispose()
        {
            CancelCurrentStream();
            currentCts?.Dispose();
            httpClient?.Dispose();
        }
    }
}
