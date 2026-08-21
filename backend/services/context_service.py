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
        # Interview context: set by the client when Interview mode is active.
        self._job_description: str = ""
        self._interview_company: str = ""
        self._interview_role: str = ""
        self._mode: str = ""
        # Full system-prompt text of the mode the user has selected in the client. Prepended
        # to every live auto-suggestion so the active mode's instructions are strictly applied.
        self._mode_system_prompt: str = ""

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

    def set_interview_context(
        self,
        job_description: str = "",
        company: str = "",
        role: str = "",
        mode: str = "",
        mode_system_prompt: str = "",
    ):
        """Store the active mode and (for interview mode) the target job description."""
        self._job_description = job_description or ""
        self._interview_company = company or ""
        self._interview_role = role or ""
        self._mode = mode or ""
        self._mode_system_prompt = mode_system_prompt or ""

    @property
    def is_interview(self) -> bool:
        return "interview" in (self._mode or "").lower() or bool(self._job_description)

    @property
    def job_description(self) -> str:
        return self._job_description

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

    @staticmethod
    def _format_experience_chunks(experience_chunks: Optional[List]) -> str:
        """Format retrieved experience/education entries for the prompt."""
        if not experience_chunks:
            return ""
        parts = []
        for chunk in experience_chunks[:5]:
            meta = getattr(chunk, "metadata", None) or {}
            text = chunk.text if hasattr(chunk, "text") else str(chunk)
            company = meta.get("company", "")
            role = meta.get("role", "")
            period = ""
            if meta.get("start_date") or meta.get("end_date"):
                period = f" ({meta.get('start_date', '?')} - {meta.get('end_date', 'Present')})"
            header_bits = [b for b in [role, company] if b]
            header = " at ".join(header_bits) + period if header_bits else "Experience"
            body = meta.get("summary") or text
            parts.append(f"- {header}:\n  {body}")
        return "\n".join(parts)

    def build_llm_context(
        self,
        question: str,
        rag_chunks: Optional[List] = None,
        system_prompt: Optional[str] = None,
        experience_chunks: Optional[List] = None,
        memory_chunks: Optional[List] = None,
        experience_searched: bool = False,
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

        experience_text = self._format_experience_chunks(experience_chunks)

        # Build the user message with clear structure
        user_parts = []

        if self._session_summary:
            user_parts.append(
                f"SESSION SUMMARY:\n{self._session_summary}"
            )

        if experience_text:
            user_parts.append(
                "MY RELEVANT BACKGROUND (draw on this to answer in the first person, "
                "as the candidate — these are my real experiences):\n"
                f"{experience_text}"
            )
        elif experience_searched and not experience_text:
            user_parts.append(
                "BACKGROUND NOTE: No experience entry matched this question closely. "
                "Do NOT invent prior roles, projects, or employers — answer from general "
                "knowledge or say briefly you don't have a matching example."
            )

        if memory_chunks:
            mem_lines = []
            for chunk in memory_chunks[:3]:
                text = chunk.text if hasattr(chunk, "text") else str(chunk)
                mem_lines.append(f"- {text}")
            user_parts.append(
                "RELEVANT PAST CONVERSATION (same user):\n" + "\n".join(mem_lines)
            )

        if self.is_interview and self._job_description:
            target = ""
            if self._interview_role or self._interview_company:
                target = f" (target role: {self._interview_role} at {self._interview_company})"
            user_parts.append(
                f"TARGET JOB DESCRIPTION{target}:\n---\n{self._job_description[:3000]}\n---\n"
                "Tailor the answer to the skills, tools, and standards this role expects."
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
            f"Using my background and any other context above, "
            f"answer the detected question. If my background is relevant, answer in the "
            f"first person about what I actually did. Be specific and actionable."
        )

        if not system_prompt:
            system_prompt = self._default_system_prompt()

        return {
            "system_prompt": system_prompt,
            "user_message": "\n\n".join(user_parts),
            "transcript_context": transcript_text,
            "rag_context": rag_text,
            "screen_context": self._screen_context,
            "experience_context": experience_text,
        }

    def _default_system_prompt(self) -> str:
        """
        Human, first-person, STAR-style assistant prompt. Answers sound like a real
        person recounting their work — the challenge, what they did (including the
        workflow/architecture), and the value delivered to the business — with genuine
        emotion and reflection rather than robotic bullet dumps.
        """
        base = (
            "You are SnapEye, a real-time assistant that speaks AS the user (first person, \"I\"). "
            "You DO have access to the live conversation transcript and the user's real background "
            "provided in the user message. Never say you lack access to the conversation.\n\n"
            "When answering questions about the user's experience, respond like a thoughtful human "
            "telling their story:\n"
            "- Briefly set the situation and the real challenge or struggle you faced.\n"
            "- Explain what YOU did: the workflow you designed and the technical architecture, "
            "concrete tools, and decisions — enough depth to sound like the person who built it.\n"
            "- Share the human side: what was hard, what you learned, how it felt to solve it.\n"
            "- Close with the outcome and the value it added to the business or organization.\n"
            "Sound natural, warm, and confident — connected and reflective, never a robotic list. "
            "Keep it tight (under ~220 words) and use light markdown (**bold**, short paragraphs), "
            "no large headings.\n"
            "STRICT RULES:\n"
            "- Answer the detected question directly and stay strictly on-topic — no filler, no "
            "unrelated tangents.\n"
            "- Be concrete and practical (specific steps, tools, decisions).\n"
            "- The detected question may be garbled by speech-to-text; answer the most likely "
            "intent from the transcript rather than replying about the literal broken words.\n"
            "- Ground every claim in the background/context provided; never invent facts. If it "
            "isn't in my background or the context, say so briefly instead of fabricating."
        )
        if self.is_interview:
            base += (
                "\n\nINTERVIEW MODE: Follow the ACTIVE MODE instructions above. Present me as a "
                "strong, likeable candidate while staying truthful to my real experience."
            )
        # Prepend the active mode's instructions so the mode the user picked is strictly
        # applied to every live suggestion, on top of the grounding rules above.
        if self._mode_system_prompt.strip():
            base = (
                "ACTIVE MODE (follow these instructions strictly):\n"
                f"{self._mode_system_prompt.strip()}\n\n" + base
            )
        return base

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
