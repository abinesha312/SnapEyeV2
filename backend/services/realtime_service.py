"""
Real-time Transcription Service
Handles WebSocket connections for live audio transcription
"""
import json
import logging
from typing import Dict, Optional

import sys
import os
sys.path.insert(0, os.path.dirname(os.path.dirname(os.path.abspath(__file__))))

from config import settings, model_config
from security import SecurityManager
from services.prompts import PromptTemplates
from models import VoiceType

# Configure logging
logger = logging.getLogger(__name__)


class RealtimeTranscriptionService:
    """
    Real-time audio transcription service using OpenAI Realtime API
    
    Handles WebSocket connections for live transcription and conversation
    """
    
    def __init__(
        self,
        api_key: str,
        security_manager: SecurityManager,
        voice: VoiceType = VoiceType.ALLOY
    ):
        """
        Initialize realtime transcription service
        
        Args:
            api_key: OpenAI API key
            security_manager: Security manager instance
            voice: Voice type for audio responses
        """
        self.api_key = api_key
        self.security = security_manager
        self.voice = voice
        self.ws_url = f"{settings.OPENAI_WS_URL}?model={settings.OPENAI_REALTIME_MODEL}"
        logger.info(f"Initialized RealtimeTranscriptionService with voice: {voice}")
    
    def get_headers(self) -> Dict[str, str]:
        """
        Get WebSocket connection headers
        
        Returns:
            Dictionary of required headers
        """
        return {
            "Authorization": f"Bearer {self.api_key}",
            "OpenAI-Beta": "realtime=v1"
        }
    
    def get_session_config(
        self,
        instructions: Optional[str] = None,
        temperature: float = model_config.TEMPERATURE_DEFAULT,
        max_tokens: int = model_config.MAX_TOKENS_DEFAULT
    ) -> Dict:
        """
        Get session configuration for WebSocket
        
        Args:
            instructions: Custom instructions for the AI
            temperature: Response creativity (0-2)
            max_tokens: Maximum tokens per response
            
        Returns:
            Session configuration dictionary
        """
        return {
            "type": "session.update",
            "session": {
                "modalities": ["text", "audio"],
                "instructions": instructions or PromptTemplates.TRANSCRIBE_SYSTEM,
                "voice": self.voice.value,
                "input_audio_format": model_config.AUDIO_INPUT_FORMAT,
                "output_audio_format": model_config.AUDIO_OUTPUT_FORMAT,
                "input_audio_transcription": {
                    "model": "whisper-1"
                },
                "turn_detection": {
                    "type": "server_vad",
                    "threshold": 0.5,
                    "prefix_padding_ms": 300,
                    "silence_duration_ms": 500
                },
                "temperature": temperature,
                "max_response_output_tokens": max_tokens
            }
        }
    
    def should_encrypt_message(self, message: str) -> bool:
        """
        Determine if a message contains sensitive data that should be encrypted
        
        Args:
            message: Message to check
            
        Returns:
            True if message should be encrypted
        """
        try:
            parsed = json.loads(message)
            message_type = parsed.get("type", "")
            
            # Encrypt transcript and conversation data
            sensitive_types = [
                "conversation.item.created",
                "response.audio_transcript.done",
                "response.text.done",
                "conversation.item.input_audio_transcription.completed"
            ]
            
            return message_type in sensitive_types
            
        except json.JSONDecodeError:
            return False
    
    def encrypt_message(self, message: str) -> str:
        """
        Encrypt a message
        
        Args:
            message: Message to encrypt
            
        Returns:
            Encrypted message as JSON string
        """
        try:
            encrypted = self.security.encrypt_data(message)
            return json.dumps({
                "type": "encrypted",
                "encrypted_data": encrypted,
                "timestamp": str(json.loads(message).get("timestamp", ""))
            })
        except Exception as e:
            logger.error(f"Encryption error: {str(e)}")
            return message
    
    def process_message(self, message: str, encrypt_sensitive: bool = True) -> str:
        """
        Process a message from OpenAI, optionally encrypting sensitive data
        
        Args:
            message: Message from OpenAI
            encrypt_sensitive: Whether to encrypt sensitive messages
            
        Returns:
            Processed (and potentially encrypted) message
        """
        if encrypt_sensitive and self.should_encrypt_message(message):
            return self.encrypt_message(message)
        return message
    
    def create_audio_append_event(self, audio_data: bytes) -> str:
        """
        Create an audio append event for sending to OpenAI
        
        Args:
            audio_data: Raw audio data
            
        Returns:
            JSON string for audio append event
        """
        import base64
        
        return json.dumps({
            "type": "input_audio_buffer.append",
            "audio": base64.b64encode(audio_data).decode('utf-8')
        })
    
    def create_response_create_event(self) -> str:
        """
        Create a response creation event
        
        Returns:
            JSON string for response create event
        """
        return json.dumps({
            "type": "response.create"
        })
    
    def create_conversation_item_create(self, text: str) -> str:
        """
        Create a conversation item
        
        Args:
            text: Text content
            
        Returns:
            JSON string for conversation item
        """
        return json.dumps({
            "type": "conversation.item.create",
            "item": {
                "type": "message",
                "role": "user",
                "content": [
                    {
                        "type": "input_text",
                        "text": text
                    }
                ]
            }
        })


class RealtimeSessionManager:
    """
    Manages multiple realtime transcription sessions
    
    Implements session lifecycle management and connection pooling
    """
    
    def __init__(self):
        """Initialize session manager"""
        self._active_sessions: Dict[str, RealtimeTranscriptionService] = {}
        logger.info("Initialized RealtimeSessionManager")
    
    def create_session(
        self,
        session_id: str,
        api_key: Optional[str] = None,
        security_manager: SecurityManager = None,
        voice: VoiceType = VoiceType.ALLOY
    ) -> RealtimeTranscriptionService:
        """
        Create a new transcription session
        
        Args:
            session_id: Unique session identifier
            api_key: OpenAI API key (uses default from settings if None)
            security_manager: Security manager
            voice: Voice type
            
        Returns:
            New transcription service instance
        """
        # Use default API key from settings if not provided
        effective_api_key = api_key or settings.OPENAI_API_KEY
        service = RealtimeTranscriptionService(effective_api_key, security_manager, voice)
        self._active_sessions[session_id] = service
        logger.info(f"Created session: {session_id}")
        return service
    
    def get_session(self, session_id: str) -> Optional[RealtimeTranscriptionService]:
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
            del self._active_sessions[session_id]
            logger.info(f"Removed session: {session_id}")
    
    def get_active_session_count(self) -> int:
        """
        Get number of active sessions
        
        Returns:
            Count of active sessions
        """
        return len(self._active_sessions)


# Global session manager
session_manager = RealtimeSessionManager()


# Factory function
def create_transcription_service(
    api_key: Optional[str] = None,
    security_manager: Optional[SecurityManager] = None,
    voice: VoiceType = VoiceType.ALLOY
) -> RealtimeTranscriptionService:
    """
    Factory function to create transcription service
    
    Args:
        api_key: OpenAI API key
        security_manager: Security manager
        voice: Voice type
        
    Returns:
        New transcription service instance
    """
    from security import security_manager as default_security
    
    return RealtimeTranscriptionService(
        api_key=api_key or settings.OPENAI_API_KEY,
        security_manager=security_manager or default_security,
        voice=voice
    )

