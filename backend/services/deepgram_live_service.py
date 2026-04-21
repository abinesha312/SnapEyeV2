"""
Deepgram Real-time Streaming Transcription Service
Implements Cluely-like behavior with:
- Real-time WebSocket streaming (not batch)
- Endpointing and interim results
- Small chunk support (200-300ms)
- Automatic reconnection
- Message segmentation based on endpointing + source changes
"""
import json
import logging
import asyncio
import time
from typing import Optional, Callable, Dict, List
from datetime import datetime
from deepgram import DeepgramClient
from config import settings

# Import AudioMessage for compatibility
import sys
import os
sys.path.insert(0, os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
from services.realtime_service import AudioMessage

logger = logging.getLogger(__name__)


class DeepgramLiveTranscriptionService:
    """
    Real-time streaming transcription with endpointing and interim results
    
    Features:
    - Uses Deepgram Live WebSocket API (wss://api.deepgram.com/v1/listen)
    - Supports 200-300ms audio chunks
    - Endpointing-based message segmentation
    - Interim results for fast UI updates
    - Automatic reconnection on failures
    - Message management with AudioMessage class
    """
    
    def __init__(
        self,
        api_key: Optional[str] = None,
        model: str = settings.DEEPGRAM_MODEL,
        language: str = settings.DEEPGRAM_LANGUAGE,
        endpointing_ms: int = 500,  # Endpointing threshold in milliseconds
        vad_events: bool = True,  # Voice Activity Detection events
    ):
        """
        Initialize Deepgram Live Transcription Service
        
        Args:
            api_key: Deepgram API key (uses settings default if None)
            model: Deepgram model (nova-3 recommended)
            language: Language code (en, es, fr, etc.)
            endpointing_ms: Milliseconds of silence to trigger endpoint (default: 500ms)
            vad_events: Enable Voice Activity Detection events
        """
        self.api_key = api_key or settings.DEEPGRAM_API_KEY
        self.model = model
        self.language = language
        self.endpointing_ms = endpointing_ms
        self.vad_events = vad_events
        
        self.deepgram = DeepgramClient(api_key=self.api_key)
        self.ws_connection = None
        self.ws_client = None
        self.is_connected = False
        self.reconnect_attempts = 0
        self.max_reconnect_attempts = 5
        self.reconnect_delay = 1.0  # Start with 1 second
        
        # Callbacks
        self.on_transcript_callback: Optional[Callable] = None
        self.on_error_callback: Optional[Callable] = None
        self.on_metadata_callback: Optional[Callable] = None
        self.on_ai_suggestion_callback: Optional[Callable] = None
        
        # Keyword detection and context management
        self._keyword_detector = None
        self._context_manager = None
        self._ai_pipeline_enabled = True
        
        # Message management
        self.messages: List[AudioMessage] = []
        self.current_message: Optional[AudioMessage] = None
        self.message_counter = 0
        
        # Source tracking
        self.current_source: Optional[str] = None
        self.previous_source: Optional[str] = None
        
        # Audio buffering for small chunks (200-300ms)
        self.audio_buffer: bytes = b''
        self.buffer_size_ms = 300  # Buffer up to 300ms before sending
        self.last_send_time: Optional[float] = None
        self.min_chunk_size = int(24000 * 2 * 0.1)  # 100ms minimum (24kHz * 2 bytes * 0.1s)
        
        # Endpointing state
        self.last_audio_time: Optional[float] = None
        self.is_speaking = False
        
        # Async tasks
        self.receive_task: Optional[asyncio.Task] = None
        self.buffer_flush_task: Optional[asyncio.Task] = None
        
        logger.info(f"Initialized DeepgramLiveTranscriptionService (model: {model}, endpointing: {endpointing_ms}ms)")
    
    async def connect(
        self,
        on_transcript: Callable,
        on_error: Callable,
        on_metadata: Optional[Callable] = None
    ) -> bool:
        """
        Connect to Deepgram Live WebSocket API
        
        Args:
            on_transcript: Callback for transcript updates (interim + final)
            on_error: Callback for errors
            on_metadata: Optional callback for metadata events
            
        Returns:
            True if connected successfully
        """
        try:
            self.on_transcript_callback = on_transcript
            self.on_error_callback = on_error
            self.on_metadata_callback = on_metadata
            
            logger.info("Connecting to Deepgram Live WebSocket...")
            
            # Configure Deepgram Live connection
            # Use minimal parameters first to avoid HTTP 400 errors
            # Deepgram SDK v5 connect() method signature may differ
            self.ws_connection = self.deepgram.listen.v1.connect(
                model=self.model,
                language=self.language,
                interim_results=True,
                encoding="linear16",
                sample_rate=24000,
                channels=1,
            )
            
            self.ws_client = self.ws_connection.__enter__()
            self.is_connected = True
            self.reconnect_attempts = 0
            self.reconnect_delay = 1.0
            
            logger.info("Connected to Deepgram Live WebSocket")
            
            # Start receive loop
            self.receive_task = asyncio.create_task(self._receive_loop())
            
            # Start buffer flush task (sends buffered audio periodically)
            self.buffer_flush_task = asyncio.create_task(self._buffer_flush_loop())
            
            return True
            
        except Exception as e:
            error_msg = str(e)
            logger.error(f"Connection error: {error_msg}")
            logger.error(f"Error type: {type(e).__name__}")
            
            # Log more details for HTTP 400 errors
            if "HTTP 400" in error_msg or "InvalidStatus" in error_msg:
                logger.error("Deepgram API rejected connection with HTTP 400")
                logger.error("This usually means:")
                logger.error("1. Invalid API key")
                logger.error("2. Unsupported parameter format")
                logger.error("3. Missing required parameters")
                logger.error(f"API Key present: {bool(self.api_key)}")
                logger.error(f"API Key length: {len(self.api_key) if self.api_key else 0}")
                logger.error(f"Model: {self.model}")
                logger.error(f"Language: {self.language}")
            
            self.is_connected = False
            if self.on_error_callback:
                await self.on_error_callback(f"Connection failed: {error_msg}")
            return False
    
    async def send_audio(self, audio_data: bytes, source: str = "microphone") -> bool:
        """
        Send audio chunk to Deepgram (buffers small chunks for efficiency)
        
        Args:
            audio_data: PCM16 audio bytes (24kHz, mono)
            source: "microphone" or "speaker"
            
        Returns:
            True if sent successfully
        """
        if not self.is_connected or not self.ws_client:
            logger.warning("Not connected, buffering audio")
            self.audio_buffer += audio_data
            return False
        
        try:
            if len(audio_data) == 0:
                return False
            
            # Update source tracking
            self.current_source = source
            self.last_audio_time = time.time()
            
            # Add to buffer
            self.audio_buffer += audio_data
            
            # Send if buffer is large enough or enough time has passed
            buffer_duration_ms = (len(self.audio_buffer) / (24000 * 2)) * 1000
            
            if len(self.audio_buffer) >= self.min_chunk_size:
                # Send buffered audio
                if self.audio_buffer:
                    self.ws_client.send_media(self.audio_buffer)
                    logger.debug(f"Sent {len(self.audio_buffer)} bytes ({buffer_duration_ms:.0f}ms) from {source}")
                    self.audio_buffer = b''
                    self.last_send_time = time.time()
                    return True
            
            return True
            
        except Exception as e:
            logger.error(f"Send error: {e}")
            # Try to reconnect
            await self._handle_reconnect()
            return False
    
    async def _buffer_flush_loop(self):
        """
        Periodically flush audio buffer to ensure low latency
        Flushes every 200-300ms even if buffer is small
        """
        while self.is_connected:
            try:
                await asyncio.sleep(self.buffer_size_ms / 1000.0)  # Convert ms to seconds
                
                if self.audio_buffer and len(self.audio_buffer) > 0:
                    if self.is_connected and self.ws_client:
                        try:
                            self.ws_client.send_media(self.audio_buffer)
                            logger.debug(f"Flushed buffer: {len(self.audio_buffer)} bytes")
                            self.audio_buffer = b''
                            self.last_send_time = time.time()
                        except Exception as e:
                            logger.error(f"Buffer flush error: {e}")
                            await self._handle_reconnect()
                            
            except asyncio.CancelledError:
                break
            except Exception as e:
                logger.error(f"Buffer flush loop error: {e}")
    
    async def _receive_loop(self):
        """
        Receive messages from Deepgram WebSocket
        Handles transcripts, metadata, errors, and endpointing events
        """
        try:
            logger.info("Starting receive loop...")
            
            while self.is_connected and self.ws_client:
                try:
                    # Receive message (non-blocking with timeout)
                    message = await asyncio.to_thread(self.ws_client.recv)
                    
                    if message:
                        await self._process_message(message)
                        
                except Exception as e:
                    if self.is_connected:
                        logger.error(f"Receive error: {e}")
                        # Try to reconnect
                        await self._handle_reconnect()
                    break
                    
        except asyncio.CancelledError:
            logger.info("Receive loop cancelled")
        except Exception as e:
            logger.exception(f"Receive loop error: {e}")
        finally:
            logger.info("Receive loop ended")
    
    async def _process_message(self, message):
        """
        Process message from Deepgram WebSocket
        
        Handles:
        - Results (transcripts with interim/final flags)
        - Metadata (connection info)
        - SpeechStarted/SpeechEnded (VAD events)
        - UtteranceEnd (endpointing)
        - Error messages
        """
        try:
            # Parse message
            if isinstance(message, str):
                data = json.loads(message)
            elif isinstance(message, bytes):
                data = json.loads(message.decode('utf-8'))
            elif hasattr(message, "model_dump"):
                # Deepgram SDK v5 returns Pydantic event objects from recv()
                data = message.model_dump()
            elif hasattr(message, "dict"):
                data = message.dict()
            else:
                data = message
            
            msg_type = data.get("type", "")
            
            if msg_type == "Results":
                await self._handle_transcript(data)
            elif msg_type == "Metadata":
                logger.debug(f"Metadata: {data}")
                if self.on_metadata_callback:
                    await self.on_metadata_callback(data)
            elif msg_type == "SpeechStarted":
                self.is_speaking = True
                logger.debug("Speech started")
            elif msg_type == "SpeechEnded":
                self.is_speaking = False
                logger.debug("Speech ended")
            elif msg_type == "UtteranceEnd":
                # Endpointing detected - finalize current message
                await self._handle_endpoint()
            elif msg_type == "Error":
                error_msg = data.get("error", "Unknown error")
                logger.error(f"Deepgram error: {error_msg}")
                if self.on_error_callback:
                    await self.on_error_callback(error_msg)
            else:
                logger.debug(f"Unknown message type: {msg_type}")
                
        except Exception as e:
            logger.exception(f"Message processing error: {e}")
    
    async def _handle_transcript(self, data):
        """
        Handle transcript result (interim or final)
        
        Creates/updates messages based on:
        - Endpointing (UtteranceEnd)
        - Source changes (microphone <-> speaker)
        - Interim vs final results
        """
        try:
            channel = data.get("channel", {})
            alternatives = channel.get("alternatives", [])
            
            if not alternatives:
                return
            
            alternative = alternatives[0]
            transcript = alternative.get("transcript", "")
            
            if not transcript or not transcript.strip():
                return
            
            is_final = data.get("is_final", False)
            confidence = alternative.get("confidence", 0.0)
            
            # Check for source change
            source_changed = (
                self.current_source and 
                self.previous_source and 
                self.current_source != self.previous_source
            )
            
            # Create new message if:
            # 1. No current message exists
            # 2. Source changed (microphone <-> speaker)
            # 3. Final result and endpointing detected (handled in _handle_endpoint)
            
            if self.current_message is None or source_changed:
                # Finalize previous message if exists
                if self.current_message:
                    self.current_message.is_final = True
                    logger.info(f"Finalized message {self.current_message.message_id} (source change)")
                
                # Create new message
                self.message_counter += 1
                self.current_message = AudioMessage.from_source(
                    message_id=self.message_counter,
                    source=self.current_source or "microphone"
                )
                self.messages.append(self.current_message)
                self.previous_source = self.current_source
                
                logger.info(f"Created message {self.current_message.message_id} (source: {self.current_source})")
            
            # Add transcript to current message
            if self.current_message:
                if is_final:
                    # Final transcript replaces interim
                    self.current_message.set_transcript(transcript)
                else:
                    # Interim transcript accumulates
                    self.current_message.add_transcript(transcript)
            
            # Determine if this is a new segment (for UI updates)
            is_new_segment = (
                self.current_message and 
                len(self.current_message.transcript_parts) == 1 and
                is_final
            )
            
            status = "FINAL" if is_final else "INTERIM"
            logger.debug(f"[{status}] {transcript[:50]}... (source={self.current_source}, conf={confidence:.2f})")
            
            # Send to callback
            if self.on_transcript_callback:
                await self.on_transcript_callback({
                    "type": "transcript.updated" if not is_final else "transcript.final",
                    "transcript": transcript,
                    "is_final": is_final,
                    "is_new_segment": is_new_segment,
                    "source": self.current_source or "microphone",
                    "confidence": confidence,
                    "message": self.current_message.to_dict() if self.current_message else None,
                    "total_messages": len(self.messages)
                })
            
            # Only feed FINAL transcripts into context manager to avoid duplicates
            # (interim transcripts would add the same text multiple times)
            if is_final and self._context_manager:
                self._context_manager.add_transcript(
                    text=transcript,
                    source=self.current_source or "microphone",
                    is_final=is_final,
                    message_id=str(self.current_message.message_id) if self.current_message else "",
                )
            
            # Run keyword/question detection on final segments
            if is_final and self._ai_pipeline_enabled and self._keyword_detector:
                await self._run_detection_pipeline(transcript)
                
        except Exception as e:
            logger.exception(f"Transcript handling error: {e}")
    
    async def _handle_endpoint(self):
        """
        Handle endpointing event (UtteranceEnd)
        Finalizes current message and prepares for next segment
        """
        try:
            if self.current_message:
                self.current_message.is_final = True
                logger.info(f"Endpointed message {self.current_message.message_id}")
                
                # Notify callback
                if self.on_transcript_callback:
                    await self.on_transcript_callback({
                        "type": "message.finalized",
                        "message": self.current_message.to_dict(),
                        "total_messages": len(self.messages),
                        "reason": "endpointing"
                    })
                
                # Reset for next message (will be created on next transcript)
                self.current_message = None
                
        except Exception as e:
            logger.exception(f"Endpoint handling error: {e}")
    
    async def _run_detection_pipeline(self, transcript: str):
        """
        Run keyword/question detection on a final transcript segment.
        If triggered, build context and stream AI suggestion back to client.
        """
        try:
            detection = self._keyword_detector.detect(transcript, is_final=True)
            
            if not detection.triggered:
                return
            
            logger.info(
                f"AI pipeline triggered: {detection.trigger_type} "
                f"(questions={detection.questions}, keywords={detection.keywords_matched})"
            )
            
            # Determine the question to answer
            # Use the full transcript as context, not just the extracted question fragment
            question = ""
            if detection.questions:
                question = detection.questions[0]
            elif detection.keywords_matched:
                question = f"The topic '{detection.keywords_matched[0]}' was mentioned: {transcript}"
            else:
                question = transcript
            
            # If the question is very short or vague, use the full transcript
            if len(question.strip()) < 15:
                question = transcript
            
            logger.info(f"AI question to answer: '{question}'")
            
            # Search RAG for relevant context
            rag_chunks = []
            try:
                from services.rag_service import rag_service
                rag_chunks = await rag_service.search(question, top_k=3)
            except Exception as e:
                logger.debug(f"RAG search skipped: {e}")
            
            # Build LLM context
            if self._context_manager:
                context = self._context_manager.build_llm_context(
                    question=question,
                    rag_chunks=rag_chunks,
                )
                logger.info(
                    f"Context built - transcript_context length: {len(context.get('transcript_context', ''))}, "
                    f"user_message length: {len(context.get('user_message', ''))}"
                )
            else:
                context = {
                    "system_prompt": (
                        "You are SnapEye, a real-time AI assistant. "
                        "Answer the user's question helpfully and concisely."
                    ),
                    "user_message": question,
                }
                logger.warning("No context manager available, using question only")
            
            # Stream AI suggestion via callback
            if self.on_ai_suggestion_callback:
                await self.on_ai_suggestion_callback({
                    "type": "ai.suggestion.start",
                    "question": question,
                    "detection": detection.to_dict(),
                })
                
                try:
                    from services.llm_router import llm_router, LLMMessage
                    
                    messages = [LLMMessage(role="user", content=context["user_message"])]
                    
                    logger.info(f"Sending to LLM - system_prompt: {context['system_prompt'][:100]}...")
                    logger.info(f"Sending to LLM - user_message: {context['user_message'][:200]}...")
                    
                    async for token in llm_router.generate_stream(
                        messages,
                        system_prompt=context["system_prompt"],
                        max_tokens=2048,
                        temperature=0.7,
                    ):
                        await self.on_ai_suggestion_callback({
                            "type": "ai.suggestion.token",
                            "token": token,
                        })
                    
                    await self.on_ai_suggestion_callback({
                        "type": "ai.suggestion.end",
                    })
                    
                except Exception as e:
                    logger.error(f"AI suggestion streaming error: {e}")
                    await self.on_ai_suggestion_callback({
                        "type": "ai.suggestion.error",
                        "error": str(e),
                    })
        
        except Exception as e:
            logger.error(f"Detection pipeline error: {e}")
    
    def enable_ai_pipeline(
        self,
        keyword_detector=None,
        context_manager=None,
        on_ai_suggestion=None,
    ):
        """
        Enable the AI auto-trigger pipeline.
        Call after connect() to wire up keyword detection and AI suggestions.
        """
        self._keyword_detector = keyword_detector
        self._context_manager = context_manager
        self.on_ai_suggestion_callback = on_ai_suggestion
        self._ai_pipeline_enabled = True
        logger.info("AI pipeline enabled on transcription service")
    
    async def _handle_reconnect(self):
        """
        Handle reconnection on failure
        Implements exponential backoff
        """
        if self.reconnect_attempts >= self.max_reconnect_attempts:
            logger.error(f"Max reconnection attempts ({self.max_reconnect_attempts}) reached")
            if self.on_error_callback:
                await self.on_error_callback("Max reconnection attempts reached")
            return
        
        self.reconnect_attempts += 1
        delay = min(self.reconnect_delay * (2 ** (self.reconnect_attempts - 1)), 30.0)
        
        logger.info(f"Reconnecting (attempt {self.reconnect_attempts}/{self.max_reconnect_attempts}) in {delay}s...")
        
        # Disconnect first
        await self.disconnect()
        
        # Wait before reconnecting
        await asyncio.sleep(delay)
        
        # Reconnect
        if self.on_transcript_callback and self.on_error_callback:
            success = await self.connect(
                self.on_transcript_callback,
                self.on_error_callback,
                self.on_metadata_callback
            )
            if success:
                logger.info("Reconnected successfully")
            else:
                logger.error("Reconnection failed")
    
    async def disconnect(self):
        """Disconnect from Deepgram WebSocket"""
        if not self.is_connected:
            return
        
        self.is_connected = False
        
        try:
            # Cancel tasks
            if self.receive_task:
                self.receive_task.cancel()
                try:
                    await self.receive_task
                except asyncio.CancelledError:
                    pass
            
            if self.buffer_flush_task:
                self.buffer_flush_task.cancel()
                try:
                    await self.buffer_flush_task
                except asyncio.CancelledError:
                    pass
            
            # Flush any remaining audio
            if self.audio_buffer and self.ws_client:
                try:
                    self.ws_client.send_media(self.audio_buffer)
                    self.audio_buffer = b''
                except Exception:
                    pass
            
            # Close connection
            if self.ws_connection:
                self.ws_connection.__exit__(None, None, None)
            
            self.ws_client = None
            self.ws_connection = None
            
            # Finalize current message
            if self.current_message:
                self.current_message.is_final = True
            
            # Reset tracking so reconnect starts fresh
            self.current_message = None
            self.current_source = None
            self.previous_source = None
            
            logger.info("Disconnected from Deepgram")
            
        except Exception as e:
            logger.error(f"Disconnect error: {e}")
    
    def get_all_messages(self) -> List[Dict]:
        """Get all messages in the session"""
        return [msg.to_dict() for msg in self.messages]
    
    def get_current_message(self) -> Optional[Dict]:
        """Get the current active message"""
        if self.current_message:
            return self.current_message.to_dict()
        return None
    
    def finalize_current_message(self):
        """Manually finalize the current message"""
        if self.current_message:
            self.current_message.is_final = True
            logger.info(f"Manually finalized message: {self.current_message.message_id}")
    
    def reset_session(self):
        """Reset the session state"""
        if self.current_message:
            self.current_message.is_final = True
        self.messages = []
        self.current_message = None
        self.message_counter = 0
        self.current_source = None
        self.previous_source = None
        logger.info("Session reset")


class DeepgramSessionManager:
    """
    Manages multiple Deepgram Live transcription sessions
    """
    
    def __init__(self):
        self._sessions: Dict[str, DeepgramLiveTranscriptionService] = {}
        logger.info("Initialized DeepgramSessionManager")
    
    async def create_session(
        self,
        session_id: str,
        on_transcript: Callable,
        on_error: Callable,
        api_key: Optional[str] = None,
        model: str = settings.DEEPGRAM_MODEL,
        language: str = settings.DEEPGRAM_LANGUAGE,
        endpointing_ms: int = 500,
        vad_events: bool = True
    ) -> Optional[DeepgramLiveTranscriptionService]:
        """
        Create a new Deepgram Live session
        
        Args:
            session_id: Unique session identifier
            on_transcript: Callback for transcript updates
            on_error: Callback for errors
            api_key: Deepgram API key (uses default if None)
            model: Deepgram model
            language: Language code
            endpointing_ms: Endpointing threshold in milliseconds
            vad_events: Enable VAD events
            
        Returns:
            Service instance or None if creation failed
        """
        try:
            service = DeepgramLiveTranscriptionService(
                api_key=api_key,
                model=model,
                language=language,
                endpointing_ms=endpointing_ms,
                vad_events=vad_events
            )
            
            connected = await service.connect(on_transcript, on_error)
            
            if connected:
                self._sessions[session_id] = service
                logger.info(f"Created session: {session_id}")
                return service
            
            return None
            
        except Exception as e:
            logger.exception(f"Session creation error: {e}")
            return None
    
    def get_session(self, session_id: str) -> Optional[DeepgramLiveTranscriptionService]:
        """Get an existing session"""
        return self._sessions.get(session_id)
    
    async def remove_session(self, session_id: str):
        """Remove a session"""
        if session_id in self._sessions:
            service = self._sessions[session_id]
            await service.disconnect()
            del self._sessions[session_id]
            logger.info(f"Removed session: {session_id}")
    
    def get_active_count(self) -> int:
        """Get number of active sessions"""
        return len(self._sessions)


# Global session manager
deepgram_session_manager = DeepgramSessionManager()
