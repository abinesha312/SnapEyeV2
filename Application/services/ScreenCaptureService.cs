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

                return await CaptureRegionAsync(0, 0, width, height);
            }
            catch (Exception ex)
            {
                ErrorOccurred?.Invoke(this, $"Full screen capture error: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Capture a specific screen region
        /// </summary>
        public Task<string?> CaptureRegionAsync(int x, int y, int width, int height)
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
                return Task.FromResult<string?>(base64);
            }
            catch (Exception ex)
            {
                ErrorOccurred?.Invoke(this, $"Region capture error: {ex.Message}");
                return Task.FromResult<string?>(null);
            }
        }

        /// <summary>
        /// Capture screen and send directly to backend OCR endpoint
        /// </summary>
        public async Task<string?> CaptureAndOcrAsync()
        {
            try
            {
                string? base64Image = await CaptureFullScreenAsync();
                if (string.IsNullOrEmpty(base64Image))
                    return null;

                return await SendToOcrAsync(base64Image);
            }
            catch (Exception ex)
            {
                ErrorOccurred?.Invoke(this, $"Capture and OCR error: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Send a base64-encoded image to the backend OCR endpoint
        /// </summary>
        public async Task<string?> SendToOcrAsync(string base64Image)
        {
            try
            {
                var payload = new
                {
                    image = base64Image,
                    format = "png"
                };

                string json = JsonSerializer.Serialize(payload);
                var content = new StringContent(json, Encoding.UTF8, "application/json");

                var response = await httpClient.PostAsync("/api/ocr/extract", content);

                if (!response.IsSuccessStatusCode)
                {
                    string error = await response.Content.ReadAsStringAsync();
                    ErrorOccurred?.Invoke(this, $"OCR request failed: {response.StatusCode}");
                    return null;
                }

                string responseBody = await response.Content.ReadAsStringAsync();
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
        /// Send a base64-encoded image to the backend for AI analysis (GPT-4 Vision)
        /// </summary>
        public async Task<string?> SendToAiAnalysisAsync(string base64Image, string query = "Describe what you see on screen")
        {
            try
            {
                var payload = new
                {
                    image = base64Image,
                    query = query,
                    use_ai = true
                };

                string json = JsonSerializer.Serialize(payload);
                var content = new StringContent(json, Encoding.UTF8, "application/json");

                var response = await httpClient.PostAsync("/api/ocr/extract", content);

                if (!response.IsSuccessStatusCode)
                {
                    ErrorOccurred?.Invoke(this, $"AI analysis failed: {response.StatusCode}");
                    return null;
                }

                string responseBody = await response.Content.ReadAsStringAsync();
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
