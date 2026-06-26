using System;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace SnapEye.Services
{
    /// <summary>
    /// Authentication service for SnapEye backend API
    /// Handles login and token management
    /// </summary>
    public class AuthService : IDisposable
    {
        private readonly HttpClient httpClient;
        private readonly string baseUrl;
        private string? accessToken;
        private string? refreshToken;
        private int expiresIn;
        private DateTime tokenExpiry;

        public string? AccessToken 
        { 
            get => accessToken;
            set => accessToken = value;
        }
        public string? RefreshToken 
        { 
            get => refreshToken;
            set => refreshToken = value;
        }
        public int ExpiresIn => expiresIn;
        public bool IsAuthenticated => !string.IsNullOrEmpty(accessToken) && DateTime.Now < tokenExpiry;

        public event EventHandler<string>? ErrorOccurred;

        public AuthService(string backendUrl = "http://localhost:8080")
        {
            baseUrl = backendUrl;
            httpClient = new HttpClient
            {
                BaseAddress = new Uri(baseUrl),
                Timeout = TimeSpan.FromSeconds(30)
            };
        }

        /// <summary>
        /// Login to the backend API and obtain JWT token
        /// </summary>
        public async Task<bool> LoginAsync(string username, string apiKey)
        {
            try
            {
                var loginData = new
                {
                    username = username,
                    api_key = apiKey
                };

                string jsonContent = JsonSerializer.Serialize(loginData);
                var content = new StringContent(jsonContent, Encoding.UTF8, "application/json");

                var response = await httpClient.PostAsync("/auth/login", content).ConfigureAwait(false);

                if (!response.IsSuccessStatusCode)
                {
                    string errorContent = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                    ErrorOccurred?.Invoke(this, $"Login failed: {response.StatusCode} - {errorContent}");
                    return false;
                }

                string responseContent = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                using var doc = JsonDocument.Parse(responseContent);
                var root = doc.RootElement;

                if (root.TryGetProperty("access_token", out var tokenElement))
                {
                    accessToken = tokenElement.GetString();
                }

                if (root.TryGetProperty("refresh_token", out var refreshElement))
                {
                    refreshToken = refreshElement.GetString();
                }

                if (root.TryGetProperty("expires_in", out var expiresElement))
                {
                    expiresIn = expiresElement.GetInt32();
                    tokenExpiry = DateTime.Now.AddSeconds(expiresIn - 60); // 60 second buffer
                }
                else
                {
                    expiresIn = 3600; // Default 1 hour
                    tokenExpiry = DateTime.Now.AddHours(1);
                }

                return !string.IsNullOrEmpty(accessToken);
            }
            catch (Exception ex)
            {
                ErrorOccurred?.Invoke(this, $"Login error: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Verify if the current token is still valid
        /// </summary>
        public async Task<bool> VerifyTokenAsync()
        {
            if (string.IsNullOrEmpty(accessToken))
            {
                return false;
            }

            try
            {
                var request = new HttpRequestMessage(HttpMethod.Post, "/auth/verify");
                request.Headers.Add("Authorization", $"Bearer {accessToken}");

                var response = await httpClient.SendAsync(request).ConfigureAwait(false);
                return response.IsSuccessStatusCode;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Refresh the access token using the refresh token
        /// </summary>
        public async Task<bool> RefreshTokenAsync()
        {
            // Use refresh token if available, fall back to access token
            string? tokenToUse = !string.IsNullOrEmpty(refreshToken) ? refreshToken : accessToken;
            
            if (string.IsNullOrEmpty(tokenToUse))
            {
                ErrorOccurred?.Invoke(this, "No token available for refresh");
                return false;
            }

            try
            {
                var request = new HttpRequestMessage(HttpMethod.Post, "/auth/refresh");
                request.Headers.Add("Authorization", $"Bearer {tokenToUse}");

                var response = await httpClient.SendAsync(request).ConfigureAwait(false);

                if (!response.IsSuccessStatusCode)
                {
                    string errorContent = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                    ErrorOccurred?.Invoke(this, $"Token refresh failed: {response.StatusCode}");
                    return false;
                }

                string responseContent = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                using var doc = JsonDocument.Parse(responseContent);
                var root = doc.RootElement;

                // Update tokens
                if (root.TryGetProperty("access_token", out var tokenElement))
                {
                    accessToken = tokenElement.GetString();
                }

                if (root.TryGetProperty("refresh_token", out var refreshElement))
                {
                    refreshToken = refreshElement.GetString();
                }

                if (root.TryGetProperty("expires_in", out var expiresElement))
                {
                    expiresIn = expiresElement.GetInt32();
                    tokenExpiry = DateTime.Now.AddSeconds(expiresIn - 60);
                }

                return !string.IsNullOrEmpty(accessToken);
            }
            catch (Exception ex)
            {
                ErrorOccurred?.Invoke(this, $"Token refresh error: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Logout and clear token
        /// </summary>
        public async Task LogoutAsync()
        {
            if (!string.IsNullOrEmpty(accessToken))
            {
                try
                {
                    var request = new HttpRequestMessage(HttpMethod.Post, "/auth/logout");
                    request.Headers.Add("Authorization", $"Bearer {accessToken}");
                    await httpClient.SendAsync(request).ConfigureAwait(false);
                }
                catch
                {
                    // Ignore errors during logout
                }
            }

            accessToken = null;
            refreshToken = null;
            tokenExpiry = DateTime.MinValue;
        }

        /// <summary>
        /// Get authorization header value
        /// </summary>
        public string? GetAuthorizationHeader()
        {
            return !string.IsNullOrEmpty(accessToken) ? $"Bearer {accessToken}" : null;
        }

        /// <summary>
        /// Cheap reachability probe with a hard timeout. Used before kicking off the
        /// transcription WebSocket / long-running streams so the user gets a fast inline
        /// error ("Backend not reachable") instead of staring at a frozen UI for the
        /// full HttpClient.Timeout while a dead host is being awaited.
        /// </summary>
        public async Task<bool> IsBackendReachableAsync(int timeoutMs = 1500)
        {
            using var cts = new CancellationTokenSource(timeoutMs);
            try
            {
                // /health is a public endpoint that doesn't require auth. If a
                // deployment doesn't have it, any 4xx (e.g. 404) still proves the
                // server is up; only network failures / timeouts mean unreachable.
                var response = await httpClient.GetAsync("/health", cts.Token).ConfigureAwait(false);
                return true;
            }
            catch
            {
                return false;
            }
        }

        public void Dispose()
        {
            try { httpClient?.Dispose(); } catch { /* ignore */ }
        }
    }
}

