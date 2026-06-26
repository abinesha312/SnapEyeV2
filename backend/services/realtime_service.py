"""
Audio-to-Text Transcription Service using Deepgram SDK v5
Handles incremental audio transcription with pause detection
"""
import json
import logging
import time
import io
from typing import Dict, Optional, List
from datetime import datetime

import sys
import os
sys.path.insert(0, os.path.dirname(os.path.dirname(os.path.abspath(__file__))))

from deepgram import DeepgramClient

from config import settings
from security import SecurityManager

# Configure logging
logger = logging.getLogger(__name__)


class AudioMessage:
    """Represents a single message in the chat session"""

    # Role mapping from audio source
    SOURCE_TO_ROLE = {
        "microphone": "user",
        "speaker": "assistant"  # System/AI responses captured from speaker
    }

    def __init__(self, message_id: int, role: str = "user", source: str = "microphone"):
        self.message_id = message_id  # Sequential integer ID
        self.role = role
        self.source = source  # Track audio source (microphone or speaker)
        self.transcript_parts: List[str] = []
        self.created_at = datetime.now()
        self.last_updated = datetime.now()
        self.is_final = False

    @classmethod
    def from_source(cls, message_id: int, source: str) -> 'AudioMessage':
        """Create a message with role automatically determined from source"""
        role = cls.SOURCE_TO_ROLE.get(source, "user")
        return cls(message_id=message_id, role=role, source=source)

    def add_transcript(self, text: str):
        """Add transcript text to this message (accumulates)"""
        if text.strip():
            self.transcript_parts.append(text.strip())
            self.last_updated = datetime.now()

    def set_transcript(self, text: str):
        """Set the complete transcript (replaces all parts)"""
        self.transcript_parts = [text] if text.strip() else []
        self.last_updated = datetime.now()

    def get_full_transcript(self) -> str:
        """Get the complete transcript for this message"""
        return " ".join(self.transcript_parts).strip()

    def to_dict(self) -> Dict:
        """Convert to dictionary for JSON serialization"""
        return {
            "id": self.message_id,  # Sequential ID for easy tracking
            "message_id": f"msg_{self.message_id}",  # String format for compatibility
            "role": self.role,
            "source": self.source,
            "content": self.get_full_transcript(),  # Standard field name
            "transcript": self.get_full_transcript(),  # Keep for compatibility
            "created_at": self.created_at.isoformat(),
            "last_updated": self.last_updated.isoformat(),
            "is_final": self.is_final
        }


class DeepgramTranscriptionService:
    """
    Audio-to-Text transcription service using Deepgram SDK v5
    
    Transcribes audio chunks incrementally with pause detection
    """
    
    def __init__(
        self,
        api_key: str,
        security_manager: SecurityManager,
        model: str = settings.DEEPGRAM_MODEL,
        language: str = settings.DEEPGRAM_LANGUAGE,
        pause_threshold: float = 5.0
    ):
        """
        Initialize Deepgram transcription service
        
        Args:
            api_key: Deepgram API key
            security_manager: Security manager instance
            model: Deepgram model (nova-3, nova-2, nova, base, enhanced, etc.)
            language: Language code (en, es, fr, etc.)
            pause_threshold: Seconds of silence to trigger new message (default: 5.0)
        """
        self.api_key = api_key
        self.security = security_manager
        self.model = model
        self.language = language
        self.pause_threshold = pause_threshold
        
        # Initialize Deepgram client (v5 API)
        self.deepgram = DeepgramClient(api_key=api_key)
        
        # Session state
        self.messages: List[AudioMessage] = []
        self.current_message: Optional[AudioMessage] = None
        self.last_audio_time: Optional[float] = None
        self.message_counter = 0
        self.current_source: Optional[str] = None  # Track current audio source
        
        # Audio buffering (accumulate chunks before transcribing)
        self.audio_buffer: bytes = b''
        self.buffer_duration = 2.0  # Buffer 2 seconds of audio (increased for better accuracy)
        self.last_transcribe_time: Optional[float] = None
        self.min_buffer_size = int(24000 * 2 * self.buffer_duration)  # 24kHz * 2 bytes * duration
        
        logger.info(f"Initialized DeepgramTranscriptionService with model: {model}, language: {language}, pause_threshold: {pause_threshold}s, buffer: {self.buffer_duration}s")
    
    def transcribe_audio_chunk(self, audio_data: bytes) -> Optional[Dict]:
        """
        Transcribe an audio chunk using Deepgram's prerecorded API
        
        Args:
            audio_data: Raw audio bytes (PCM16, 24kHz, mono)
            
        Returns:
            Transcription result dictionary or None if failed
        """
        try:
            # Validate audio data
            if not audio_data or len(audio_data) == 0:
                logger.warning("Empty audio data, skipping transcription")
                return None
            
            # Check minimum size (at least 0.5 seconds of audio)
            min_size = 24000 * 2 * 0.5  # 24kHz * 2 bytes * 0.5 seconds = 24,000 bytes
            if len(audio_data) < min_size:
                logger.warning(f"Audio too short: {len(audio_data)} bytes ({len(audio_data) / (24000 * 2):.2f}s), minimum: {min_size} bytes (0.5s)")
                return None
            
            # Log audio info
            duration = len(audio_data) / (24000 * 2)
            logger.info(f"Transcribing audio: {len(audio_data)} bytes ({duration:.2f}s)")
            
            # Create a BytesIO object with WAV header for better compatibility
            import struct
            
            # Create WAV file in memory
            wav_buffer = io.BytesIO()
            
            # WAV header
            sample_rate = 24000
            num_channels = 1
            bits_per_sample = 16
            byte_rate = sample_rate * num_channels * bits_per_sample // 8
            block_align = num_channels * bits_per_sample // 8
            data_size = len(audio_data)
            
            # Write WAV header
            wav_buffer.write(b'RIFF')
            wav_buffer.write(struct.pack('<I', 36 + data_size))  # File size - 8
            wav_buffer.write(b'WAVE')
            wav_buffer.write(b'fmt ')
            wav_buffer.write(struct.pack('<I', 16))  # fmt chunk size
            wav_buffer.write(struct.pack('<H', 1))   # PCM format
            wav_buffer.write(struct.pack('<H', num_channels))
            wav_buffer.write(struct.pack('<I', sample_rate))
            wav_buffer.write(struct.pack('<I', byte_rate))
            wav_buffer.write(struct.pack('<H', block_align))
            wav_buffer.write(struct.pack('<H', bits_per_sample))
            wav_buffer.write(b'data')
            wav_buffer.write(struct.pack('<I', data_size))
            wav_buffer.write(audio_data)
            
            # Get WAV bytes
            wav_bytes = wav_buffer.getvalue()
            
            logger.info(f"Created WAV file: {len(wav_bytes)} bytes (including header)")
            
            # Transcribe the audio chunk using Deepgram SDK v5
            # All parameters must be keyword arguments
            response = self.deepgram.listen.v1.media.transcribe_file(
                request=wav_bytes,
                model=self.model,
                language=self.language,
                smart_format=settings.DEEPGRAM_SMART_FORMAT,
                punctuate=settings.DEEPGRAM_PUNCTUATE,
                diarize=settings.DEEPGRAM_DIARIZE,
            )
            
            # Extract transcript
            if response.results and response.results.channels:
                channel = response.results.channels[0]
                if channel.alternatives:
                    transcript = channel.alternatives[0].transcript
                    confidence = channel.alternatives[0].confidence
                    
                    if transcript.strip():
                        logger.info(f"Transcript: '{transcript}' (confidence: {confidence:.2f})")
                        return {
                            "transcript": transcript,
                            "confidence": confidence,
                            "timestamp": datetime.now().isoformat()
                        }
            
            logger.info("No transcript returned from Deepgram")
            return None
            
        except Exception as e:
            logger.error(f"Error transcribing audio chunk: {str(e)}")
            return None
    
    def should_create_new_message(self) -> bool:
        """
        Check if we should create a new message based on pause detection
        
        Returns:
            True if pause threshold exceeded
        """
        if self.last_audio_time is None:
            return True
        
        time_since_last_audio = time.time() - self.last_audio_time
        return time_since_last_audio >= self.pause_threshold
    
    def process_audio_chunk(self, audio_data: bytes, source: str = "microphone") -> Optional[Dict]:
        """
        Process audio chunk - create new message only on PAUSE, not on source flicker
        """
        current_time = time.time()
        
        # CREATE FIRST MESSAGE if none exists
        if self.current_message is None:
            self.message_counter += 1
            self.current_message = AudioMessage.from_source(
                message_id=self.message_counter,
                source=source
            )
            self.messages.append(self.current_message)
            self.current_source = source
            logger.info(f"Created FIRST message: {self.current_message.message_id} (role: {self.current_message.role}, source: {source})")
        
        # CHECK FOR ACTUAL PAUSE (not just source change)
        pause_detected = (
            self.last_audio_time is not None and 
            (current_time - self.last_audio_time) >= self.pause_threshold
        )
        
        # CHECK FOR SOURCE CHANGE (but only if accumulated text)
        source_changed = (
            self.current_source is not None and 
            self.current_source != source and
            self.current_message is not None and
            len(self.current_message.get_full_transcript().strip()) > 20  # Only if substantial text
        )
        
        # Create new message ONLY if pause OR significant source change with content
        if pause_detected or source_changed:
            if self.current_message:
                self.current_message.is_final = True
                reason = "source change" if source_changed else f"pause ({self.pause_threshold}s)"
                logger.info(f"Finalized message {self.current_message.message_id} (role: {self.current_message.role}) after {reason}")

            # Create new message with proper role from source
            self.message_counter += 1
            self.current_message = AudioMessage.from_source(
                message_id=self.message_counter,
                source=source
            )
            self.messages.append(self.current_message)
            self.current_source = source
            logger.info(f"Created new message: {self.current_message.message_id} (role: {self.current_message.role}, source: {source})")

            # Clear buffer for new message
            self.audio_buffer = b''
            self.last_transcribe_time = None
        
        # Update timestamp
        self.last_audio_time = current_time
        
        # Add to buffer
        self.audio_buffer += audio_data
        
        # Transcribe every 2 seconds or when buffer fills
        should_transcribe = False
        
        if self.last_transcribe_time is None:
            should_transcribe = len(self.audio_buffer) >= self.min_buffer_size
        else:
            time_since_last = current_time - self.last_transcribe_time
            should_transcribe = time_since_last >= self.buffer_duration
        
        if should_transcribe and len(self.audio_buffer) > 0:
            result = self.transcribe_audio_chunk(self.audio_buffer)
            self.audio_buffer = b''
            self.last_transcribe_time = current_time

            if result and self.current_message:
                # Use add_transcript() to ACCUMULATE transcripts properly
                self.current_message.add_transcript(result["transcript"])

                is_new_segment = len(self.current_message.transcript_parts) == 1

                return {
                    "type": "transcript.updated",
                    "message": self.current_message.to_dict(),
                    "chunk": result,
                    "total_messages": len(self.messages),
                    "is_new_segment": is_new_segment
                }
        
        return None
    
    def flush_buffer(self) -> Optional[Dict]:
        """
        Force transcribe any remaining audio in buffer
        Useful when session is ending
        
        Returns:
            Message update dictionary or None
        """
        if len(self.audio_buffer) > 0 and self.current_message:
            logger.info(f"Flushing buffer: {len(self.audio_buffer)} bytes")
            
            result = self.transcribe_audio_chunk(self.audio_buffer)
            self.audio_buffer = b''
            self.last_transcribe_time = time.time()
            
            if result:
                # Add transcript (accumulates with previous)
                self.current_message.add_transcript(result["transcript"])
                return {
                    "type": "transcript.updated",
                    "message": self.current_message.to_dict(),
                    "chunk": result,
                    "total_messages": len(self.messages),
                    "is_new_segment": False
                }
        
        return None
    
    def get_all_messages(self) -> List[Dict]:
        """
        Get all messages in the session
        
        Returns:
            List of message dictionaries
        """
        return [msg.to_dict() for msg in self.messages]
    
    def get_current_message(self) -> Optional[Dict]:
        """
        Get the current active message
        
        Returns:
            Current message dictionary or None
        """
        if self.current_message:
            return self.current_message.to_dict()
        return None
    
    def finalize_current_message(self):
        """Finalize the current message"""
        if self.current_message:
            self.current_message.is_final = True
            logger.info(f"Manually finalized message: {self.current_message.message_id}")
    
    def reset_session(self):
        """Reset the session state"""
        if self.current_message:
            self.current_message.is_final = True
        self.messages = []
        self.current_message = None
        self.last_audio_time = None
        self.message_counter = 0
        self.audio_buffer = b''
        self.last_transcribe_time = None
        self.current_source = None
        logger.info("Session reset")


class DeepgramSessionManager:
    """
    Manages multiple Deepgram transcription sessions
    
    Implements session lifecycle management for audio-to-text transcription
    """
    
    def __init__(self):
        """Initialize session manager"""
        self._active_sessions: Dict[str, DeepgramTranscriptionService] = {}
        logger.info("Initialized DeepgramSessionManager")
    
    def create_session(
        self,
        session_id: str,
        api_key: Optional[str] = None,
        security_manager: SecurityManager = None,
        model: str = settings.DEEPGRAM_MODEL,
        language: str = settings.DEEPGRAM_LANGUAGE,
        pause_threshold: float = 5.0
    ) -> DeepgramTranscriptionService:
        """
        Create a new transcription session
        
        Args:
            session_id: Unique session identifier
            api_key: Deepgram API key (uses default from settings if None)
            security_manager: Security manager
            model: Deepgram model
            language: Language code
            pause_threshold: Seconds of silence to trigger new message
            
        Returns:
            New transcription service instance
        """
        # Use default API key from settings if not provided
        effective_api_key = api_key or settings.DEEPGRAM_API_KEY
        service = DeepgramTranscriptionService(
            effective_api_key,
            security_manager,
            model,
            language,
            pause_threshold
        )
        self._active_sessions[session_id] = service
        logger.info(f"Created Deepgram session: {session_id}")
        return service
    
    def get_session(self, session_id: str) -> Optional[DeepgramTranscriptionService]:
        """
        Get an existing session
        
        Args:
            session_id: Session identifier
            
        Returns:
            Session service or None if not found
        """
        return self._active_sessions.get(session_id)
    
    def remove_session(self, session_id: str) -> None:
        """
        Remove a session
        
        Args:
            session_id: Session identifier
        """
        if session_id in self._active_sessions:
            session = self._active_sessions[session_id]
            # Flush remaining audio buffer before removing
            session.flush_buffer()
            # Finalize current message before removing
            session.finalize_current_message()
            del self._active_sessions[session_id]
            logger.info(f"Removed Deepgram session: {session_id}")
    
    def get_active_session_count(self) -> int:
        """
        Get number of active sessions
        
        Returns:
            Count of active sessions
        """
        return len(self._active_sessions)


# Global session manager
session_manager = DeepgramSessionManager()


# Factory function
def create_transcription_service(
    api_key: Optional[str] = None,
    security_manager: Optional[SecurityManager] = None,
    model: str = settings.DEEPGRAM_MODEL,
    language: str = settings.DEEPGRAM_LANGUAGE,
    pause_threshold: float = 5.0
) -> DeepgramTranscriptionService:
    """
    Factory function to create transcription service
    
    Args:
        api_key: Deepgram API key
        security_manager: Security manager
        model: Deepgram model
        language: Language code
        pause_threshold: Seconds of silence to trigger new message
        
    Returns:
        New transcription service instance
    """
    from security import security_manager as default_security
    
    return DeepgramTranscriptionService(
        api_key=api_key or settings.DEEPGRAM_API_KEY,
        security_manager=security_manager or default_security,
        model=model,
        language=language,
        pause_threshold=pause_threshold
    )
