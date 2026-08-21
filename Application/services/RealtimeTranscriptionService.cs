using System;
using System.Diagnostics;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using SnapEye.Models;

namespace SnapEye.Services
{
    /// <summary>
    /// WebSocket-based real-time transcription service  that connects to the SnapEye backend API
    /// Handles bidirectional audio streaming and transcription
    /// </summary>
    public class RealtimeTranscriptionService : IDisposable
    {
        private ClientWebSocket? webSocket;
        private CancellationTokenSource? cancellationTokenSource;
        private bool isConnected = false;
        private readonly string baseUrl;
        private string? authToken;
        private TranscriptionConversation conversation;
        private DateTime lastAudioSent = DateTime.MinValue;

        // ClientWebSocket.SendAsync is NOT safe for concurrent calls. Two audio capture
        // services (mic + speaker) deliver buffers independently and can both call
        // SendAudioAsync at the same instant, which would throw InvalidOperationException
        // ("There is already one outstanding Send call for this WebSocket instance").
        // This semaphore serializes all outbound frames.
        private readonly System.Threading.SemaphoreSlim sendLock = new(1, 1);

        // Events
        public event EventHandler<TranscriptionMessage>? TranscriptionReceived;
        public event EventHandler<string>? ErrorOccurred;
        public event EventHandler? Connected;
        public event EventHandler? Disconnected;
        public event EventHandler<string>? SessionCreated;
        
        // AI Suggestion events (from auto-trigger pipeline)
        public event EventHandler<string>? AISuggestionStarted;
        public event EventHandler<string>? AISuggestionToken;
        public event EventHandler<string>? AISuggestionCompleted;
        public event EventHandler<string>? AISuggestionError;

        /// <summary>Backend-reported confidence (0..1) for the just-completed AI suggestion.</summary>
        public event EventHandler<double>? AISuggestionConfidence;

        public bool IsConnected => isConnected;
        public TranscriptionConversation Conversation => conversation;

        /// <summary>Enable raw WebSocket trace via environment SNAPEYE_VERBOSE_WS=1</summary>
        private static bool VerboseWs =>
            string.Equals(Environment.GetEnvironmentVariable("SNAPEYE_VERBOSE_WS"), "1", StringComparison.OrdinalIgnoreCase);

        private static void WsTrace(string message)
        {
            if (VerboseWs)
                Trace.WriteLine("[WS] " + message);
        }

        public RealtimeTranscriptionService(string backendUrl = "ws://localhost:8080")
        {
            baseUrl = backendUrl;
            conversation = new TranscriptionConversation();
        }

        /// <summary>
        /// Set authentication token for WebSocket connection
        /// </summary>
        public void SetAuthToken(string token)
        {
            authToken = token;
        }

        /// <summary>
        /// Connect to the real-time transcription WebSocket endpoint
        /// </summary>
        /// <param name="model">Deepgram model (default: nova-3)</param>
        /// <param name="language">Language code (default: en)</param>
        /// <param name="endpointingMs">Endpointing threshold in milliseconds (default: 500ms)</param>
        public async Task<bool> ConnectAsync(string model = "nova-3", string language = "en", int endpointingMs = 300)
        {
            if (isConnected)
            {
                ErrorOccurred?.Invoke(this, "Already connected");
                return false;
            }

            try
            {
                webSocket = new ClientWebSocket();
                cancellationTokenSource = new CancellationTokenSource();

                // Build WebSocket URL with query parameters for Deepgram Live Streaming API
                var uriBuilder = new StringBuilder($"{baseUrl}/api/transcribe/audio?");
                
                if (!string.IsNullOrEmpty(authToken))
                {
                    uriBuilder.Append($"token={Uri.EscapeDataString(authToken)}&");
                }
                
                uriBuilder.Append($"model={model}&language={language}&endpointing_ms={endpointingMs}");

                var uri = new Uri(uriBuilder.ToString());

                // Connect to WebSocket
                await webSocket.ConnectAsync(uri, cancellationTokenSource.Token);
                isConnected = true;

                // Start receiving messages
                _ = Task.Run(() => ReceiveMessagesAsync(cancellationTokenSource.Token));

                Connected?.Invoke(this, EventArgs.Empty);
                return true;
            }
            catch (Exception ex)
            {
                isConnected = false;
                ErrorOccurred?.Invoke(this, $"Connection failed: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Send audio data to the backend for transcription
        /// Audio must be in PCM16 format, 24kHz, mono (handled by AudioCaptureService)
        /// </summary>
        public async Task SendAudioAsync(byte[] audioData, MessageSource source)
        {
            // Silently drop packets when disconnected — audio capture can continue to
            // deliver a few buffers after a disconnect starts, and we don't want to spam
            // the UI / error log with "Not connected" for every one.
            if (!isConnected || webSocket == null || webSocket.State != WebSocketState.Open)
                return;

            string audioBase64 = Convert.ToBase64String(audioData);
            string sourceStr = source == MessageSource.Microphone ? "microphone" : "speaker";
            var message = new { type = "audio", data = audioBase64, source = sourceStr };
            string jsonMessage = JsonSerializer.Serialize(message);
            byte[] messageBytes = Encoding.UTF8.GetBytes(jsonMessage);

            var token = cancellationTokenSource?.Token ?? CancellationToken.None;
            await sendLock.WaitAsync(token).ConfigureAwait(false);
            try
            {
                if (!isConnected || webSocket == null || webSocket.State != WebSocketState.Open)
                    return;
                await webSocket.SendAsync(
                    new ArraySegment<byte>(messageBytes),
                    WebSocketMessageType.Text,
                    endOfMessage: true,
                    cancellationToken: token).ConfigureAwait(false);
                lastAudioSent = DateTime.Now;
            }
            catch (OperationCanceledException)
            {
                // Cancellation during shutdown — silent.
            }
            catch (Exception ex)
            {
                ErrorOccurred?.Invoke(this, $"Failed to send audio: {ex.Message}");
            }
            finally
            {
                try { sendLock.Release(); } catch { /* disposed */ }
            }
        }

        /// <summary>
        /// Push on-screen OCR text to the backend so auto-suggestions include ON-SCREEN TEXT in context.
        /// </summary>
        public async Task SendScreenContextAsync(string ocrText)
        {
            if (!isConnected || webSocket == null || webSocket.State != WebSocketState.Open)
                return;
            if (string.IsNullOrWhiteSpace(ocrText))
                return;

            if (ocrText.Length > 12000)
                ocrText = ocrText.Substring(0, 12000);

            var message = new { action = "set_screen_context", text = ocrText };
            string json = JsonSerializer.Serialize(message);
            byte[] bytes = Encoding.UTF8.GetBytes(json);
            var token = cancellationTokenSource?.Token ?? CancellationToken.None;

            await sendLock.WaitAsync(token).ConfigureAwait(false);
            try
            {
                if (!isConnected || webSocket == null || webSocket.State != WebSocketState.Open)
                    return;
                await webSocket.SendAsync(
                    new ArraySegment<byte>(bytes),
                    WebSocketMessageType.Text,
                    endOfMessage: true,
                    cancellationToken: token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { /* shutdown */ }
            catch (Exception ex)
            {
                ErrorOccurred?.Invoke(this, $"Failed to send screen context: {ex.Message}");
            }
            finally
            {
                try { sendLock.Release(); } catch { /* disposed */ }
            }
        }

        /// <summary>
        /// Push the active mode and (for interview mode) the target job description to the
        /// backend session, so the live auto-suggestion pipeline tailors and grounds answers.
        /// </summary>
        public async Task SendInterviewContextAsync(
            string jobDescription, string company, string role, string mode, string modeSystemPrompt = "")
        {
            if (!isConnected || webSocket == null || webSocket.State != WebSocketState.Open)
                return;

            if (!string.IsNullOrEmpty(jobDescription) && jobDescription.Length > 8000)
                jobDescription = jobDescription.Substring(0, 8000);

            if (!string.IsNullOrEmpty(modeSystemPrompt) && modeSystemPrompt.Length > 4000)
                modeSystemPrompt = modeSystemPrompt.Substring(0, 4000);

            var message = new
            {
                action = "set_interview_context",
                job_description = jobDescription ?? "",
                company = company ?? "",
                role = role ?? "",
                mode = mode ?? "",
                mode_system_prompt = modeSystemPrompt ?? "",
            };
            string json = JsonSerializer.Serialize(message);
            byte[] bytes = Encoding.UTF8.GetBytes(json);
            var token = cancellationTokenSource?.Token ?? CancellationToken.None;

            await sendLock.WaitAsync(token).ConfigureAwait(false);
            try
            {
                if (!isConnected || webSocket == null || webSocket.State != WebSocketState.Open)
                    return;
                await webSocket.SendAsync(
                    new ArraySegment<byte>(bytes),
                    WebSocketMessageType.Text,
                    endOfMessage: true,
                    cancellationToken: token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { /* shutdown */ }
            catch (Exception ex)
            {
                ErrorOccurred?.Invoke(this, $"Failed to send interview context: {ex.Message}");
            }
            finally
            {
                try { sendLock.Release(); } catch { /* disposed */ }
            }
        }

        /// <summary>
        /// Ask the backend to abort any in-flight auto AI suggestion (user hit Stop). The
        /// backend cancels the running generation task and frees its AI lock.
        /// </summary>
        public async Task SendCancelAiAsync()
        {
            if (!isConnected || webSocket == null || webSocket.State != WebSocketState.Open)
                return;

            var message = new { action = "cancel_ai" };
            string json = JsonSerializer.Serialize(message);
            byte[] bytes = Encoding.UTF8.GetBytes(json);
            var token = cancellationTokenSource?.Token ?? CancellationToken.None;

            await sendLock.WaitAsync(token).ConfigureAwait(false);
            try
            {
                if (!isConnected || webSocket == null || webSocket.State != WebSocketState.Open)
                    return;
                await webSocket.SendAsync(
                    new ArraySegment<byte>(bytes),
                    WebSocketMessageType.Text,
                    endOfMessage: true,
                    cancellationToken: token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { /* shutdown */ }
            catch (Exception ex)
            {
                ErrorOccurred?.Invoke(this, $"Failed to send cancel: {ex.Message}");
            }
            finally
            {
                try { sendLock.Release(); } catch { /* disposed */ }
            }
        }

        /// <summary>
        /// Receive and process messages from WebSocket
        /// </summary>
        private async Task ReceiveMessagesAsync(CancellationToken cancellationToken)
        {
            var buffer = new byte[16384]; // 16KB buffer for larger messages

            try
            {
                while (isConnected && webSocket != null && webSocket.State == WebSocketState.Open)
                {
                    // Accumulate multi-frame messages into a MemoryStream
                    using var messageStream = new System.IO.MemoryStream();
                    WebSocketReceiveResult result;
                    
                    do
                    {
                        result = await webSocket.ReceiveAsync(
                            new ArraySegment<byte>(buffer),
                            cancellationToken
                        );
                        
                        if (result.MessageType == WebSocketMessageType.Close)
                        {
                            await DisconnectAsync();
                            return;
                        }
                        
                        messageStream.Write(buffer, 0, result.Count);
                    } while (!result.EndOfMessage);

                    if (result.MessageType == WebSocketMessageType.Text)
                    {
                        string message = Encoding.UTF8.GetString(messageStream.ToArray());
                        ProcessReceivedMessage(message);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Normal cancellation
            }
            catch (Exception ex)
            {
                ErrorOccurred?.Invoke(this, $"Receive error: {ex.Message}");
            }
            finally
            {
                await DisconnectAsync();
            }
        }

        /// <summary>
        /// Process received WebSocket message
        /// </summary>
        private void ProcessReceivedMessage(string message)
        {
            try
            {
                WsTrace(message.Length > 500 ? message.Substring(0, 500) + "…" : message);

                if (string.IsNullOrWhiteSpace(message) || !message.TrimStart().StartsWith("{"))
                {
                    WsTrace("skip non-JSON");
                    return;
                }

                using var doc = JsonDocument.Parse(message);
                var root = doc.RootElement;

                if (!root.TryGetProperty("type", out var typeElement))
                {
                    WsTrace("missing type");
                    return;
                }

                string messageType = typeElement.GetString() ?? "";
                WsTrace("type=" + messageType);

                switch (messageType)
                {
                    case "session.created":
                        HandleSessionCreated(root);
                        break;

                    case "transcript.updated":
                    case "transcript.final":
                        HandleDeepgramTranscription(root);
                        break;
                    
                    case "message.finalized":
                        HandleMessageFinalized(root);
                        break;

                    case "transcript":
                        HandleTranscription(root);
                        break;

                    case "session.messages":
                        HandleSessionMessages(root);
                        break;

                    case "session.reset":
                        conversation.Clear();
                        break;

                    case "error":
                        HandleError(root);
                        break;

                    case "ai.suggestion.start":
                        HandleAISuggestionStart(root);
                        break;
                    
                    case "ai.suggestion.token":
                        HandleAISuggestionToken(root);
                        break;
                    
                    case "ai.suggestion.end":
                        if (root.TryGetProperty("confidence", out var aiConfElem)
                            && aiConfElem.ValueKind == JsonValueKind.Number)
                        {
                            AISuggestionConfidence?.Invoke(this, aiConfElem.GetDouble());
                        }
                        AISuggestionCompleted?.Invoke(this, "");
                        break;
                    
                    case "ai.suggestion.error":
                        {
                            string aiError = "AI suggestion failed";
                            if (root.TryGetProperty("error", out var aiErrElem) && aiErrElem.ValueKind == JsonValueKind.String)
                                aiError = aiErrElem.GetString() ?? aiError;
                            AISuggestionError?.Invoke(this, aiError);
                        }
                        break;

                    case "screen_context.ack":
                        break;

                    default:
                        WsTrace("unknown type: " + messageType);
                        break;
                }
            }
            catch (JsonException ex)
            {
                WsTrace("JSON parse: " + ex.Message);
            }
            catch (Exception ex)
            {
                Trace.WriteLine("[WS] process error: " + ex.Message);
                ErrorOccurred?.Invoke(this, $"Failed to process message: {ex.Message}");
            }
        }

        private void HandleSessionCreated(JsonElement root)
        {
            // New Deepgram format: session_id is at root level
            if (root.TryGetProperty("session_id", out var sessionId))
            {
                string id = sessionId.GetString() ?? "";
                conversation.SessionId = id;
                SessionCreated?.Invoke(this, id);
            }
            // Old OpenAI format (fallback)
            else if (root.TryGetProperty("session", out var session) &&
                session.TryGetProperty("id", out var oldSessionId))
            {
                string id = oldSessionId.GetString() ?? "";
                conversation.SessionId = id;
                SessionCreated?.Invoke(this, id);
            }
        }

        private void HandleDeepgramTranscription(JsonElement root)
        {
            try
            {
                if (!root.TryGetProperty("transcript", out var transcriptElement))
                    return;

                var transcript = transcriptElement.GetString() ?? "";
                if (string.IsNullOrWhiteSpace(transcript))
                    return;

                // Get is_final flag (at root level)
                bool isFinal = false;
                if (root.TryGetProperty("is_final", out var isFinalElement))
                {
                    isFinal = isFinalElement.GetBoolean();
                }

                // Get source from root level
                MessageSource source = MessageSource.Microphone;
                if (root.TryGetProperty("source", out var sourceElement))
                {
                    string sourceStr = sourceElement.GetString() ?? "";
                    source = sourceStr == "speaker" ? MessageSource.Speaker : MessageSource.Microphone;
                }

                // Get is_new_segment flag (at root level)
                bool isNewSegment = false;
                if (root.TryGetProperty("is_new_segment", out var segmentElement))
                {
                    isNewSegment = segmentElement.GetBoolean();
                }

                // Get confidence if available (at root level)
                double confidence = 0.0;
                if (root.TryGetProperty("confidence", out var confidenceElement))
                {
                    confidence = confidenceElement.GetDouble();
                }

                // Try to get message object if available (for message_id)
                string messageId = "";
                JsonElement messageElement = default;
                if (root.TryGetProperty("message", out messageElement))
                {
                    // Check if message is an object (not null/string)
                    if (messageElement.ValueKind == JsonValueKind.Object)
                    {
                        // Message object exists, use it for message_id
                        if (messageElement.TryGetProperty("message_id", out var messageIdElement))
                        {
                            if (messageIdElement.ValueKind == JsonValueKind.String)
                            {
                                messageId = messageIdElement.GetString() ?? "";
                            }
                        }
                        else if (messageElement.TryGetProperty("id", out var idElement))
                        {
                            if (idElement.ValueKind == JsonValueKind.String)
                            {
                                messageId = idElement.GetString() ?? "";
                            }
                        }
                    }
                    // If message is null or not an object, that's okay - we'll generate an ID
                }

                TranscriptionMessage? existingMessage = null;
                if (!string.IsNullOrEmpty(messageId))
                {
                    existingMessage = conversation.FindByMessageId(messageId);
                }

                if (existingMessage != null)
                {
                    existingMessage.Text = transcript;
                    existingMessage.IsFinal = isFinal;
                    existingMessage.Timestamp = DateTime.Now;
                    existingMessage.IsNewSegment = false;  // Updating, not new
                    if (confidence > 0)
                    {
                        existingMessage.Confidence = confidence;
                    }
                    
                    TranscriptionReceived?.Invoke(this, existingMessage);
                }
                else
                {
                    var message = conversation.AddMessage(transcript, source);
                    
                    if (!string.IsNullOrEmpty(messageId))
                    {
                        message.MessageId = messageId;
                    }
                    else
                    {
                        // Generate a temporary ID if backend didn't provide one
                        message.MessageId = $"msg_{conversation.Messages.Count}_{DateTime.Now.Ticks}";
                    }
                    
                    message.IsFinal = isFinal;
                    message.IsNewSegment = isNewSegment;
                    message.Source = source;
                    if (confidence > 0)
                    {
                        message.Confidence = confidence;
                    }

                    TranscriptionReceived?.Invoke(this, message);
                }
            }
            catch (Exception ex)
            {
                Trace.WriteLine("[DEEPGRAM] " + ex.Message);
                ErrorOccurred?.Invoke(this, $"Failed to parse Deepgram transcription: {ex.Message}");
            }
        }

        private void HandleMessageFinalized(JsonElement root)
        {
            try
            {
                if (root.TryGetProperty("message", out var messageElement))
                {
                    if (messageElement.ValueKind != JsonValueKind.Object)
                    {
                        WsTrace("message.finalized: message not object");
                        return;
                    }
                    
                    // Safely get message_id
                    string messageId = "";
                    if (messageElement.TryGetProperty("message_id", out var messageIdElement))
                    {
                        if (messageIdElement.ValueKind == JsonValueKind.String)
                        {
                            messageId = messageIdElement.GetString() ?? "";
                        }
                    }
                    
                    // Safely get transcript
                    string transcript = "";
                    if (messageElement.TryGetProperty("transcript", out var transcriptElement))
                    {
                        if (transcriptElement.ValueKind == JsonValueKind.String)
                        {
                            transcript = transcriptElement.GetString() ?? "";
                        }
                    }
                    
                    // Find and finalize the message
                    if (!string.IsNullOrEmpty(messageId))
                    {
                        var message = conversation.FindByMessageId(messageId);
                        if (message != null)
                        {
                            if (!string.IsNullOrEmpty(transcript))
                            {
                                message.Text = transcript;
                            }
                            message.IsFinal = true;
                            message.Timestamp = DateTime.Now;
                            TranscriptionReceived?.Invoke(this, message);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Trace.WriteLine("[DEEPGRAM] finalized: " + ex.Message);
                ErrorOccurred?.Invoke(this, $"Failed to parse finalized message: {ex.Message}");
            }
        }

        private void HandleSessionMessages(JsonElement root)
        {
            try
            {
                if (!root.TryGetProperty("messages", out var messagesElement))
                    return;

                // Build the replacement list off-lock then swap atomically.
                var rebuilt = new System.Collections.Generic.List<TranscriptionMessage>();
                int idx = 0;
                foreach (var msgElement in messagesElement.EnumerateArray())
                {
                    string messageId = "";
                    if (msgElement.TryGetProperty("message_id", out var idElem) && idElem.ValueKind == JsonValueKind.String)
                        messageId = idElem.GetString() ?? "";

                    string transcript = "";
                    if (msgElement.TryGetProperty("transcript", out var transcriptElem) && transcriptElem.ValueKind == JsonValueKind.String)
                        transcript = transcriptElem.GetString() ?? "";

                    bool isFinal = false;
                    if (msgElement.TryGetProperty("is_final", out var finalElem))
                        isFinal = finalElem.ValueKind == JsonValueKind.True;

                    MessageSource source = MessageSource.Microphone;
                    if (msgElement.TryGetProperty("source", out var sourceElem) && sourceElem.ValueKind == JsonValueKind.String)
                    {
                        string sourceStr = sourceElem.GetString() ?? "";
                        source = sourceStr == "speaker" ? MessageSource.Speaker : MessageSource.Microphone;
                    }

                    rebuilt.Add(new TranscriptionMessage
                    {
                        Index = idx++,
                        MessageId = messageId,
                        Text = transcript,
                        Source = source,
                        IsFinal = isFinal,
                        IsNewSegment = true,
                        Timestamp = DateTime.Now,
                        SessionId = conversation.SessionId,
                    });
                }
                conversation.ReplaceAll(rebuilt);
            }
            catch (Exception ex)
            {
                ErrorOccurred?.Invoke(this, $"Failed to parse session messages: {ex.Message}");
            }
        }

        private void HandleTranscription(JsonElement root)
        {
            try {
                string? text = null;
                MessageSource source = MessageSource.Microphone;
                bool isNewSegment = false;  // NEW
                
                // Parse transcript
                if (root.TryGetProperty("text", out var textProp))
                {
                    text = textProp.GetString();
                }
                
                // Parse source
                if (root.TryGetProperty("source", out var sourceProp))
                {
                    string sourceStr = sourceProp.GetString() ?? "";
                    source = sourceStr == "speaker" ? MessageSource.Speaker : MessageSource.Microphone;
                }
                
                // Parse segment flag - THIS IS THE KEY
                if (root.TryGetProperty("is_new_segment", out var segmentProp))
                {
                    isNewSegment = segmentProp.GetBoolean();
                }
                
                if (!string.IsNullOrWhiteSpace(text))
                {
                    var message = conversation.AddMessage(text, source);
                    message.IsNewSegment = isNewSegment;
                    TranscriptionReceived?.Invoke(this, message);
                }
            }
            catch (Exception ex)
            {
                ErrorOccurred?.Invoke(this, $"Parse error: {ex.Message}");
            }
        }

        private void HandleError(JsonElement root)
        {
            if (root.TryGetProperty("error", out var error))
            {
                string? errorMessage = null;
                
                if (error.TryGetProperty("message", out var message))
                {
                    errorMessage = message.GetString();
                }
                else if (error.ValueKind == JsonValueKind.String)
                {
                    errorMessage = error.GetString();
                }

                ErrorOccurred?.Invoke(this, errorMessage ?? "Unknown error");
            }
        }

        private void HandleAISuggestionStart(JsonElement root)
        {
            string question = "";
            if (root.TryGetProperty("question", out var qElem) && qElem.ValueKind == JsonValueKind.String)
                question = qElem.GetString() ?? "";
            AISuggestionStarted?.Invoke(this, question);
        }

        private void HandleAISuggestionToken(JsonElement root)
        {
            if (root.TryGetProperty("token", out var tokenElem) && tokenElem.ValueKind == JsonValueKind.String)
            {
                string token = tokenElem.GetString() ?? "";
                AISuggestionToken?.Invoke(this, token);
            }
        }

        /// <summary>
        /// Disconnect from WebSocket
        /// </summary>
        private readonly object disconnectLock = new object();
        private bool isDisconnecting = false;
        
        public async Task DisconnectAsync()
        {
            // Thread-safe guard to prevent double disconnect
            lock (disconnectLock)
            {
                if (!isConnected || isDisconnecting)
                    return;
                isDisconnecting = true;
            }

            isConnected = false;

            try
            {
                cancellationTokenSource?.Cancel();

                if (webSocket != null && webSocket.State == WebSocketState.Open)
                {
                    await webSocket.CloseAsync(
                        WebSocketCloseStatus.NormalClosure,
                        "Client disconnecting",
                        CancellationToken.None
                    );
                }
            }
            catch
            {
                // Ignore errors during disconnect
            }
            finally
            {
                webSocket?.Dispose();
                webSocket = null;
                cancellationTokenSource?.Dispose();
                cancellationTokenSource = null;
                isDisconnecting = false;

                Disconnected?.Invoke(this, EventArgs.Empty);
            }
        }

        /// <summary>
        /// Clear conversation history
        /// </summary>
        public void ClearConversation()
        {
            conversation.Clear();
        }

        /// <summary>
        /// Non-blocking dispose. The WebSocket close is fired as a 1-second background task
        /// so the caller (typically the UI thread during window close) never stalls.
        /// </summary>
        public void Dispose()
        {
            try
            {
                isConnected = false;
                cancellationTokenSource?.Cancel();

                var ws = webSocket;
                if (ws != null && ws.State == WebSocketState.Open)
                {
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            using var closeCts = new CancellationTokenSource(TimeSpan.FromSeconds(1));
                            await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "Disposing", closeCts.Token)
                                    .ConfigureAwait(false);
                        }
                        catch { /* ignore */ }
                        finally { try { ws.Dispose(); } catch { } }
                    });
                    webSocket = null;
                }
                else
                {
                    webSocket?.Dispose();
                    webSocket = null;
                }
            }
            catch
            {
                // Ignore errors during dispose
            }
            finally
            {
                cancellationTokenSource?.Dispose();
                cancellationTokenSource = null;
            }
        }
    }
}

