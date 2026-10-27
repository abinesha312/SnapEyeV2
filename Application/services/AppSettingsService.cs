using System;
using System.IO;
using System.Text.Json;
using SnapEye.Config;

namespace SnapEye.Services
{
    /// <summary>
    /// User-facing application preferences stored in
    /// <c>%APPDATA%\SnapEye\app_settings.json</c>. Overrides matching
    /// <see cref="AppConfig"/> defaults (e.g. mic on/off at Listen start).
    /// </summary>
    public static class AppSettingsService
    {
        private static readonly string AppDataPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "SnapEye");
        private static readonly string SettingsFilePath = Path.Combine(AppDataPath, "app_settings.json");

        private static readonly JsonSerializerOptions JsonOpts = new()
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        };

        private static AppUserSettings? _cache;
        private static readonly object _lock = new();

        public static AppUserSettings Load()
        {
            lock (_lock)
            {
                if (_cache != null) return _cache;

                try
                {
                    if (!File.Exists(SettingsFilePath))
                    {
                        _cache = new AppUserSettings();
                        return _cache;
                    }

                    string json = File.ReadAllText(SettingsFilePath);
                    _cache = JsonSerializer.Deserialize<AppUserSettings>(json, JsonOpts)
                             ?? new AppUserSettings();
                    return _cache;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[AppSettings] Load failed: {ex.Message}");
                    _cache = new AppUserSettings();
                    return _cache;
                }
            }
        }

        public static void Save(AppUserSettings settings)
        {
            if (settings == null) throw new ArgumentNullException(nameof(settings));
            try
            {
                if (!Directory.Exists(AppDataPath))
                    Directory.CreateDirectory(AppDataPath);

                string json = JsonSerializer.Serialize(settings, JsonOpts);
                string tmp = SettingsFilePath + ".tmp";
                File.WriteAllText(tmp, json);
                if (File.Exists(SettingsFilePath))
                    File.Replace(tmp, SettingsFilePath, null);
                else
                    File.Move(tmp, SettingsFilePath);

                lock (_lock) _cache = settings;
                ApplyToAppConfig(settings);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[AppSettings] Save failed: {ex.Message}");
            }
        }

        /// <summary>Push persisted prefs into <see cref="AppConfig"/> (call after config YAML load).</summary>
        public static void ApplyToAppConfig(AppUserSettings? settings = null)
        {
            settings ??= Load();
            // UI "Microphone On" means not muted when Listen starts.
            AppConfig.MicMutedByDefault = !settings.MicrophoneOnByDefault;
        }
    }

    public sealed class AppUserSettings
    {
        /// <summary>
        /// When true, Listen starts with the microphone unmuted (default).
        /// When false, Listen starts muted until the user unmutes.
        /// </summary>
        public bool MicrophoneOnByDefault { get; set; } = true;
    }
}
