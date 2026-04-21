using System;
using System.Collections.Generic;
using System.Linq;

namespace SnapEye.Models
{
    public class TranscriptionConversation
    {
        public List<TranscriptionMessage> Messages { get; set; } = new List<TranscriptionMessage>();
        public DateTime SessionStartTime { get; set; } = DateTime.Now;
        public string SessionId { get; set; } = Guid.NewGuid().ToString();

        // Add a new message with automatic indexing
        public TranscriptionMessage AddMessage(string text, MessageSource source)
        {
            var message = new TranscriptionMessage
            {
                Index = Messages.Count,
                Text = text,
                Source = source,
                Timestamp = DateTime.Now,
                SessionId = SessionId
            };

            Messages.Add(message);
            return message;
        }

        // Get messages by source
        public List<TranscriptionMessage> GetMessagesBySource(MessageSource source)
        {
            return Messages.Where(m => m.Source == source).ToList();
        }

        // Get recent messages
        public List<TranscriptionMessage> GetRecentMessages(int count)
        {
            return Messages.OrderByDescending(m => m.Timestamp).Take(count).ToList();
        }

        // Clear all messages
        public void Clear()
        {
            Messages.Clear();
        }

        // Export conversation as formatted text
        public string ExportAsText()
        {
            var output = $"Transcription Session: {SessionId}\n";
            output += $"Started: {SessionStartTime}\n";
            output += new string('-', 50) + "\n\n";

            foreach (var message in Messages)
            {
                var sourceLabel = message.Source == MessageSource.Microphone ? "USER" : "SYSTEM";
                output += $"[{message.Index}] {sourceLabel} ({message.Timestamp:HH:mm:ss})\n";
                output += $"{message.Text}\n\n";
            }

            return output;
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