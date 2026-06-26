using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using SnapEye.Models;

namespace SnapEye.Services
{
    /// <summary>
    /// Persists the user's AI provider/model choice and per-provider API keys to
    /// <c>%APPDATA%\SnapEye\ai_models.json</c>. API keys are encrypted with Windows DPAPI
    /// (CurrentUser scope) so the stored JSON is useless if copied to another machine or user.
    /// </summary>
    public static class AiModelsService
    {
        private static readonly string AppDataPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "SnapEye"
        );
        private static readonly string SettingsFilePath = Path.Combine(AppDataPath, "ai_models.json");

        // Extra entropy (not a secret — just prevents other apps from unprotecting our blobs by
        // accident). Pairs with ProtectedData.Protect/Unprotect calls below.
        private static readonly byte[] DpapiEntropy = System.Text.Encoding.UTF8.GetBytes("SnapEye.AiModels.v1");

        private const int CurrentSchemaVersion = 1;

        // In-memory cache so we don't hit the disk + DPAPI on every LLM stream. Both fields
        // are guarded by _cacheLock; Save() invalidates the decrypted-key cache so the next
        // GetActiveApiKeyDecrypted() picks up the new value.
        private static AiModelsSettings? _cache;
        private static readonly object _cacheLock = new();
        // Memoized DPAPI plaintext keyed by ciphertext, so we don't re-Unprotect the same blob
        // for every streaming request. Cleared on Save().
        private static readonly Dictionary<string, string> _decryptedKeyCache = new(StringComparer.Ordinal);

        /// <summary>
        /// Loads the persisted settings (cached). Returns a fresh empty instance on first run
        /// or when the file is missing/corrupt — never throws to callers.
        /// </summary>
        public static AiModelsSettings Load()
        {
            lock (_cacheLock)
            {
                if (_cache != null) return _cache;

                try
                {
                    if (!File.Exists(SettingsFilePath))
                    {
                        _cache = new AiModelsSettings();
                        return _cache;
                    }

                    string json = File.ReadAllText(SettingsFilePath);
                    var settings = JsonSerializer.Deserialize<AiModelsSettings>(json, JsonOpts)
                                   ?? new AiModelsSettings();
                    _cache = settings;
                    return _cache;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[AiModelsService] Load failed: {ex.Message}");
                    _cache = new AiModelsSettings();
                    return _cache;
                }
            }
        }

        /// <summary>
        /// Persists <paramref name="settings"/> to disk. Keys are already expected to be
        /// encrypted/protected within the <see cref="AiModelProviderChoice.ApiKeyProtected"/>
        /// field — use <see cref="ProtectApiKey"/> before passing them here.
        /// </summary>
        public static void Save(AiModelsSettings settings)
        {
            if (settings == null) throw new ArgumentNullException(nameof(settings));
            try
            {
                if (!Directory.Exists(AppDataPath))
                    Directory.CreateDirectory(AppDataPath);

                settings.Version = CurrentSchemaVersion;
                string json = JsonSerializer.Serialize(settings, JsonOpts);

                string tmp = SettingsFilePath + ".tmp";
                File.WriteAllText(tmp, json);
                if (File.Exists(SettingsFilePath))
                    File.Replace(tmp, SettingsFilePath, null);
                else
                    File.Move(tmp, SettingsFilePath);

                lock (_cacheLock)
                {
                    _cache = settings;
                    _decryptedKeyCache.Clear();
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[AiModelsService] Save failed: {ex.Message}");
                throw;
            }
        }

        // === Convenience getters used by the streaming service at request time ===

        public static string? GetActiveProvider()
        {
            var s = Load();
            return string.IsNullOrWhiteSpace(s.ActiveProvider) ? null : s.ActiveProvider;
        }

        public static string? GetActiveModel()
        {
            var s = Load();
            if (string.IsNullOrWhiteSpace(s.ActiveProvider)) return null;
            if (s.Selections.TryGetValue(s.ActiveProvider, out var sel))
                return string.IsNullOrWhiteSpace(sel.Model) ? null : sel.Model;
            return null;
        }

        public static string? GetActiveApiKeyDecrypted()
        {
            var s = Load();
            if (string.IsNullOrWhiteSpace(s.ActiveProvider)) return null;
            if (!s.Selections.TryGetValue(s.ActiveProvider, out var sel)) return null;
            return UnprotectApiKey(sel.ApiKeyProtected);
        }

        public static string? GetApiKeyDecrypted(string providerId)
        {
            var s = Load();
            if (!s.Selections.TryGetValue(providerId, out var sel)) return null;
            return UnprotectApiKey(sel.ApiKeyProtected);
        }

        // === DPAPI helpers ===

        /// <summary>
        /// Encrypts an API key with DPAPI (CurrentUser scope) and returns a base64 string safe
        /// to persist in JSON. Returns an empty string for empty input.
        /// </summary>
        public static string ProtectApiKey(string plainKey)
        {
            if (string.IsNullOrEmpty(plainKey)) return "";
            try
            {
                byte[] cipher = ProtectedData.Protect(
                    System.Text.Encoding.UTF8.GetBytes(plainKey),
                    DpapiEntropy,
                    DataProtectionScope.CurrentUser);
                return Convert.ToBase64String(cipher);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[AiModelsService] Protect failed: {ex.Message}");
                return "";
            }
        }

        /// <summary>
        /// Decrypts a DPAPI-protected base64 blob. Returns <c>null</c> if the blob is empty or
        /// unreadable on the current machine/user (treated as "no key").
        /// </summary>
        public static string? UnprotectApiKey(string? protectedBase64)
        {
            if (string.IsNullOrEmpty(protectedBase64)) return null;

            // Memoize. DPAPI Unprotect on its own is fast, but the StreamingResponseService
            // calls this on every request — burning microseconds per stream and putting the
            // cryptographic provider in the hot path. The cache is invalidated whenever the
            // user clicks Save in the AI Models view.
            lock (_cacheLock)
            {
                if (_decryptedKeyCache.TryGetValue(protectedBase64, out var cached))
                    return cached;
            }

            try
            {
                byte[] cipher = Convert.FromBase64String(protectedBase64);
                byte[] plain = ProtectedData.Unprotect(cipher, DpapiEntropy, DataProtectionScope.CurrentUser);
                string result = System.Text.Encoding.UTF8.GetString(plain);

                lock (_cacheLock)
                {
                    _decryptedKeyCache[protectedBase64] = result;
                }
                return result;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[AiModelsService] Unprotect failed: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Pings the backend's <c>/api/llm/test</c> endpoint with the given credentials and
        /// returns whether the provider accepted the key plus the round-trip latency.
        /// </summary>
        public static async Task<TestConnectionResult> TestConnectionAsync(
            string backendHttpUrl,
            string provider,
            string model,
            string apiKey,
            string? bearerToken = null)
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
            if (!string.IsNullOrWhiteSpace(bearerToken))
                http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", bearerToken);

            var payload = new
            {
                provider,
                model,
                api_key = apiKey,
            };

            try
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var resp = await http.PostAsJsonAsync(
                    new Uri(new Uri(backendHttpUrl.TrimEnd('/') + "/"), "api/llm/test"),
                    payload);
                sw.Stop();

                string body = await resp.Content.ReadAsStringAsync();
                if (!resp.IsSuccessStatusCode)
                {
                    return new TestConnectionResult(false, $"HTTP {(int)resp.StatusCode}: {body}", (int)sw.ElapsedMilliseconds);
                }

                try
                {
                    using var doc = JsonDocument.Parse(body);
                    bool ok = doc.RootElement.TryGetProperty("ok", out var okProp) && okProp.GetBoolean();
                    string? err = doc.RootElement.TryGetProperty("error", out var errProp) ? errProp.GetString() : null;
                    int latency = doc.RootElement.TryGetProperty("latency_ms", out var lat) ? lat.GetInt32() : (int)sw.ElapsedMilliseconds;
                    return new TestConnectionResult(ok, err, latency);
                }
                catch
                {
                    return new TestConnectionResult(true, null, (int)sw.ElapsedMilliseconds);
                }
            }
            catch (Exception ex)
            {
                return new TestConnectionResult(false, ex.Message, 0);
            }
        }

        private static readonly JsonSerializerOptions JsonOpts = new()
        {
            WriteIndented = true,
            PropertyNameCaseInsensitive = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };
    }

    /// <summary>Result payload returned by <see cref="AiModelsService.TestConnectionAsync"/>.</summary>
    public sealed record TestConnectionResult(bool Ok, string? Error, int LatencyMs);

    /// <summary>Root object persisted to <c>%APPDATA%\SnapEye\ai_models.json</c>.</summary>
    public sealed class AiModelsSettings
    {
        [JsonPropertyName("version")] public int Version { get; set; } = 1;
        [JsonPropertyName("activeProvider")] public string ActiveProvider { get; set; } = "";
        [JsonPropertyName("selections")] public Dictionary<string, AiModelProviderChoice> Selections { get; set; } = new();
    }

    /// <summary>Per-provider selection — the chosen model and the DPAPI-encrypted API key.</summary>
    public sealed class AiModelProviderChoice
    {
        [JsonPropertyName("model")] public string Model { get; set; } = "";
        /// <summary>Base64-encoded DPAPI ciphertext. Use <see cref="AiModelsService.UnprotectApiKey"/>.</summary>
        [JsonPropertyName("apiKeyProtected")] public string ApiKeyProtected { get; set; } = "";
    }
}
