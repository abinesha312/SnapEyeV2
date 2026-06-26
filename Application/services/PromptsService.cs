using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using SnapEye.Config;

namespace SnapEye.Services
{
    /// <summary>
    /// Persists user-customized AI mode prompts in <c>%APPDATA%\SnapEye\prompts.json</c>.
    /// The file is a plain (unencrypted) JSON document so power-users can inspect and back it
    /// up themselves. On first run the file is seeded from the built-in defaults that were
    /// loaded from <c>config.yaml</c> into <see cref="AppConfig.DefaultModes"/>.
    /// </summary>
    public static class PromptsService
    {
        private static readonly string AppDataPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "SnapEye"
        );
        private static readonly string PromptsFilePath = Path.Combine(AppDataPath, "prompts.json");

        private const int CurrentSchemaVersion = 1;

        // Lightweight in-memory cache so repeated Load() calls during overlay/dashboard
        // open and during streaming setup don't re-read prompts.json from disk every time.
        // Save / ResetToDefaults invalidate this so the next Load picks up new content.
        private static List<ModeConfig>? _cache;
        private static readonly object _cacheLock = new();

        /// <summary>
        /// True when the last <see cref="Load"/> call detected a corrupted prompts.json. The
        /// Dashboard surfaces this as an inline error with a "Reset to defaults" call-to-action.
        /// </summary>
        public static bool LastLoadFailed { get; private set; }
        public static string? LastLoadError { get; private set; }

        /// <summary>
        /// Load the user's saved modes. On first run (file missing) this seeds a copy of the
        /// built-in defaults captured in <see cref="AppConfig.DefaultModes"/> and writes it to
        /// disk so the file always exists after the first launch. On parse failure the returned
        /// list falls back to the built-in defaults but the broken file is left untouched so the
        /// user has a chance to recover it manually.
        /// </summary>
        public static List<ModeConfig> Load()
        {
            lock (_cacheLock)
            {
                if (_cache != null)
                    return CloneList(_cache);
            }

            LastLoadFailed = false;
            LastLoadError = null;

            List<ModeConfig> result;
            try
            {
                if (!File.Exists(PromptsFilePath))
                {
                    var seeded = CloneList(AppConfig.DefaultModes);
                    EnsureIdsAndOrder(seeded);
                    SaveInternal(seeded);
                    result = seeded;
                }
                else
                {
                    string json = File.ReadAllText(PromptsFilePath);
                    if (string.IsNullOrWhiteSpace(json))
                    {
                        var seeded = CloneList(AppConfig.DefaultModes);
                        EnsureIdsAndOrder(seeded);
                        SaveInternal(seeded);
                        result = seeded;
                    }
                    else
                    {
                        var envelope = JsonSerializer.Deserialize<PromptsFile>(json, JsonOpts);
                        if (envelope?.Modes == null || envelope.Modes.Count == 0)
                        {
                            var seeded = CloneList(AppConfig.DefaultModes);
                            EnsureIdsAndOrder(seeded);
                            SaveInternal(seeded);
                            result = seeded;
                        }
                        else
                        {
                            var modes = envelope.Modes.Select(m => m.ToModeConfig()).ToList();
                            EnsureIdsAndOrder(modes);
                            result = modes;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                LastLoadFailed = true;
                LastLoadError = ex.Message;
                Console.WriteLine($"[PromptsService] Load failed: {ex.Message}");
                return CloneList(AppConfig.DefaultModes);
            }

            lock (_cacheLock)
            {
                _cache = CloneList(result);
            }
            return result;
        }

        /// <summary>
        /// Clears the in-memory cache. Call this when the on-disk prompts.json has been
        /// edited externally (e.g. a future "import" feature) so the next Load picks up the
        /// new content. Save / ResetToDefaults already invalidate automatically.
        /// </summary>
        public static void InvalidateCache()
        {
            lock (_cacheLock) { _cache = null; }
        }

        /// <summary>
        /// Persist the provided modes to disk. Writes to a temp file first and then atomically
        /// replaces prompts.json to avoid partial writes if the process is killed mid-save.
        /// </summary>
        public static void Save(IEnumerable<ModeConfig> modes)
        {
            if (modes == null) throw new ArgumentNullException(nameof(modes));
            var list = modes.Select(m => m.Clone()).ToList();
            EnsureIdsAndOrder(list);
            SaveInternal(list);
            lock (_cacheLock) { _cache = CloneList(list); }
        }

        /// <summary>
        /// Overwrites the user file with a fresh copy of the built-in defaults and returns it.
        /// </summary>
        public static List<ModeConfig> ResetToDefaults()
        {
            var defaults = CloneList(AppConfig.DefaultModes);
            EnsureIdsAndOrder(defaults);
            SaveInternal(defaults);
            LastLoadFailed = false;
            LastLoadError = null;
            lock (_cacheLock) { _cache = CloneList(defaults); }
            return defaults;
        }

        /// <summary>
        /// Returns the built-in default <see cref="ModeConfig"/> (from config.yaml) that matches
        /// the given id, or <c>null</c> if the user created this mode themselves.
        /// </summary>
        public static ModeConfig? GetBuiltInById(string id)
        {
            if (string.IsNullOrEmpty(id)) return null;
            return AppConfig.DefaultModes.FirstOrDefault(m =>
                string.Equals(m.Id, id, StringComparison.OrdinalIgnoreCase))?.Clone();
        }

        // === Internals ===

        private static readonly JsonSerializerOptions JsonOpts = new()
        {
            WriteIndented = true,
            PropertyNameCaseInsensitive = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };

        private static void SaveInternal(List<ModeConfig> modes)
        {
            try
            {
                if (!Directory.Exists(AppDataPath))
                    Directory.CreateDirectory(AppDataPath);

                var envelope = new PromptsFile
                {
                    Version = CurrentSchemaVersion,
                    Modes = modes.Select(PromptsModeDto.FromModeConfig).ToList(),
                };

                string json = JsonSerializer.Serialize(envelope, JsonOpts);
                string tempPath = PromptsFilePath + ".tmp";
                File.WriteAllText(tempPath, json);

                // Atomic replace: on Windows File.Move with overwrite is atomic within the same volume.
                if (File.Exists(PromptsFilePath))
                    File.Replace(tempPath, PromptsFilePath, null);
                else
                    File.Move(tempPath, PromptsFilePath);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[PromptsService] Save failed: {ex.Message}");
                throw;
            }
        }

        private static List<ModeConfig> CloneList(IEnumerable<ModeConfig> src)
            => src.Select(m => m.Clone()).ToList();

        /// <summary>
        /// Guarantees every mode has a non-empty Id and normalizes Order to a dense 0..N-1
        /// sequence in the existing list order.
        /// </summary>
        private static void EnsureIdsAndOrder(List<ModeConfig> modes)
        {
            for (int i = 0; i < modes.Count; i++)
            {
                if (string.IsNullOrWhiteSpace(modes[i].Id))
                    modes[i].Id = Guid.NewGuid().ToString("N");
                modes[i].Order = i;
            }
        }

        // === DTOs ===

        private class PromptsFile
        {
            [JsonPropertyName("version")]
            public int Version { get; set; } = CurrentSchemaVersion;

            [JsonPropertyName("modes")]
            public List<PromptsModeDto> Modes { get; set; } = new();
        }

        private class PromptsModeDto
        {
            [JsonPropertyName("id")]
            public string Id { get; set; } = "";

            [JsonPropertyName("name")]
            public string Name { get; set; } = "";

            [JsonPropertyName("icon")]
            public string Icon { get; set; } = "brain";

            [JsonPropertyName("systemPrompt")]
            public string SystemPrompt { get; set; } = "";

            [JsonPropertyName("order")]
            public int Order { get; set; }

            public ModeConfig ToModeConfig() => new()
            {
                Id = Id,
                Name = Name,
                Icon = string.IsNullOrWhiteSpace(Icon) ? "brain" : Icon,
                SystemPrompt = SystemPrompt ?? "",
                Order = Order,
            };

            public static PromptsModeDto FromModeConfig(ModeConfig m) => new()
            {
                Id = m.Id,
                Name = m.Name,
                Icon = m.Icon,
                SystemPrompt = m.SystemPrompt,
                Order = m.Order,
            };
        }
    }
}
