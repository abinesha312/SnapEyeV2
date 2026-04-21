using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using SnapEye.Models;

namespace SnapEye.Services
{
    /// <summary>
    /// Keeps the current meeting's conversation log in memory and persists
    /// each session to %APPDATA%\SnapEye\conversations\session-&lt;timestamp&gt;.json
    /// so the user can re-open SnapEye and still see past conversations.
    /// </summary>
    public class ConversationHistoryService
    {
        private static readonly string RootDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "SnapEye", "conversations");

        private static readonly JsonSerializerOptions JsonOpts = new()
        {
            WriteIndented = true
        };

        public ConversationSession Current { get; private set; } = new();

        public event EventHandler<ConversationMessage>? MessageAdded;
        public event EventHandler? SessionReset;

        public ConversationHistoryService()
        {
            EnsureDir();
        }

        /// <summary>Start a brand-new meeting session (previous one is flushed to disk).</summary>
        public void StartNewSession()
        {
            try
            {
                if (Current != null && Current.Messages.Count > 0)
                {
                    Current.EndedAt ??= DateTime.Now;
                    Save(Current);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[History] Flush-on-reset failed: {ex.Message}");
            }

            Current = new ConversationSession();
            SessionReset?.Invoke(this, EventArgs.Empty);
        }

        public void EndCurrentSession()
        {
            if (Current == null) return;
            Current.EndedAt = DateTime.Now;
            Save(Current);
        }

        /// <summary>
        /// Update an already-saved session with a new title and persist the change.
        /// Finds the file by session id / started-at timestamp.
        /// </summary>
        public static void UpdateSavedSessionTitle(string sessionId, string title)
        {
            try
            {
                if (!Directory.Exists(RootDir)) return;
                foreach (var file in Directory.GetFiles(RootDir, "session-*.json"))
                {
                    try
                    {
                        string raw = File.ReadAllText(file);
                        var s = JsonSerializer.Deserialize<ConversationSession>(raw);
                        if (s == null) continue;
                        if (!string.Equals(s.SessionId, sessionId, StringComparison.Ordinal)) continue;
                        s.Title = title;
                        File.WriteAllText(file, JsonSerializer.Serialize(s, JsonOpts));
                        return;
                    }
                    catch { /* skip corrupt */ }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[History] UpdateTitle failed: {ex.Message}");
            }
        }

        /// <summary>Persist the current session right now (no throttle).</summary>
        public void SaveCurrentNow()
        {
            try { Save(Current); } catch (Exception ex) { Console.WriteLine($"[History] Save failed: {ex.Message}"); }
        }

        public ConversationMessage Append(ConversationMessageKind kind, string text, string? label = null)
        {
            var msg = new ConversationMessage
            {
                Kind = kind,
                Text = text ?? string.Empty,
                Label = label
            };
            Current.Messages.Add(msg);
            TrySaveThrottled();
            MessageAdded?.Invoke(this, msg);
            return msg;
        }

        /// <summary>List saved sessions (newest first). Used by a future history viewer.</summary>
        public IReadOnlyList<ConversationSession> ListSavedSessions()
        {
            var list = new List<ConversationSession>();
            try
            {
                if (!Directory.Exists(RootDir)) return list;
                foreach (var file in Directory.GetFiles(RootDir, "session-*.json")
                                              .OrderByDescending(File.GetCreationTimeUtc))
                {
                    try
                    {
                        string json = File.ReadAllText(file);
                        var s = JsonSerializer.Deserialize<ConversationSession>(json);
                        if (s != null) list.Add(s);
                    }
                    catch { /* skip corrupt */ }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[History] List failed: {ex.Message}");
            }
            return list;
        }

        #region IO

        private DateTime lastSaveAt = DateTime.MinValue;

        private void TrySaveThrottled()
        {
            // Save at most every 750ms while a session is active to avoid disk spam during streaming.
            var now = DateTime.Now;
            if ((now - lastSaveAt).TotalMilliseconds < 750) return;
            lastSaveAt = now;
            try { Save(Current); } catch (Exception ex) { Console.WriteLine($"[History] Save failed: {ex.Message}"); }
        }

        private static void EnsureDir()
        {
            try
            {
                if (!Directory.Exists(RootDir)) Directory.CreateDirectory(RootDir);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[History] mkdir failed: {ex.Message}");
            }
        }

        private static void Save(ConversationSession session)
        {
            EnsureDir();
            string path = Path.Combine(
                RootDir,
                $"session-{session.StartedAt:yyyyMMdd-HHmmss}-{session.SessionId[..Math.Min(8, session.SessionId.Length)]}.json");
            string json = JsonSerializer.Serialize(session, JsonOpts);
            File.WriteAllText(path, json);
        }

        #endregion
    }
}
