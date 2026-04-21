"""
Keyword and Question Detection Service for SnapEye
Detects questions, keywords, and trigger phrases in real-time transcript stream.
"""
import logging
import re
import time
from dataclasses import dataclass, field
from typing import List, Optional, Set

from config import settings

logger = logging.getLogger(__name__)


@dataclass
class DetectionResult:
    """Result of keyword/question detection on a transcript segment."""
    triggered: bool = False
    trigger_type: str = ""  # "question", "keyword", "command"
    questions: List[str] = field(default_factory=list)
    keywords_matched: List[str] = field(default_factory=list)
    original_text: str = ""
    confidence: float = 0.0

    def to_dict(self):
        return {
            "triggered": self.triggered,
            "trigger_type": self.trigger_type,
            "questions": self.questions,
            "keywords_matched": self.keywords_matched,
            "original_text": self.original_text,
            "confidence": self.confidence,
        }


class KeywordDetector:
    """
    Detects questions, custom keywords, and trigger phrases in transcript text.

    Features:
    - Regex-based question detection
    - Custom keyword lists (configurable at runtime)
    - Debouncing to avoid rapid-fire triggers
    - Confidence scoring
    """

    # Question patterns (English)
    QUESTION_PATTERNS = [
        # Direct questions ending with ?
        r"[^.!]*\?",
        # Question words at start of sentence
        r"\b(what|how|why|when|where|who|which|whose|whom)\b[^.!?]*[.!?]?",
        # Polite requests
        r"\b(can you|could you|would you|will you|please)\b[^.!?]*[.!?]?",
        # Tell/explain/describe
        r"\b(tell me|explain|describe|elaborate|walk me through|give me)\b[^.!?]*[.!?]?",
        # Technical interview patterns
        r"\b(what is|what are|what was|what were|how do|how does|how did)\b[^.!?]*[.!?]?",
        # Opinion questions
        r"\b(what do you think|in your opinion|your thoughts on)\b[^.!?]*[.!?]?",
    ]

    # Command patterns (user explicitly asking for AI help)
    COMMAND_PATTERNS = [
        r"\b(hey snap ?eye|snap ?eye help|ai help|assist me)\b",
    ]

    def __init__(self):
        self._custom_keywords: Set[str] = set()
        self._question_regex = re.compile(
            "|".join(self.QUESTION_PATTERNS), re.IGNORECASE
        )
        self._command_regex = re.compile(
            "|".join(self.COMMAND_PATTERNS), re.IGNORECASE
        )
        self._last_trigger_time: float = 0
        self._debounce_ms = settings.KEYWORD_DEBOUNCE_MS

        logger.info(
            f"KeywordDetector initialized (debounce: {self._debounce_ms}ms, "
            f"question patterns: {len(self.QUESTION_PATTERNS)})"
        )

    def detect(self, text: str, is_final: bool = True) -> DetectionResult:
        """
        Detect questions, keywords, and commands in transcript text.

        Args:
            text: Transcript text to analyze
            is_final: Only trigger on final (not interim) results

        Returns:
            DetectionResult with trigger info
        """
        result = DetectionResult(original_text=text)

        # Only process final transcript segments to avoid noise
        if not is_final:
            return result

        if not text or len(text.strip()) < 5:
            return result

        # Check debounce
        now = time.time() * 1000  # ms
        if now - self._last_trigger_time < self._debounce_ms:
            return result

        text_lower = text.lower().strip()

        # 1. Check for explicit commands
        command_match = self._command_regex.search(text_lower)
        if command_match:
            result.triggered = True
            result.trigger_type = "command"
            result.confidence = 1.0
            self._last_trigger_time = now
            logger.info(f"Command detected: '{command_match.group()}'")
            return result

        # 2. Check for questions
        questions = self._find_questions(text)
        if questions:
            result.questions = questions
            result.triggered = True
            result.trigger_type = "question"
            result.confidence = min(0.5 + 0.1 * len(questions), 1.0)

        # 3. Check for custom keywords
        matched_keywords = self._find_keywords(text_lower)
        if matched_keywords:
            result.keywords_matched = matched_keywords
            if not result.triggered:
                result.triggered = True
                result.trigger_type = "keyword"
                result.confidence = min(0.4 + 0.15 * len(matched_keywords), 1.0)
            else:
                # Boost confidence if both question and keywords
                result.confidence = min(result.confidence + 0.2, 1.0)
                result.trigger_type = "question+keyword"

        if result.triggered:
            self._last_trigger_time = now
            logger.info(
                f"Detection triggered: type={result.trigger_type}, "
                f"questions={len(result.questions)}, "
                f"keywords={len(result.keywords_matched)}, "
                f"confidence={result.confidence:.2f}"
            )

        return result

    def _find_questions(self, text: str) -> List[str]:
        """Extract question sentences from text."""
        questions = []

        # First: anything ending with ?
        q_mark_matches = re.findall(r"[^.!?]*\?", text)
        for q in q_mark_matches:
            q = q.strip()
            if len(q) > 10:
                questions.append(q)

        # Second: question-word patterns (if no ? found)
        if not questions:
            matches = self._question_regex.findall(text)
            for m in matches:
                m = m.strip() if isinstance(m, str) else m
                if isinstance(m, str) and len(m) > 10:
                    questions.append(m)

        return questions[:3]  # cap at 3 questions per segment

    def _find_keywords(self, text_lower: str) -> List[str]:
        """Find custom keywords in text."""
        matched = []
        for keyword in self._custom_keywords:
            if keyword.lower() in text_lower:
                matched.append(keyword)
        return matched

    # --- Keyword Management ---

    def add_keywords(self, keywords: List[str]):
        """Add custom keywords to monitor."""
        for kw in keywords:
            kw = kw.strip()
            if kw:
                self._custom_keywords.add(kw)
        logger.info(f"Added {len(keywords)} keywords (total: {len(self._custom_keywords)})")

    def remove_keywords(self, keywords: List[str]):
        """Remove custom keywords."""
        for kw in keywords:
            self._custom_keywords.discard(kw.strip())

    def set_keywords(self, keywords: List[str]):
        """Replace all custom keywords."""
        self._custom_keywords = {kw.strip() for kw in keywords if kw.strip()}
        logger.info(f"Set {len(self._custom_keywords)} keywords")

    def get_keywords(self) -> List[str]:
        """Get current custom keywords."""
        return sorted(self._custom_keywords)

    def set_debounce(self, ms: int):
        """Set the debounce interval in milliseconds."""
        self._debounce_ms = max(ms, 500)  # minimum 500ms


# Global service instance
keyword_detector = KeywordDetector()
