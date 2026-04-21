"""
Context Management Service for SnapEye
Maintains a rolling transcript window and builds structured LLM prompts.
"""
import logging
from collections import deque
from dataclasses import dataclass, field
from datetime import datetime
from typing import Deque, Dict, List, Optional

from config import settings

logger = logging.getLogger(__name__)


@dataclass
class TranscriptSegment:
    """A single transcript segment in the rolling window."""
    text: str
    source: str  # "microphone" or "speaker"
    is_final: bool = True
    timestamp: str = ""
    session_id: str = ""
    message_id: str = ""
    approx_tokens: int = 0

    def __post_init__(self):
        if not self.timestamp:
            self.timestamp = datetime.utcnow().isoformat()
        if not self.approx_tokens:
            self.approx_tokens = len(self.text) // 4  # rough token estimate


class ContextManager:
    """
    Manages conversation context for a session.

    Features:
    - Rolling transcript window (configurable token limit)
    - Entity tracking (speakers, topics)
    - Session summary generation (auto-compress when window is full)
    - Structured prompt building for LLM (transcript + RAG + question)
    """

    def __init__(self, session_id: str = "", max_tokens: int = 0):
        self.session_id = session_id
        self.max_tokens = max_tokens or settings.CONTEXT_MAX_TOKENS
        self.summary_threshold = settings.CONTEXT_SUMMARY_THRESHOLD

        self._segments: Deque[TranscriptSegment] = deque()
        self._total_tokens: int = 0
        self._session_summary: str = ""
        self._entities: Dict[str, List[str]] = {
            "speakers": [],
            "topics": [],
            "key_terms": [],
        }
        self._screen_context: str = ""

    def add_transcript(
        self,
        text: str,
        source: str = "microphone",
        is_final: bool = True,
        message_id: str = "",
    ):
        """Add a transcript segment to the rolling window."""
        if not text or not text.strip():
            return

        segment = TranscriptSegment(
            text=text.strip(),
            source=source,
            is_final=is_final,
            session_id=self.session_id,
            message_id=message_id,
        )

        self._segments.append(segment)
        self._total_tokens += segment.approx_tokens

        # Trim old segments if exceeding max tokens
        while self._total_tokens > self.max_tokens and len(self._segments) > 1:
            removed = self._segments.popleft()
            self._total_tokens -= removed.approx_tokens

    def set_screen_context(self, ocr_text: str):
        """Update the screen context (from OCR)."""
        self._screen_context = ocr_text

    def get_rolling_window(self, last_n_tokens: int = 0) -> str:
        """Get the rolling transcript window as formatted text."""
        target_tokens = last_n_tokens or self.max_tokens
        parts = []
        token_count = 0

        for segment in reversed(list(self._segments)):
            if token_count + segment.approx_tokens > target_tokens:
                break
            speaker = "You" if segment.source == "microphone" else "Other"
            parts.append(f"[{speaker}]: {segment.text}")
            token_count += segment.approx_tokens

        parts.reverse()
        return "\n".join(parts)

    def build_llm_context(
        self,
        question: str,
        rag_chunks: Optional[List] = None,
        system_prompt: Optional[str] = None,
    ) -> Dict:
        """
        Build a complete context for the LLM request.

        Returns a dict with:
        - system_prompt: the system message
        - user_message: combined context + question
        - transcript_context: the rolling window text
        - rag_context: formatted RAG results
        - screen_context: OCR text
        """
        # Build transcript portion
        transcript_text = self.get_rolling_window(last_n_tokens=50000)

        # Build RAG portion
        rag_text = ""
        if rag_chunks:
            rag_parts = []
            for i, chunk in enumerate(rag_chunks[:5]):
                text = chunk.text if hasattr(chunk, "text") else str(chunk)
                score = chunk.score if hasattr(chunk, "score") else 0.0
                rag_parts.append(f"[Source {i+1}, relevance: {score:.2f}]\n{text}")
            rag_text = "\n\n".join(rag_parts)

        # Build the user message with clear structure
        user_parts = []

        if self._session_summary:
            user_parts.append(
                f"SESSION SUMMARY:\n{self._session_summary}"
            )

        if transcript_text:
            user_parts.append(
                f"LIVE CONVERSATION TRANSCRIPT (you have full access to this):\n"
                f"---\n{transcript_text[-4000:]}\n---"
            )

        if rag_text:
            user_parts.append(
                f"RELEVANT KNOWLEDGE BASE:\n{rag_text}"
            )

        if self._screen_context:
            user_parts.append(
                f"ON-SCREEN TEXT:\n{self._screen_context[:2000]}"
            )

        user_parts.append(
            f"DETECTED QUESTION FROM CONVERSATION: {question}\n\n"
            f"Using the conversation transcript and any other context above, "
            f"provide a helpful, concise answer to the detected question. "
            f"If the question references something discussed in the transcript, "
            f"use that information directly. Be specific and actionable."
        )

        if not system_prompt:
            system_prompt = (
                "You are SnapEye, a real-time AI assistant that monitors live conversations. "
                "You DO have access to the live conversation transcript provided below. "
                "When a question or topic is detected in the conversation, you provide "
                "helpful, context-aware answers based on what was actually said. "
                "IMPORTANT: You CAN see the conversation transcript. Do NOT say you don't "
                "have access to previous conversations - you do, it's provided in the user message. "
                "Be concise, accurate, and use markdown formatting. "
                "Answer based on the transcript context when relevant, or provide general "
                "knowledge when the question is about a topic discussed."
            )

        return {
            "system_prompt": system_prompt,
            "user_message": "\n\n".join(user_parts),
            "transcript_context": transcript_text,
            "rag_context": rag_text,
            "screen_context": self._screen_context,
        }

    async def summarize_if_needed(self) -> Optional[str]:
        """
        If the rolling window exceeds the summary threshold, compress older segments
        into a summary using the LLM.
        """
        if self._total_tokens < self.summary_threshold:
            return None

        # Take the older half of segments for summarization
        segments_list = list(self._segments)
        half = len(segments_list) // 2
        old_segments = segments_list[:half]

        old_text = "\n".join(
            f"[{'You' if s.source == 'microphone' else 'Other'}]: {s.text}"
            for s in old_segments
        )

        try:
            from services.llm_router import llm_router, LLMMessage

            summary = await llm_router.generate(
                messages=[LLMMessage(
                    role="user",
                    content=(
                        f"Summarize this conversation excerpt in 3-5 bullet points. "
                        f"Focus on key topics, decisions, and questions:\n\n{old_text}"
                    ),
                )],
                max_tokens=500,
                temperature=0.3,
                system_prompt="You are a concise conversation summarizer.",
            )

            self._session_summary = summary

            # Remove old segments that were summarized
            for _ in range(half):
                if self._segments:
                    removed = self._segments.popleft()
                    self._total_tokens -= removed.approx_tokens

            logger.info(
                f"Context summarized: removed {half} segments, "
                f"summary: {len(summary)} chars, "
                f"remaining tokens: ~{self._total_tokens}"
            )
            return summary

        except Exception as e:
            logger.error(f"Context summarization error: {e}")
            return None

    def reset(self):
        """Reset the context for a new session."""
        self._segments.clear()
        self._total_tokens = 0
        self._session_summary = ""
        self._entities = {"speakers": [], "topics": [], "key_terms": []}
        self._screen_context = ""

    @property
    def total_tokens(self) -> int:
        return self._total_tokens

    @property
    def segment_count(self) -> int:
        return len(self._segments)


class ContextSessionManager:
    """Manages context instances for multiple sessions."""

    def __init__(self):
        self._contexts: Dict[str, ContextManager] = {}

    def get_or_create(self, session_id: str) -> ContextManager:
        if session_id not in self._contexts:
            self._contexts[session_id] = ContextManager(session_id=session_id)
        return self._contexts[session_id]

    def remove(self, session_id: str):
        self._contexts.pop(session_id, None)

    def get(self, session_id: str) -> Optional[ContextManager]:
        return self._contexts.get(session_id)


# Global context session manager
context_session_manager = ContextSessionManager()
