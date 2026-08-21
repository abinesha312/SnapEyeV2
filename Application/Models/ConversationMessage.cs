using System;

namespace SnapEye.Models
{
    /// <summary>Kind of message in a meeting conversation thread.</summary>
    public enum ConversationMessageKind
    {
        UserTyped,       // Typed into Ask AI bar
        UserSpoken,      // Microphone transcription
        OtherSpoken,     // Speaker / system audio transcription
        AssistantAnswer, // AI streaming answer (final text)
        QuickAction      // Quick action chip label (displayed as user prompt)
    }

    /// <summary>One entry in a meeting conversation log.</summary>
    public sealed class ConversationMessage
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public DateTime Timestamp { get; set; } = DateTime.Now;
        public ConversationMessageKind Kind { get; set; }
        public string Text { get; set; } = string.Empty;

        /// <summary>Optional label (e.g. chip title that produced the answer).</summary>
        public string? Label { get; set; }
    }

    /// <summary>A full meeting conversation session (persisted to disk).</summary>
    public sealed class ConversationSession
    {
        public string SessionId { get; set; } = Guid.NewGuid().ToString("N");
        public DateTime StartedAt { get; set; } = DateTime.Now;
        public DateTime? EndedAt { get; set; }

        /// <summary>AI-generated one-line summary of the conversation (populated on end/close).</summary>
        public string? Title { get; set; }

        /// <summary>
        /// The backend's conversation_id for this session (see /api/conversations), once
        /// known - either returned by GET /api/conversations/active on hydration, or by
        /// POST /api/conversations/new when the user starts a fresh thread. Null until the
        /// first successful server round-trip.
        /// </summary>
        public string? ConversationId { get; set; }

        public System.Collections.Generic.List<ConversationMessage> Messages { get; set; } = new();
    }
}
