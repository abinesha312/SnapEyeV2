using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;

namespace SnapEye.Services
{
    /// <summary>
    /// Screen capture service for capturing screenshots and sending them to the backend OCR/AI.
    /// Supports full-screen capture and region-based capture.
    /// </summary>
    public class ScreenCaptureService : IDisposable
    {
        private readonly HttpClient httpClient;
        private readonly string backendUrl;
        private string? authToken;

        public event EventHandler<string>? CaptureCompleted;
        public event EventHandler<string>? OcrResultReceived;
        public event EventHandler<string>? ErrorOccurred;

        public ScreenCaptureService(string backendUrl)
        {
            this.backendUrl = backendUrl;
            httpClient = new HttpClient
            {
                BaseAddress = new Uri(backendUrl),
                Timeout = TimeSpan.FromSeconds(30)
            };
        }

        public void SetAuthToken(string token)
        {
            authToken = token;
            httpClient.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", token);
        }

        /// <summary>
        /// Capture the entire primary screen
        /// </summary>
        public async Task<string?> CaptureFullScreenAsync()
        {
            try
            {
                int width = (int)SystemParameters.PrimaryScreenWidth;
                int height = (int)SystemParameters.PrimaryScreenHeight;

                return await CaptureRegionAsync(0, 0, width, height).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                ErrorOccurred?.Invoke(this, $"Full screen capture error: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Capture a specific screen region. The bitmap allocation, GDI copy, PNG encode,
        /// and Base64 conversion all run on a thread-pool worker so the UI thread stays
        /// responsive — for a 1080p capture this work can take 300–800 ms which is more
        /// than enough to trigger Windows' "Not responding" dialog if done inline.
        /// </summary>
        public Task<string?> CaptureRegionAsync(int x, int y, int width, int height)
        {
            return Task.Run<string?>(() =>
            {
                try
                {
                    using var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
                    using (var graphics = Graphics.FromImage(bitmap))
                    {
                        graphics.CopyFromScreen(x, y, 0, 0, new System.Drawing.Size(width, height));
                    }

                    string base64 = BitmapToBase64(bitmap);
                    CaptureCompleted?.Invoke(this, $"Captured {width}x{height} region");
                    return base64;
                }
                catch (Exception ex)
                {
                    ErrorOccurred?.Invoke(this, $"Region capture error: {ex.Message}");
                    return null;
                }
            });
        }

        /// <summary>
        /// Capture screen and send directly to backend OCR endpoint
        /// </summary>
        public async Task<string?> CaptureAndOcrAsync()
        {
            try
            {
                string? base64Image = await CaptureFullScreenAsync().ConfigureAwait(false);
                if (string.IsNullOrEmpty(base64Image))
                    return null;

                return await SendToOcrAsync(base64Image).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                ErrorOccurred?.Invoke(this, $"Capture and OCR error: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Send a base64-encoded image to the backend OCR endpoint. The base64 payload
        /// for a full screen is several megabytes, so the JSON serialize + StringContent
        /// allocation runs on a worker thread to keep the dispatcher responsive.
        /// </summary>
        public async Task<string?> SendToOcrAsync(string base64Image)
        {
            try
            {
                // Build the request body off the UI thread. Serializing 5–10 MB of base64
                // text into JSON can stall the dispatcher long enough to trigger Windows'
                // "Not responding" watchdog if done inline.
                var content = await Task.Run(() =>
                {
                    var payload = new
                    {
                        image = base64Image,
                        format = "png"
                    };
                    string json = JsonSerializer.Serialize(payload);
                    return new StringContent(json, Encoding.UTF8, "application/json");
                }).ConfigureAwait(false);

                var response = await httpClient.PostAsync("/api/ocr/extract", content).ConfigureAwait(false);

                string responseBody = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

                if (!response.IsSuccessStatusCode)
                {
                    string friendlyError = ExtractErrorMessage(responseBody)
                        ?? $"OCR request failed: {(int)response.StatusCode} {response.StatusCode}";
                    ErrorOccurred?.Invoke(this, friendlyError);
                    return null;
                }

                using var doc = JsonDocument.Parse(responseBody);
                var root = doc.RootElement;

                string extractedText = "";
                if (root.TryGetProperty("text", out var textElement))
                {
                    extractedText = textElement.GetString() ?? "";
                }

                OcrResultReceived?.Invoke(this, extractedText);
                return extractedText;
            }
            catch (Exception ex)
            {
                ErrorOccurred?.Invoke(this, $"OCR send error: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Try to pull a human-readable message out of the backend's JSON error body
        /// (shape: {"error": "..."}). Returns null if the body isn't JSON or has no
        /// recognisable error field, so the caller can fall back to a generic message.
        /// </summary>
        private static string? ExtractErrorMessage(string? body)
        {
            if (string.IsNullOrWhiteSpace(body))
                return null;

            try
            {
                using var doc = JsonDocument.Parse(body);
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object)
                    return null;

                if (root.TryGetProperty("error", out var err) && err.ValueKind == JsonValueKind.String)
                {
                    var msg = err.GetString();
                    if (!string.IsNullOrWhiteSpace(msg))
                        return msg;
                }

                if (root.TryGetProperty("detail", out var detail) && detail.ValueKind == JsonValueKind.String)
                {
                    var msg = detail.GetString();
                    if (!string.IsNullOrWhiteSpace(msg))
                        return msg;
                }
            }
            catch (JsonException)
            {
                // Not JSON — caller will use the fallback message.
            }

            return null;
        }

        /// <summary>
        /// Send a base64-encoded image to the backend for AI analysis (GPT-4 Vision)
        /// </summary>
        public async Task<string?> SendToAiAnalysisAsync(string base64Image, string query = "Describe what you see on screen")
        {
            try
            {
                var content = await Task.Run(() =>
                {
                    var payload = new
                    {
                        image = base64Image,
                        query = query,
                        use_ai = true
                    };
                    string json = JsonSerializer.Serialize(payload);
                    return new StringContent(json, Encoding.UTF8, "application/json");
                }).ConfigureAwait(false);

                var response = await httpClient.PostAsync("/api/ocr/extract", content).ConfigureAwait(false);

                if (!response.IsSuccessStatusCode)
                {
                    ErrorOccurred?.Invoke(this, $"AI analysis failed: {response.StatusCode}");
                    return null;
                }

                string responseBody = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                using var doc = JsonDocument.Parse(responseBody);
                var root = doc.RootElement;

                if (root.TryGetProperty("analysis", out var analysisElement))
                    return analysisElement.GetString();
                if (root.TryGetProperty("text", out var textElement))
                    return textElement.GetString();

                return responseBody;
            }
            catch (Exception ex)
            {
                ErrorOccurred?.Invoke(this, $"AI analysis error: {ex.Message}");
                return null;
            }
        }

        private static string BitmapToBase64(Bitmap bitmap)
        {
            using var ms = new MemoryStream();
            bitmap.Save(ms, ImageFormat.Png);
            return Convert.ToBase64String(ms.ToArray());
        }

        public void Dispose()
        {
            httpClient?.Dispose();
        }
    }
}
