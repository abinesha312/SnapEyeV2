using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using SnapEye.Models;

namespace SnapEye.Services
{
    /// <summary>
    /// Keeps the current meeting's conversation log in memory and persists each session
    /// to <c>%APPDATA%\SnapEye\conversations\session-&lt;timestamp&gt;.json</c> so the user can
    /// re-open SnapEye and still see past conversations.
    ///
    /// Disk writes are serialized on a background worker task so that <see cref="Append"/>
    /// never blocks the UI thread, even when sessions grow large.
    /// </summary>
    public class ConversationHistoryService
    {
        private static readonly string RootDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "SnapEye", "conversations");

        /// <summary>
        /// Stable filename for the one ongoing conversation thread. Voice, typed, screen-
        /// capture, and assistant answers all append here until the user explicitly ends
        /// or starts a new conversation — closing the overlay must NOT archive it.
        /// </summary>
        private const string ActiveSessionFileName = "current-session.json";

        private static readonly JsonSerializerOptions JsonOpts = new()
        {
            WriteIndented = true
        };

        // Throttled background-save pipeline.
        private readonly object sync = new();
        private DateTime lastSaveAt = DateTime.MinValue;
        private Task? pendingSaveTask;
        private bool saveRequested;
        private static readonly TimeSpan SaveThrottle = TimeSpan.FromMilliseconds(750);

        public ConversationSession Current { get; private set; } = new();

        // Live spoken-transcript entries keyed by the backend message id, so transcript
        // updates refine ONE history entry instead of appending a duplicate entry per
        // update (each update carries the full utterance text).
        private readonly Dictionary<string, ConversationMessage> liveSpokenById = new();

        public event EventHandler<ConversationMessage>? MessageAdded;
        public event EventHandler? SessionReset;

        /// <summary>
        /// Raised when a message reaches its final form and should be synced to the
        /// backend - once for every non-spoken message (fired right after MessageAdded),
        /// and once per spoken message when its transcript is finalized (not on every
        /// interim update, to avoid spamming the server with partial transcripts).
        /// </summary>
        public event EventHandler<ConversationMessage>? MessageFinalized;

        public ConversationHistoryService()
        {
            EnsureDir();
            ResumeActiveSession();
        }

        /// <summary>
        /// Load the single ongoing conversation from disk (if any). Called at startup so
        /// reopening the overlay continues the same thread instead of starting a fresh one.
        /// </summary>
        public void ResumeActiveSession()
        {
            lock (sync)
            {
                try
                {
                    string path = Path.Combine(RootDir, ActiveSessionFileName);
                    if (!File.Exists(path)) return;

                    var loaded = JsonSerializer.Deserialize<ConversationSession>(File.ReadAllText(path));
                    if (loaded == null || loaded.EndedAt.HasValue) return;

                    Current = loaded;
                    liveSpokenById.Clear();
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[History] ResumeActiveSession failed: {ex.Message}");
                }
            }
        }

        /// <summary>Start a brand-new meeting session (previous one is flushed to disk).</summary>
        public void StartNewSession()
        {
            ConversationSession? toSave = null;
            lock (sync)
            {
                if (Current.Messages.Count > 0)
                {
                    Current.EndedAt ??= DateTime.Now;
                    toSave = CloneForSave(Current);
                }
                Current = new ConversationSession();
                liveSpokenById.Clear();
            }
            if (toSave != null)
                _ = Task.Run(() => SaveSafe(toSave));
            SessionReset?.Invoke(this, EventArgs.Empty);
        }

        public void EndCurrentSession()
        {
            ConversationSession? toSave = null;
            lock (sync)
            {
                Current.EndedAt = DateTime.Now;
                toSave = CloneForSave(Current);
            }
            if (toSave != null)
                _ = Task.Run(() => SaveSafe(toSave));
        }

        /// <summary>
        /// Update an already-saved session with a new title and persist the change.
        /// Runs on the caller's thread; callers should invoke from a background task
        /// (e.g. <see cref="Task.Run(Action)"/>).
        /// </summary>
        public static void UpdateSavedSessionTitle(string sessionId, string title)
        {
            try
            {
                if (!Directory.Exists(RootDir)) return;
                // Active ongoing thread
                string activePath = Path.Combine(RootDir, ActiveSessionFileName);
                if (File.Exists(activePath))
                {
                    try
                    {
                        string raw = File.ReadAllText(activePath);
                        var s = JsonSerializer.Deserialize<ConversationSession>(raw);
                        if (s != null && string.Equals(s.SessionId, sessionId, StringComparison.Ordinal))
                        {
                            s.Title = title;
                            File.WriteAllText(activePath, JsonSerializer.Serialize(s, JsonOpts));
                            return;
                        }
                    }
                    catch { /* fall through to archived files */ }
                }
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

        /// <summary>Persist the current session right now, off the calling thread (fire-and-forget).</summary>
        public void SaveCurrentNow()
        {
            ConversationSession snapshot;
            lock (sync) snapshot = CloneForSave(Current);
            _ = Task.Run(() => SaveSafe(snapshot));
        }

        public ConversationMessage Append(ConversationMessageKind kind, string text, string? label = null)
        {
            var msg = new ConversationMessage
            {
                Kind = kind,
                Text = text ?? string.Empty,
                Label = label
            };
            lock (sync)
            {
                Current.Messages.Add(msg);
            }
            RequestThrottledSave();
            MessageAdded?.Invoke(this, msg);
            return msg;
        }

        /// <summary>
        /// Add or update a live spoken-transcript entry. Transcript events repeat the
        /// full utterance text as it refines, so events sharing a
        /// <paramref name="messageId"/> update the same entry in place; only a new id
        /// creates a new entry. Falls back to <see cref="Append"/> when no id is given.
        /// <paramref name="isFinal"/> should reflect the transcript event's own finality
        /// flag - <see cref="MessageFinalized"/> only fires when true, so interim
        /// (still-refining) transcript updates don't get synced to the backend on every
        /// partial revision.
        /// </summary>
        public ConversationMessage UpsertSpoken(ConversationMessageKind kind, string? messageId, string text, bool isFinal = false)
        {
            if (string.IsNullOrEmpty(messageId))
            {
                var appended = Append(kind, text);
                if (isFinal)
                    MessageFinalized?.Invoke(this, appended);
                return appended;
            }

            lock (sync)
            {
                if (liveSpokenById.TryGetValue(messageId, out var existing))
                {
                    existing.Text = text ?? string.Empty;
                    RequestThrottledSave();
                    if (isFinal)
                        MessageFinalized?.Invoke(this, existing);
                    return existing;
                }
            }

            var msg = Append(kind, text);
            lock (sync)
            {
                liveSpokenById[messageId] = msg;
            }
            if (isFinal)
                MessageFinalized?.Invoke(this, msg);
            return msg;
        }

        /// <summary>
        /// Forget live transcript ids. Call when a new transcription WebSocket session
        /// starts: backend message ids restart from msg_1, and stale ids must not make
        /// new utterances overwrite entries from the previous connection.
        /// </summary>
        public void ResetLiveTranscripts()
        {
            lock (sync)
            {
                liveSpokenById.Clear();
            }
        }

        /// <summary>
        /// Replace the in-memory conversation with the server's version of it, so a
        /// relaunched app resumes the same ongoing thread instead of starting blank.
        /// Called once at startup after GET /api/conversations/active resolves.
        /// </summary>
        public void HydrateFromServer(string conversationId, IEnumerable<ConversationMessage> messages)
        {
            lock (sync)
            {
                Current.ConversationId = conversationId;
                Current.Messages.Clear();
                Current.Messages.AddRange(messages);
                liveSpokenById.Clear();
            }
        }

        /// <summary>List saved sessions (newest first). Uses blocking I/O; callers on the UI thread should wrap in <see cref="Task.Run(System.Func{IReadOnlyList{ConversationSession}})"/>.</summary>
        public IReadOnlyList<ConversationSession> ListSavedSessions()
        {
            var list = new List<ConversationSession>();
            try
            {
                if (!Directory.Exists(RootDir)) return list;

                // Ongoing thread (all message types in one place).
                string activePath = Path.Combine(RootDir, ActiveSessionFileName);
                if (File.Exists(activePath))
                {
                    try
                    {
                        var active = JsonSerializer.Deserialize<ConversationSession>(File.ReadAllText(activePath));
                        if (active != null && !active.EndedAt.HasValue)
                            list.Add(active);
                    }
                    catch { /* skip corrupt */ }
                }

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

        /// <summary>Async variant that avoids blocking the UI thread.</summary>
        public Task<IReadOnlyList<ConversationSession>> ListSavedSessionsAsync(CancellationToken ct = default)
            => Task.Run(() => ListSavedSessions(), ct);

        #region IO

        /// <summary>
        /// Request a throttled save. Coalesces multiple rapid calls into at most one
        /// background file write per <see cref="SaveThrottle"/>. The caller's thread
        /// is never blocked for disk I/O.
        /// </summary>
        private void RequestThrottledSave()
        {
            ConversationSession? snapshot = null;
            bool shouldStartWorker = false;

            lock (sync)
            {
                var now = DateTime.Now;
                if ((now - lastSaveAt) < SaveThrottle)
                {
                    // A recent save happened; mark pending and let the worker pick it up.
                    saveRequested = true;
                    if (pendingSaveTask == null || pendingSaveTask.IsCompleted)
                        shouldStartWorker = true;
                }
                else
                {
                    lastSaveAt = now;
                    snapshot = CloneForSave(Current);
                }
            }

            if (snapshot != null)
                _ = Task.Run(() => SaveSafe(snapshot));
            else if (shouldStartWorker)
                StartDrainWorker();
        }

        /// <summary>Worker that waits out the throttle window then performs the latest pending save.</summary>
        private void StartDrainWorker()
        {
            lock (sync)
            {
                pendingSaveTask = Task.Run(async () =>
                {
                    await Task.Delay(SaveThrottle).ConfigureAwait(false);
                    ConversationSession? snapshot = null;
                    lock (sync)
                    {
                        if (saveRequested)
                        {
                            saveRequested = false;
                            lastSaveAt = DateTime.Now;
                            snapshot = CloneForSave(Current);
                        }
                    }
                    if (snapshot != null) SaveSafe(snapshot);
                });
            }
        }

        /// <summary>
        /// Deep-enough copy of the session so it can be serialized off the caller's thread
        /// without racing with subsequent <see cref="Append"/> calls mutating <c>Messages</c>.
        /// The <see cref="ConversationMessage"/> entries themselves are not mutated after add,
        /// so copying by reference is safe.
        /// </summary>
        private static ConversationSession CloneForSave(ConversationSession src)
        {
            return new ConversationSession
            {
                SessionId = src.SessionId,
                StartedAt = src.StartedAt,
                EndedAt = src.EndedAt,
                Title = src.Title,
                ConversationId = src.ConversationId,
                Messages = new List<ConversationMessage>(src.Messages),
            };
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

        private static string ResolveSavePath(ConversationSession session)
        {
            if (!session.EndedAt.HasValue)
                return Path.Combine(RootDir, ActiveSessionFileName);

            string sessionIdSlice = session.SessionId[..Math.Min(8, session.SessionId.Length)];
            return Path.Combine(
                RootDir,
                $"session-{session.StartedAt:yyyyMMdd-HHmmss}-{sessionIdSlice}.json");
        }

        private static void SaveSafe(ConversationSession session)
        {
            try
            {
                EnsureDir();
                string path = ResolveSavePath(session);
                string json = JsonSerializer.Serialize(session, JsonOpts);
                // Write via a UNIQUE temp file + replace to avoid torn writes if the app dies
                // mid-write. The temp name includes a GUID so concurrent writers (the Dashboard
                // and overlay history services, or a save racing a title update) can't collide
                // on the same ".tmp" path — which previously produced
                // "the file is being used by another process".
                string tmp = $"{path}.{Guid.NewGuid():N}.tmp";
                File.WriteAllText(tmp, json);
                try
                {
                    if (File.Exists(path))
                        File.Replace(tmp, path, destinationBackupFileName: null);
                    else
                        File.Move(tmp, path);
                }
                finally
                {
                    try { if (File.Exists(tmp)) File.Delete(tmp); } catch { /* ignore */ }
                }

                // When a session is archived, remove the active-session file so we don't
                // show duplicate entries in the conversation list.
                if (session.EndedAt.HasValue)
                {
                    string activePath = Path.Combine(RootDir, ActiveSessionFileName);
                    try { if (File.Exists(activePath)) File.Delete(activePath); } catch { /* ignore */ }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[History] Save failed: {ex.Message}");
            }
        }

        #endregion
    }
}
