using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace SnapEye.Models
{
    /// <summary>
    /// Thread-safe wrapper around the live transcription message list.
    /// WebSocket receive callbacks (background thread) add / mutate messages,
    /// while the UI thread reads them to build context for the LLM. All public
    /// members acquire a single lock to keep the two in sync.
    /// </summary>
    public class TranscriptionConversation
    {
        private readonly List<TranscriptionMessage> messages = new();
        private readonly object sync = new();

        public DateTime SessionStartTime { get; set; } = DateTime.Now;
        public string SessionId { get; set; } = Guid.NewGuid().ToString();

        /// <summary>
        /// Raw backing list. Enumerate only while <see cref="SyncRoot"/> is locked.
        /// Prefer <see cref="Snapshot"/> / <see cref="FindByMessageId"/> / <see cref="GetTail"/> for safety.
        /// </summary>
        public List<TranscriptionMessage> Messages => messages;

        /// <summary>Lock object for callers that need to enumerate <see cref="Messages"/> directly.</summary>
        public object SyncRoot => sync;

        public int Count { get { lock (sync) return messages.Count; } }

        public TranscriptionMessage AddMessage(string text, MessageSource source)
        {
            lock (sync)
            {
                var message = new TranscriptionMessage
                {
                    Index = messages.Count,
                    Text = text,
                    Source = source,
                    Timestamp = DateTime.Now,
                    SessionId = SessionId
                };
                messages.Add(message);
                return message;
            }
        }

        /// <summary>Copy the current message list; safe to call from any thread.</summary>
        public List<TranscriptionMessage> Snapshot()
        {
            lock (sync) return new List<TranscriptionMessage>(messages);
        }

        /// <summary>Returns the last <paramref name="count"/> messages in order; safe to call from any thread.</summary>
        public List<TranscriptionMessage> GetTail(int count)
        {
            lock (sync)
            {
                if (count >= messages.Count) return new List<TranscriptionMessage>(messages);
                var result = new List<TranscriptionMessage>(count);
                for (int i = messages.Count - count; i < messages.Count; i++)
                    result.Add(messages[i]);
                return result;
            }
        }

        public TranscriptionMessage? FindByMessageId(string id)
        {
            if (string.IsNullOrEmpty(id)) return null;
            lock (sync)
            {
                for (int i = messages.Count - 1; i >= 0; i--)
                    if (messages[i].MessageId == id) return messages[i];
                return null;
            }
        }

        public void ReplaceAll(IEnumerable<TranscriptionMessage> newMessages)
        {
            lock (sync)
            {
                messages.Clear();
                messages.AddRange(newMessages);
            }
        }

        public List<TranscriptionMessage> GetMessagesBySource(MessageSource source)
        {
            lock (sync) return messages.Where(m => m.Source == source).ToList();
        }

        public List<TranscriptionMessage> GetRecentMessages(int count)
        {
            lock (sync)
                return messages.OrderByDescending(m => m.Timestamp).Take(count).ToList();
        }

        public void Clear()
        {
            lock (sync) messages.Clear();
        }

        public string ExportAsText()
        {
            var sb = new StringBuilder();
            sb.Append("Transcription Session: ").Append(SessionId).Append('\n');
            sb.Append("Started: ").Append(SessionStartTime).Append('\n');
            sb.Append(new string('-', 50)).Append("\n\n");

            lock (sync)
            {
                foreach (var message in messages)
                {
                    string sourceLabel = message.Source == MessageSource.Microphone ? "USER" : "SYSTEM";
                    sb.Append('[').Append(message.Index).Append("] ").Append(sourceLabel)
                      .Append(" (").Append(message.Timestamp.ToString("HH:mm:ss")).Append(")\n")
                      .Append(message.Text).Append("\n\n");
                }
            }
            return sb.ToString();
        }
    }

    public class TranscriptionMessage
    {
        public string MessageId { get; set; } = string.Empty;
        public int Index { get; set; }
        public string Text { get; set; } = string.Empty;
        public MessageSource Source { get; set; }
        public DateTime Timestamp { get; set; }
        public string SessionId { get; set; } = string.Empty;
        public double Confidence { get; set; } = 1.0;
        public bool IsNewSegment { get; set; } = false;
        public bool IsFinal { get; set; } = false;
    }

    public enum MessageSource
    {
        Microphone,  // User input
        Speaker      // System output
    }
}