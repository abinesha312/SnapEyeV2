using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace SnapEye.Config
{
    /// <summary>
    /// Application configuration loaded from config.yaml with environment variable overrides.
    /// All settings are centralized here for fail-safe access.
    /// </summary>
    public static class AppConfig
    {
        // === Backend ===
        public static string BackendHttpUrl { get; set; } = "http://localhost:8080";
        public static string BackendWebSocketUrl { get; set; } = "ws://localhost:8080";

        // === Auth ===
        public static string DefaultUsername { get; set; } = "snapeye_user";
        public static string DefaultApiKey { get; set; } = "sk-test-api-key-1234567890";
        public static int SessionExpiryDays { get; set; } = 90;

        // === Audio ===
        public static int AudioBufferSize { get; set; } = 4096;
        public static int AudioSampleRate { get; set; } = 24000;
        public static int AudioChannels { get; set; } = 1;

        // === Transcription ===
        public static string DeepgramModel { get; set; } = "nova-3";
        public static string DeepgramLanguage { get; set; } = "en";
        public static double PauseThreshold { get; set; } = 0.5;
        public static int EndpointingMs { get; set; } = 500;

        // === UI ===
        public static int WindowWidth { get; set; } = 380;
        public static int WindowHeight { get; set; } = 620;
        public static double DefaultOpacity { get; set; } = 0.95;
        public static double MinOpacity { get; set; } = 0.3;
        public static double MaxOpacity { get; set; } = 1.0;
        public static int CornerRadius { get; set; } = 16;
        public static bool InvisibleToCapture { get; set; } = true;
        public static string AccentColor { get; set; } = "#2563EB";
        public static string AccentColorLight { get; set; } = "#60A5FA";
        public static string BackgroundPrimary { get; set; } = "#27272A";
        public static string BackgroundSecondary { get; set; } = "#3F3F46";
        public static string BackgroundCard { get; set; } = "#3F3F46";
        public static string TextPrimary { get; set; } = "#FFFFFF";
        public static string TextSecondary { get; set; } = "#A1A1AA";
        public static string TextMuted { get; set; } = "#71717A";
        public static string BorderColor { get; set; } = "#3F3F46";
        public static string SuccessColor { get; set; } = "#22C55E";
        public static string ErrorColor { get; set; } = "#EF4444";
        public static string WarningColor { get; set; } = "#F59E0B";

        // === Modes ===
        public static List<ModeConfig> Modes { get; set; } = new();

        // === Quick Actions ===
        public static List<QuickActionConfig> QuickActions { get; set; } = new();

        // === Keyboard Shortcuts ===
        public static string ShortcutToggleListen { get; set; } = "Ctrl+Shift+L";
        public static string ShortcutScreenshot { get; set; } = "Ctrl+Shift+S";
        public static string ShortcutHideShow { get; set; } = "Ctrl+Shift+H";
        public static string ShortcutAskAi { get; set; } = "Ctrl+Enter";

        /// <summary>
        /// Load configuration from config.yaml with environment variable overrides.
        /// Fail-safe: uses defaults if YAML file is missing or malformed.
        /// </summary>
        public static void LoadConfiguration()
        {
            try
            {
                LoadFromYaml();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Config] YAML load failed, using defaults: {ex.Message}");
            }

            // Environment variables always override YAML values
            ApplyEnvironmentOverrides();
            LoadDefaultModes();
            LoadDefaultQuickActions();

            Console.WriteLine($"[Config] Loaded: backend={BackendHttpUrl}, model={DeepgramModel}, modes={Modes.Count}, actions={QuickActions.Count}");
        }

        private static void LoadFromYaml()
        {
            string yamlPath = FindConfigFile();
            if (string.IsNullOrEmpty(yamlPath))
            {
                Console.WriteLine("[Config] config.yaml not found, using defaults");
                return;
            }

            string yamlContent = File.ReadAllText(yamlPath);
            var deserializer = new DeserializerBuilder()
                .WithNamingConvention(UnderscoredNamingConvention.Instance)
                .IgnoreUnmatchedProperties()
                .Build();

            var config = deserializer.Deserialize<Dictionary<string, object>>(yamlContent);
            if (config == null) return;

            // Backend
            if (config.TryGetValue("backend", out var backendObj) && backendObj is Dictionary<object, object> backend)
            {
                BackendHttpUrl = GetString(backend, "http_url", BackendHttpUrl);
                BackendWebSocketUrl = GetString(backend, "websocket_url", BackendWebSocketUrl);
            }

            // Auth
            if (config.TryGetValue("auth", out var authObj) && authObj is Dictionary<object, object> auth)
            {
                DefaultUsername = GetString(auth, "default_username", DefaultUsername);
                DefaultApiKey = GetString(auth, "default_api_key", DefaultApiKey);
                SessionExpiryDays = GetInt(auth, "session_expiry_days", SessionExpiryDays);
            }

            // Audio
            if (config.TryGetValue("audio", out var audioObj) && audioObj is Dictionary<object, object> audio)
            {
                AudioBufferSize = GetInt(audio, "buffer_size", AudioBufferSize);
                AudioSampleRate = GetInt(audio, "sample_rate", AudioSampleRate);
                AudioChannels = GetInt(audio, "channels", AudioChannels);
            }

            // Transcription
            if (config.TryGetValue("transcription", out var txObj) && txObj is Dictionary<object, object> tx)
            {
                DeepgramModel = GetString(tx, "model", DeepgramModel);
                DeepgramLanguage = GetString(tx, "language", DeepgramLanguage);
                PauseThreshold = GetDouble(tx, "pause_threshold", PauseThreshold);
                EndpointingMs = GetInt(tx, "endpointing_ms", EndpointingMs);
            }

            // UI
            if (config.TryGetValue("ui", out var uiObj) && uiObj is Dictionary<object, object> ui)
            {
                WindowWidth = GetInt(ui, "width", WindowWidth);
                WindowHeight = GetInt(ui, "height", WindowHeight);
                DefaultOpacity = GetDouble(ui, "opacity", DefaultOpacity);
                MinOpacity = GetDouble(ui, "min_opacity", MinOpacity);
                MaxOpacity = GetDouble(ui, "max_opacity", MaxOpacity);
                CornerRadius = GetInt(ui, "corner_radius", CornerRadius);
                InvisibleToCapture = GetBool(ui, "invisible_to_capture", InvisibleToCapture);
                AccentColor = GetString(ui, "accent_color", AccentColor);
                AccentColorLight = GetString(ui, "accent_color_light", AccentColorLight);
                BackgroundPrimary = GetString(ui, "background_primary", BackgroundPrimary);
                BackgroundSecondary = GetString(ui, "background_secondary", BackgroundSecondary);
                BackgroundCard = GetString(ui, "background_card", BackgroundCard);
                TextPrimary = GetString(ui, "text_primary", TextPrimary);
                TextSecondary = GetString(ui, "text_secondary", TextSecondary);
                TextMuted = GetString(ui, "text_muted", TextMuted);
                BorderColor = GetString(ui, "border_color", BorderColor);
                SuccessColor = GetString(ui, "success_color", SuccessColor);
                ErrorColor = GetString(ui, "error_color", ErrorColor);
                WarningColor = GetString(ui, "warning_color", WarningColor);
            }

            // Modes
            if (config.TryGetValue("modes", out var modesObj) && modesObj is List<object> modesList)
            {
                Modes.Clear();
                foreach (var item in modesList)
                {
                    if (item is Dictionary<object, object> modeDict)
                    {
                        Modes.Add(new ModeConfig
                        {
                            Name = GetString(modeDict, "name", "Unnamed"),
                            Icon = GetString(modeDict, "icon", "brain"),
                            SystemPrompt = GetString(modeDict, "system_prompt", ""),
                        });
                    }
                }
            }

            // Quick Actions
            if (config.TryGetValue("quick_actions", out var qaObj) && qaObj is List<object> qaList)
            {
                QuickActions.Clear();
                foreach (var item in qaList)
                {
                    if (item is Dictionary<object, object> qaDict)
                    {
                        QuickActions.Add(new QuickActionConfig
                        {
                            Label = GetString(qaDict, "label", "Action"),
                            Prompt = GetString(qaDict, "prompt", ""),
                        });
                    }
                }
            }

            // Keyboard Shortcuts
            if (config.TryGetValue("keyboard_shortcuts", out var ksObj) && ksObj is Dictionary<object, object> ks)
            {
                ShortcutToggleListen = GetString(ks, "toggle_listen", ShortcutToggleListen);
                ShortcutScreenshot = GetString(ks, "screenshot", ShortcutScreenshot);
                ShortcutHideShow = GetString(ks, "hide_show", ShortcutHideShow);
                ShortcutAskAi = GetString(ks, "ask_ai", ShortcutAskAi);
            }

            Console.WriteLine($"[Config] Loaded from: {yamlPath}");
        }

        private static void LoadDefaultModes()
        {
            if (Modes.Count > 0) return;
            Modes.AddRange(new[]
            {
                new ModeConfig { Name = "General Assistant", Icon = "brain", SystemPrompt = "You are SnapEye, a helpful AI meeting assistant." },
                new ModeConfig { Name = "Interview Mode", Icon = "briefcase", SystemPrompt = "Help the user during a job interview." },
                new ModeConfig { Name = "Sales Mode", Icon = "chart", SystemPrompt = "Help the user during a sales call." },
                new ModeConfig { Name = "Meeting Notes", Icon = "clipboard", SystemPrompt = "Summarize key points and action items." },
            });
        }

        private static void LoadDefaultQuickActions()
        {
            if (QuickActions.Count > 0) return;
            QuickActions.AddRange(new[]
            {
                new QuickActionConfig { Label = "What should I say?", Prompt = "Based on the conversation transcript, suggest what I should say next. Be specific and actionable." },
                new QuickActionConfig { Label = "Follow up", Prompt = "Based on the conversation transcript, suggest 2-3 follow-up questions I could ask." },
                new QuickActionConfig { Label = "Fact check", Prompt = "Review the conversation transcript and verify any facts or claims that were mentioned. Highlight anything that may need checking." },
                new QuickActionConfig { Label = "Who am I talking to?", Prompt = "Based on the conversation transcript, identify who I am talking to and any details about them." },
                new QuickActionConfig { Label = "Recap", Prompt = "Provide a brief recap of the conversation so far, including key topics discussed and any decisions made." },
            });
        }

        private static void ApplyEnvironmentOverrides()
        {
            BackendHttpUrl = Environment.GetEnvironmentVariable("SNAPEYE_BACKEND_HTTP") ?? BackendHttpUrl;
            BackendWebSocketUrl = Environment.GetEnvironmentVariable("SNAPEYE_BACKEND_WS") ?? BackendWebSocketUrl;
            DefaultUsername = Environment.GetEnvironmentVariable("SNAPEYE_USERNAME") ?? DefaultUsername;
            DefaultApiKey = Environment.GetEnvironmentVariable("SNAPEYE_API_KEY") ?? DefaultApiKey;
            DeepgramModel = Environment.GetEnvironmentVariable("DEEPGRAM_MODEL") ?? DeepgramModel;
            DeepgramLanguage = Environment.GetEnvironmentVariable("DEEPGRAM_LANGUAGE") ?? DeepgramLanguage;

            if (int.TryParse(Environment.GetEnvironmentVariable("AUDIO_SAMPLE_RATE"), out int sr))
                AudioSampleRate = sr;
            if (double.TryParse(Environment.GetEnvironmentVariable("PAUSE_THRESHOLD"), NumberStyles.Float, CultureInfo.InvariantCulture, out double pt))
                PauseThreshold = pt;
        }

        private static string FindConfigFile()
        {
            string[] searchPaths = new[]
            {
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "config", "config.yaml"),
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "config.yaml"),
                Path.Combine(Directory.GetCurrentDirectory(), "config", "config.yaml"),
                Path.Combine(Directory.GetCurrentDirectory(), "config.yaml"),
            };

            foreach (var path in searchPaths)
            {
                if (File.Exists(path))
                    return path;
            }
            return "";
        }

        // === Helpers ===

        private static string GetString(Dictionary<object, object> dict, string key, string fallback)
        {
            if (dict.TryGetValue(key, out var val) && val != null)
                return val.ToString() ?? fallback;
            return fallback;
        }

        private static int GetInt(Dictionary<object, object> dict, string key, int fallback)
        {
            if (dict.TryGetValue(key, out var val) && val != null)
            {
                if (int.TryParse(val.ToString(), out int result))
                    return result;
            }
            return fallback;
        }

        private static double GetDouble(Dictionary<object, object> dict, string key, double fallback)
        {
            if (dict.TryGetValue(key, out var val) && val != null)
            {
                if (double.TryParse(val.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out double result))
                    return result;
            }
            return fallback;
        }

        private static bool GetBool(Dictionary<object, object> dict, string key, bool fallback)
        {
            if (dict.TryGetValue(key, out var val) && val != null)
            {
                string str = val.ToString()?.ToLower() ?? "";
                if (str == "true" || str == "1" || str == "yes") return true;
                if (str == "false" || str == "0" || str == "no") return false;
            }
            return fallback;
        }
    }

    /// <summary>Configuration for an AI mode (Interview, Sales, etc.)</summary>
    public class ModeConfig
    {
        public string Name { get; set; } = "";
        public string Icon { get; set; } = "brain";
        public string SystemPrompt { get; set; } = "";
    }

    /// <summary>Configuration for a quick action button</summary>
    public class QuickActionConfig
    {
        public string Label { get; set; } = "";
        public string Prompt { get; set; } = "";
    }
}
