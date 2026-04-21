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

            var payload = new
            {
                messages = messages,
                system_prompt = systemPrompt,
                stream = true,
                provider = provider
            };

            await StreamFromEndpointAsync("/api/llm/stream", payload);
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
            var payload = new
            {
                question = question,
                transcript_context = transcriptContext,
                rag_context = ragContext,
                screen_context = screenContext,
                system_prompt = systemPrompt,
                provider = provider
            };

            await StreamFromEndpointAsync("/api/llm/context-stream", payload);
        }

        /// <summary>
        /// Core streaming method: sends POST request and reads SSE response.
        /// </summary>
        private async Task StreamFromEndpointAsync(string endpoint, object payload)
        {
            if (IsStreaming)
            {
                CancelCurrentStream();
                await Task.Delay(100); // Brief delay for cleanup
            }

            currentCts = new CancellationTokenSource();
            var ct = currentCts.Token;
            IsStreaming = true;

            try
            {
                string json = JsonSerializer.Serialize(payload);
                var content = new StringContent(json, Encoding.UTF8, "application/json");

                var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
                {
                    Content = content
                };

                var response = await httpClient.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    ct
                );

                if (!response.IsSuccessStatusCode)
                {
                    string error = await response.Content.ReadAsStringAsync(ct);
                    ErrorOccurred?.Invoke(this, $"LLM request failed ({response.StatusCode}): {error}");
                    return;
                }

                using var stream = await response.Content.ReadAsStreamAsync(ct);
                using var reader = new StreamReader(stream);

                string fullResponse = "";

                while (!reader.EndOfStream && !ct.IsCancellationRequested)
                {
                    string? line = await reader.ReadLineAsync(ct);

                    if (string.IsNullOrEmpty(line))
                        continue;

                    if (!line.StartsWith("data: "))
                        continue;

                    string data = line.Substring(6); // Remove "data: " prefix

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
                                    fullResponse += token;
                                    TokenReceived?.Invoke(this, token);
                                }
                                break;

                            case "end":
                                if (root.TryGetProperty("full_response", out var fullRespElement))
                                {
                                    fullResponse = fullRespElement.GetString() ?? fullResponse;
                                }
                                ResponseComplete?.Invoke(this, fullResponse);
                                break;

                            case "error":
                                string errorMsg = "Unknown error";
                                if (root.TryGetProperty("error", out var errorElement))
                                {
                                    errorMsg = errorElement.GetString() ?? errorMsg;
                                }
                                ErrorOccurred?.Invoke(this, errorMsg);
                                break;
                        }
                    }
                    catch (JsonException)
                    {
                        // Skip malformed SSE data
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Stream was cancelled
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
