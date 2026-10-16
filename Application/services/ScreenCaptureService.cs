using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;

namespace SnapEye.Services
{
    /// <summary>
    /// Screen capture service for capturing screenshots and sending them to the backend OCR/AI.
    /// Captures the active window (falls back to the monitor hosting the SnapEye overlay),
    /// and always saves a local PNG under %AppData%\SnapEye\captures.
    /// </summary>
    public class ScreenCaptureService : IDisposable
    {
        private const uint MONITOR_DEFAULTTONEAREST = 2;

        private static readonly string CapturesDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "SnapEye",
            "captures");

        private readonly HttpClient httpClient;
        private readonly string backendUrl;
        private string? authToken;

        public event EventHandler<string>? CaptureCompleted;
        public event EventHandler<string>? OcrResultReceived;
        public event EventHandler<string>? ErrorOccurred;

        /// <summary>Folder where capture PNGs (and OCR .txt sidecars) are written.</summary>
        public static string LocalCapturesPath => CapturesDirectory;

        /// <summary>Full path of the most recent locally saved capture, if any.</summary>
        public string? LastSavedCapturePath { get; private set; }

        /// <summary>Base64 PNG of the most recent capture, reused to send the image to the model.</summary>
        public string? LastCaptureBase64 { get; private set; }

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

        private const int SM_CXSCREEN = 0;
        private const int SM_CYSCREEN = 1;

        [DllImport("user32.dll")]
        private static extern int GetSystemMetrics(int nIndex);

        [DllImport("user32.dll")]
        private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        [DllImport("dwmapi.dll")]
        private static extern int DwmGetWindowAttribute(IntPtr hwnd, int dwAttribute, out RECT pvAttribute, int cbAttribute);

        private const int DWMWA_EXTENDED_FRAME_BOUNDS = 9;

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        private struct MONITORINFO
        {
            public int cbSize;
            public RECT rcMonitor;
            public RECT rcWork;
            public uint dwFlags;
        }

        /// <summary>
        /// Capture the entire primary screen (legacy helper). Prefer
        /// <see cref="CaptureWindowMonitorAsync"/> when an overlay window is available.
        /// </summary>
        public Task<string?> CaptureFullScreenAsync()
        {
            try
            {
                var (x, y, width, height) = PrimaryMonitorPixels();
                return CaptureRegionAsync(x, y, width, height, saveLocally: true);
            }
            catch (Exception ex)
            {
                ErrorOccurred?.Invoke(this, $"Full screen capture error: {ex.Message}");
                return Task.FromResult<string?>(null);
            }
        }

        /// <summary>
        /// Capture the physical monitor that currently hosts <paramref name="window"/>.
        /// Falls back to the primary display if the HWND or monitor cannot be resolved.
        /// </summary>
        public Task<string?> CaptureWindowMonitorAsync(Window? window)
        {
            try
            {
                var bounds = ResolveMonitorBounds(window);
                return CaptureRegionAsync(bounds.X, bounds.Y, bounds.Width, bounds.Height, saveLocally: true);
            }
            catch (Exception ex)
            {
                ErrorOccurred?.Invoke(this, $"Monitor capture error: {ex.Message}");
                return Task.FromResult<string?>(null);
            }
        }

        /// <summary>
        /// Capture only the foreground (active) window, clamped to its monitor's work area.
        /// Falls back to the monitor hosting <paramref name="snapEyeWindow"/> when there is no
        /// usable foreground window or it belongs to SnapEye.
        /// </summary>
        public Task<string?> CaptureActiveWindowAsync(Window? snapEyeWindow)
        {
            try
            {
                var bounds = ResolveActiveWindowBounds();
                if (bounds.HasValue)
                {
                    var b = bounds.Value;
                    return CaptureRegionAsync(b.X, b.Y, b.Width, b.Height, saveLocally: true);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ScreenCapture] Active window fallback: {ex.Message}");
            }
            return CaptureWindowMonitorAsync(snapEyeWindow);
        }

        /// <summary>
        /// Capture a specific screen region. The bitmap allocation, GDI copy, PNG encode,
        /// and Base64 conversion all run on a thread-pool worker so the UI thread stays
        /// responsive — for a 1080p capture this work can take 300–800 ms which is more
        /// than enough to trigger Windows' "Not responding" dialog if done inline.
        /// </summary>
        public Task<string?> CaptureRegionAsync(int x, int y, int width, int height, bool saveLocally = true)
        {
            return Task.Run<string?>(() =>
            {
                try
                {
                    LastCaptureBase64 = null;
                    if (width <= 0 || height <= 0)
                    {
                        ErrorOccurred?.Invoke(this, $"Invalid capture size: {width}x{height}");
                        return null;
                    }

                    using var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
                    using (var graphics = Graphics.FromImage(bitmap))
                    {
                        graphics.CopyFromScreen(x, y, 0, 0, new System.Drawing.Size(width, height));
                    }

                    if (saveLocally)
                        LastSavedCapturePath = SaveBitmapLocally(bitmap);

                    string base64 = BitmapToBase64(bitmap);
                    LastCaptureBase64 = base64;
                    CaptureCompleted?.Invoke(this,
                        $"Captured {width}x{height} at ({x},{y})"
                        + (LastSavedCapturePath != null ? $" → {LastSavedCapturePath}" : ""));
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
        /// Capture the monitor hosting <paramref name="window"/> and send it to backend OCR.
        /// The PNG is always saved under %AppData%\SnapEye\captures first.
        /// </summary>
        public async Task<string?> CaptureAndOcrAsync(Window? window = null)
        {
            try
            {
                string? base64Image = await CaptureActiveWindowAsync(window).ConfigureAwait(false);
                if (string.IsNullOrEmpty(base64Image))
                    return null;

                string? ocrText = await SendToOcrAsync(base64Image).ConfigureAwait(false);
                if (!string.IsNullOrEmpty(ocrText) && !string.IsNullOrEmpty(LastSavedCapturePath))
                    TrySaveOcrSidecar(LastSavedCapturePath, ocrText);

                return ocrText;
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

        private static (int X, int Y, int Width, int Height)? ResolveActiveWindowBounds()
        {
            IntPtr fg = GetForegroundWindow();
            if (fg == IntPtr.Zero) return null;

            GetWindowThreadProcessId(fg, out uint pid);
            if (pid == (uint)Environment.ProcessId) return null;

            // Extended frame bounds exclude the invisible resize borders Windows adds around windows.
            if (DwmGetWindowAttribute(fg, DWMWA_EXTENDED_FRAME_BOUNDS, out RECT r, Marshal.SizeOf<RECT>()) != 0
                && !GetWindowRect(fg, out r))
                return null;

            IntPtr monitor = MonitorFromWindow(fg, MONITOR_DEFAULTTONEAREST);
            var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
            if (monitor != IntPtr.Zero && GetMonitorInfo(monitor, ref info))
            {
                r.Left = Math.Max(r.Left, info.rcWork.Left);
                r.Top = Math.Max(r.Top, info.rcWork.Top);
                r.Right = Math.Min(r.Right, info.rcWork.Right);
                r.Bottom = Math.Min(r.Bottom, info.rcWork.Bottom);
            }

            int w = r.Right - r.Left;
            int h = r.Bottom - r.Top;
            if (w < 50 || h < 50) return null;
            return (r.Left, r.Top, w, h);
        }

        private static (int X, int Y, int Width, int Height) ResolveMonitorBounds(Window? window)
        {
            try
            {
                IntPtr hwnd = IntPtr.Zero;
                if (window != null)
                {
                    var helper = new WindowInteropHelper(window);
                    hwnd = helper.Handle;
                    if (hwnd == IntPtr.Zero)
                    {
                        helper.EnsureHandle();
                        hwnd = helper.Handle;
                    }
                }

                if (hwnd != IntPtr.Zero)
                {
                    IntPtr monitor = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
                    if (monitor != IntPtr.Zero)
                    {
                        var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
                        if (GetMonitorInfo(monitor, ref info))
                        {
                            int x = info.rcMonitor.Left;
                            int y = info.rcMonitor.Top;
                            int w = info.rcMonitor.Right - info.rcMonitor.Left;
                            int h = info.rcMonitor.Bottom - info.rcMonitor.Top;
                            if (w > 0 && h > 0)
                                return (x, y, w, h);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ScreenCapture] Monitor resolve fallback: {ex.Message}");
            }

            return PrimaryMonitorPixels();
        }

        /// <summary>
        /// Primary monitor in physical pixels. <see cref="SystemParameters.PrimaryScreenWidth"/>
        /// is in DIPs, which crops the capture whenever Windows display scaling is above 100%.
        /// </summary>
        private static (int X, int Y, int Width, int Height) PrimaryMonitorPixels()
        {
            int w = GetSystemMetrics(SM_CXSCREEN);
            int h = GetSystemMetrics(SM_CYSCREEN);
            if (w > 0 && h > 0)
                return (0, 0, w, h);
            return (0, 0, (int)SystemParameters.PrimaryScreenWidth, (int)SystemParameters.PrimaryScreenHeight);
        }

        private static string? SaveBitmapLocally(Bitmap bitmap)
        {
            try
            {
                Directory.CreateDirectory(CapturesDirectory);
                string fileName = $"capture_{DateTime.Now:yyyyMMdd_HHmmss_fff}.png";
                string path = Path.Combine(CapturesDirectory, fileName);
                bitmap.Save(path, ImageFormat.Png);
                Console.WriteLine($"[ScreenCapture] Saved locally: {path}");
                return path;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ScreenCapture] Local save failed: {ex.Message}");
                return null;
            }
        }

        private static void TrySaveOcrSidecar(string pngPath, string ocrText)
        {
            try
            {
                string txtPath = Path.ChangeExtension(pngPath, ".txt");
                File.WriteAllText(txtPath, ocrText, Encoding.UTF8);
                Console.WriteLine($"[ScreenCapture] Saved OCR text: {txtPath}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ScreenCapture] OCR sidecar save failed: {ex.Message}");
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
