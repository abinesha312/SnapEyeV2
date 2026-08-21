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
import re
import difflib
from typing import Optional, Callable, Dict, List, Tuple
from datetime import datetime
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
        endpointing_ms: int = 300,  # Endpointing threshold in milliseconds
        vad_events: bool = True,  # Voice Activity Detection events
        source: str = "microphone",  # FIXED audio source for this connection
        id_offset: int = 0,  # Starting offset for message IDs (keeps streams unique)
        username: Optional[str] = None,  # Owning user, for per-user RAG/experience scoping
    ):
        """
        Initialize Deepgram Live Transcription Service

        Each instance owns ONE Deepgram WebSocket and transcribes exactly ONE audio
        source (microphone OR speaker). Mixing two sources onto a single stream makes
        the audio interleave and the source attribution unreliable, so callers should
        create one instance per source (see DualDeepgramSession).

        Args:
            api_key: Deepgram API key (uses settings default if None)
            model: Deepgram model (nova-3 recommended)
            language: Language code (en, es, fr, etc.)
            endpointing_ms: Milliseconds of silence to trigger endpoint (default: 300ms)
            vad_events: Enable Voice Activity Detection events
            source: The fixed source label ("microphone" or "speaker") for every
                transcript produced by this connection.
            id_offset: Base value for this stream's message counter so message IDs
                never collide with the other stream (e.g. speaker uses a large offset).
        """
        self.api_key = api_key or settings.DEEPGRAM_API_KEY
        self.model = model
        self.language = language
        self.endpointing_ms = endpointing_ms
        self.vad_events = vad_events
        # This connection's fixed source — never changes for the lifetime of the stream.
        self.source = source if source in ("microphone", "speaker") else "microphone"
        self.id_offset = id_offset
        self.username = username

        # Deferred import: the deepgram-sdk import chain is expensive and should not
        # happen at process/module import time - only when a real session is created.
        from deepgram import DeepgramClient
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
        # Optional shared lock so only one AI answer streams at a time across BOTH
        # the mic and speaker streams (set by DualDeepgramSession.enable_ai_pipeline).
        self._ai_lock: Optional[asyncio.Lock] = None
        # Echo-suppression hooks (wired by DualDeepgramSession):
        #  _echo_check(text) -> True if this (mic) transcript is an echo of speaker
        #    audio the microphone picked up, and should be dropped entirely.
        #  _record_final(text) -> store a (speaker) final so the mic side can compare.
        self._echo_check: Optional[Callable[[str], bool]] = None
        self._record_final: Optional[Callable[[str], None]] = None
        
        # Message management
        self.messages: List[AudioMessage] = []
        self.current_message: Optional[AudioMessage] = None
        # Start the counter at the stream's offset so mic/speaker IDs never collide.
        self.message_counter = id_offset
        # Finalized segments of the current message. Interim results are cumulative
        # per segment, so the displayed text is always: finals + latest interim.
        self.current_final_parts: List[str] = []
        
        # Audio buffering for small chunks (100ms)
        self.audio_buffer: bytes = b''
        self.buffer_size_ms = 100  # Buffer up to 100ms before sending
        self.last_send_time: Optional[float] = None
        self.min_chunk_size = int(24000 * 2 * 0.05)  # 50ms minimum (24kHz * 2 bytes * 0.05s)
        
        # Endpointing state
        self.last_audio_time: Optional[float] = None
        self.is_speaking = False
        
        # Async tasks
        self.receive_task: Optional[asyncio.Task] = None
        self.buffer_flush_task: Optional[asyncio.Task] = None
        self.ai_task: Optional[asyncio.Task] = None
        # Periodically sends a Deepgram KeepAlive when the stream is idle (no audio)
        # so Deepgram doesn't close the connection with a 1011 timeout. Without this,
        # an idle source (silent speaker, or a muted mic) drops the connection and
        # audio is lost across the reconnect.
        self.keepalive_task: Optional[asyncio.Task] = None
        # Seconds of no-audio before we start sending KeepAlive frames.
        self.keepalive_idle_seconds = 5.0
        
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
                # Low-latency endpointing: speech_final fires after endpointing_ms of
                # silence; UtteranceEnd needs utterance_end_ms (min 1000) to be emitted.
                endpointing=self.endpointing_ms,
                utterance_end_ms=max(1000, self.endpointing_ms),
                vad_events=self.vad_events,
                smart_format=settings.DEEPGRAM_SMART_FORMAT,
                punctuate=settings.DEEPGRAM_PUNCTUATE,
            )
            
            self.ws_client = self.ws_connection.__enter__()
            self.is_connected = True
            self.reconnect_attempts = 0
            self.reconnect_delay = 1.0
            # Treat the connect moment as the last send so the keepalive timer is sane.
            self.last_send_time = time.time()
            
            logger.info("Connected to Deepgram Live WebSocket")
            
            # Start receive loop
            self.receive_task = asyncio.create_task(self._receive_loop())
            
            # Start buffer flush task (sends buffered audio periodically)
            self.buffer_flush_task = asyncio.create_task(self._buffer_flush_loop())

            # Start keepalive loop (prevents 1011 idle-timeout disconnects)
            self.keepalive_task = asyncio.create_task(self._keepalive_loop())
            
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
    
    async def send_audio(self, audio_data: bytes, source: Optional[str] = None) -> bool:
        """
        Send audio chunk to Deepgram (buffers small chunks for efficiency)

        Args:
            audio_data: PCM16 audio bytes (24kHz, mono)
            source: Ignored — this connection always transcribes self.source. The
                parameter is kept for call-site compatibility only.

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

            self.last_audio_time = time.time()
            
            # Add to buffer
            self.audio_buffer += audio_data
            
            # Send if buffer is large enough or enough time has passed
            buffer_duration_ms = (len(self.audio_buffer) / (24000 * 2)) * 1000
            
            if len(self.audio_buffer) >= self.min_chunk_size:
                # Send buffered audio
                if self.audio_buffer:
                    self.ws_client.send_media(self.audio_buffer)
                    logger.debug(f"Sent {len(self.audio_buffer)} bytes ({buffer_duration_ms:.0f}ms) from {self.source}")
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

    async def _keepalive_loop(self):
        """
        Send a Deepgram KeepAlive whenever the stream has been idle (no audio sent)
        for longer than keepalive_idle_seconds. This stops Deepgram from closing an
        idle connection with a 1011 timeout — which previously caused constant
        reconnects on the speaker stream during silence (and would also affect a
        muted microphone), dropping audio across the gap.
        """
        # Check a bit more often than the idle threshold so we react promptly.
        interval = max(1.0, self.keepalive_idle_seconds / 2.0)
        while self.is_connected:
            try:
                await asyncio.sleep(interval)
                if not self.is_connected or not self.ws_client:
                    continue
                last = self.last_send_time or 0.0
                if (time.time() - last) >= self.keepalive_idle_seconds:
                    try:
                        from deepgram.extensions.types.sockets.listen_v1_control_message import (
                            ListenV1ControlMessage,
                        )
                        self.ws_client.send_control(
                            ListenV1ControlMessage(type="KeepAlive")
                        )
                        # Count the keepalive as a send so we don't spam every tick.
                        self.last_send_time = time.time()
                        logger.debug(f"Sent KeepAlive ({self.source})")
                    except Exception as e:
                        logger.error(f"KeepAlive send error ({self.source}): {e}")
                        await self._handle_reconnect()
            except asyncio.CancelledError:
                break
            except Exception as e:
                logger.error(f"Keepalive loop error: {e}")
    
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
            speech_final = data.get("speech_final", False)
            confidence = alternative.get("confidence", 0.0)

            # Echo suppression: when the microphone physically picks up the speaker
            # output, the other party's words are transcribed on BOTH streams. Drop
            # the mic copy entirely (no client, no context, no AI) when it matches a
            # recent speaker final. The speaker side records its finals for comparison.
            if self._echo_check is not None:
                try:
                    if self._echo_check(transcript):
                        logger.debug(f"Suppressed echo on mic stream: {transcript[:50]}")
                        return
                except Exception as e:
                    logger.debug(f"Echo check error: {e}")
            if is_final and self._record_final is not None:
                try:
                    self._record_final(transcript)
                except Exception as e:
                    logger.debug(f"Record-final error: {e}")

            # This connection has a single fixed source, so a new message is started
            # only when there is no in-progress message (i.e. after the previous one was
            # endpointed). The two sources can never be confused because they live on
            # separate Deepgram connections.
            if self.current_message is None:
                self.message_counter += 1
                self.current_message = AudioMessage.from_source(
                    message_id=self.message_counter,
                    source=self.source,
                )
                self.messages.append(self.current_message)
                self.current_final_parts = []

                logger.info(f"Created message {self.current_message.message_id} (source: {self.source})")

            # Build message text without duplicating words:
            # Deepgram interim results are CUMULATIVE for the in-progress segment,
            # so an interim must REPLACE the pending part (never be appended).
            # Each final covers only its own segment, so finals accumulate once each.
            if is_final:
                # Guard against Deepgram re-emitting the SAME final segment (it can send
                # the segment again when speech_final/endpointing fires). Appending it a
                # second time is exactly what produced the "last sentence shown twice"
                # bug, so only append when it isn't an exact repeat of the last part.
                if not self.current_final_parts or self.current_final_parts[-1] != transcript:
                    self.current_final_parts.append(transcript)
                display_text = " ".join(self.current_final_parts)
            else:
                display_text = " ".join(self.current_final_parts + [transcript])

            if self.current_message:
                self.current_message.set_transcript(display_text)

            # Determine if this is a new segment (for UI updates)
            is_new_segment = is_final and len(self.current_final_parts) == 1

            status = "FINAL" if is_final else "INTERIM"
            logger.debug(f"[{status}] {transcript[:50]}... (source={self.source}, conf={confidence:.2f})")

            # Send to callback (full message text so the UI can render it directly)
            if self.on_transcript_callback:
                await self.on_transcript_callback({
                    "type": "transcript.updated" if not is_final else "transcript.final",
                    "transcript": display_text,
                    "is_final": is_final,
                    "is_new_segment": is_new_segment,
                    "source": self.source,
                    "confidence": confidence,
                    "message": self.current_message.to_dict() if self.current_message else None,
                    "total_messages": len(self.messages)
                })
            
            # Only feed FINAL transcripts into context manager to avoid duplicates
            # (interim transcripts would add the same text multiple times)
            if is_final and self._context_manager:
                self._context_manager.add_transcript(
                    text=transcript,
                    source=self.source,
                    is_final=is_final,
                    message_id=str(self.current_message.message_id) if self.current_message else "",
                )
            
            # Run keyword/question detection on final segments.
            # Runs as a background task so streaming the AI answer never blocks
            # the Deepgram receive loop (keeps transcripts real-time). Skipped if
            # a previous AI answer is still streaming to avoid interleaved output.
            if is_final and self._ai_pipeline_enabled and self._keyword_detector:
                if self.ai_task is None or self.ai_task.done():
                    self.ai_task = asyncio.create_task(self._run_detection_pipeline(transcript))
                else:
                    logger.debug("AI pipeline busy, skipping trigger for this segment")

            # Deepgram sets speech_final once endpointing silence is reached:
            # finalize the message so the next speech starts a fresh one.
            if speech_final:
                await self._handle_endpoint()

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
                finalized = self.current_message.to_dict()
                logger.info(f"Endpointed message {self.current_message.message_id}")

                # Reset for next message BEFORE notifying, so a callback failure
                # can never leak the old message into the next utterance
                self.current_message = None
                self.current_final_parts = []

                # Notify callback
                if self.on_transcript_callback:
                    await self.on_transcript_callback({
                        "type": "message.finalized",
                        "message": finalized,
                        "total_messages": len(self.messages),
                        "reason": "endpointing"
                    })

        except Exception as e:
            logger.exception(f"Endpoint handling error: {e}")
    
    async def _run_detection_pipeline(self, transcript: str):
        """
        Run keyword/question detection on a final transcript segment.
        If triggered, build context and stream AI suggestion back to client.
        """
        # If the other stream is already streaming an answer, skip this trigger so the
        # two streams never produce interleaved AI output.
        if self._ai_lock is not None and self._ai_lock.locked():
            logger.debug(f"AI busy on the other stream, skipping trigger ({self.source})")
            return

        if self._ai_lock is not None:
            await self._ai_lock.acquire()
        try:
            await self._run_detection_pipeline_inner(transcript)
        finally:
            if self._ai_lock is not None and self._ai_lock.locked():
                self._ai_lock.release()

    async def _run_detection_pipeline_inner(self, transcript: str):
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
            
            # Search RAG + the user's experience concurrently (with a timeout so a slow
            # vector store never delays the answer — real-time is the priority).
            rag_chunks = []
            experience_chunks = []
            memory_chunks = []
            experience_searched = False
            try:
                from services.rag_service import rag_service
                rag_task = rag_service.search(question, top_k=3, username=self.username)
                exp_task = rag_service.search_experience(question, top_k=4, username=self.username)
                mem_task = rag_service.search_user_memory(question, top_k=2, username=self.username or "")
                experience_searched = True
                rag_chunks, experience_chunks, memory_chunks = await asyncio.wait_for(
                    asyncio.gather(rag_task, exp_task, mem_task, return_exceptions=True),
                    timeout=0.8,
                )
                rag_chunks = rag_chunks if isinstance(rag_chunks, list) else []
                experience_chunks = experience_chunks if isinstance(experience_chunks, list) else []
                memory_chunks = memory_chunks if isinstance(memory_chunks, list) else []
            except asyncio.TimeoutError:
                logger.debug("RAG/experience search timed out (>800ms), skipping for latency")
            except Exception as e:
                logger.debug(f"RAG/experience search skipped: {e}")

            # Only ground the answer in experience matches that are actually relevant -
            # a low-confidence match is worse than no match (it invites the model to
            # stretch an unrelated bit of background into a fabricated answer).
            experience_chunks = [
                c for c in experience_chunks
                if getattr(c, "score", 0.0) >= settings.RAG_EXPERIENCE_MIN_SCORE
            ]

            # Build LLM context
            if self._context_manager:
                context = self._context_manager.build_llm_context(
                    question=question,
                    rag_chunks=rag_chunks,
                    experience_chunks=experience_chunks,
                    memory_chunks=memory_chunks,
                    experience_searched=experience_searched,
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
            
            # Stream AI suggestion via callback. Sending on a client that has already
            # disconnected mid-suggestion must never propagate out of this function -
            # it would otherwise abort detection for the rest of this session.
            if self.on_ai_suggestion_callback:
                try:
                    await self.on_ai_suggestion_callback({
                        "type": "ai.suggestion.start",
                        "question": question,
                        "detection": detection.to_dict(),
                    })
                except Exception as e:
                    logger.warning(f"ai.suggestion.start callback failed (client likely disconnected): {e}")
                    return

                try:
                    from services.llm_router import llm_router, LLMMessage

                    messages = [LLMMessage(role="user", content=context["user_message"])]

                    logger.info(f"Sending to LLM - system_prompt: {context['system_prompt'][:100]}...")
                    logger.info(f"Sending to LLM - user_message: {context['user_message'][:200]}...")

                    async for token in llm_router.generate_stream(
                        messages,
                        system_prompt=context["system_prompt"],
                        max_tokens=700,
                        temperature=0.7,
                    ):
                        try:
                            await self.on_ai_suggestion_callback({
                                "type": "ai.suggestion.token",
                                "token": token,
                            })
                        except Exception as e:
                            logger.warning(f"ai.suggestion.token callback failed, stopping stream: {e}")
                            return

                    from services.rag_service import confidence_from_chunks
                    confidence = confidence_from_chunks(
                        (experience_chunks or []) + (rag_chunks or [])
                    )
                    try:
                        await self.on_ai_suggestion_callback({
                            "type": "ai.suggestion.end",
                            "confidence": confidence,
                        })
                    except Exception as e:
                        logger.warning(f"ai.suggestion.end callback failed: {e}")

                except Exception as e:
                    logger.error(f"AI suggestion streaming error: {e}")
                    try:
                        await self.on_ai_suggestion_callback({
                            "type": "ai.suggestion.error",
                            "error": str(e),
                        })
                    except Exception as callback_error:
                        logger.warning(f"ai.suggestion.error callback failed: {callback_error}")
        
        except Exception as e:
            logger.error(f"Detection pipeline error: {e}")
    
    def enable_ai_pipeline(
        self,
        keyword_detector=None,
        context_manager=None,
        on_ai_suggestion=None,
        ai_lock: Optional[asyncio.Lock] = None,
    ):
        """
        Enable the AI auto-trigger pipeline.
        Call after connect() to wire up keyword detection and AI suggestions.

        ai_lock, when provided, is shared between the mic and speaker streams so only
        one AI answer is generated at a time (prevents interleaved/duplicate answers).
        """
        self._keyword_detector = keyword_detector
        self._context_manager = context_manager
        self.on_ai_suggestion_callback = on_ai_suggestion
        self._ai_lock = ai_lock
        self._ai_pipeline_enabled = True
        logger.info(f"AI pipeline enabled on transcription service (source={self.source})")

    async def cancel_ai(self) -> bool:
        """Cancel an in-flight AI suggestion for this stream. Cancelling the task unwinds
        _run_detection_pipeline's finally block, which releases the shared AI lock so the
        next question can be answered immediately. Returns True if a task was cancelled."""
        task = self.ai_task
        if task is None or task.done():
            return False
        task.cancel()
        try:
            await task
        except asyncio.CancelledError:
            pass
        except Exception as e:
            logger.debug(f"cancel_ai await error ({self.source}): {e}")
        return True

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

            if self.keepalive_task:
                self.keepalive_task.cancel()
                try:
                    await self.keepalive_task
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
            self.current_final_parts = []
            
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
        self.current_final_parts = []
        self.message_counter = self.id_offset
        logger.info(f"Session reset (source={self.source})")


class DualDeepgramSession:
    """
    A single logical transcription session backed by TWO independent Deepgram
    connections — one for the microphone and one for the speaker.

    Why two connections? Sending both sources over one Deepgram stream interleaves
    their audio (garbling fast/overlapping speech) and makes source attribution a
    guess ("whatever chunk was last sent"). With one connection per source, each
    stream carries clean mono audio and every transcript is reliably tagged with a
    fixed source, so the UI can always place the mic on the right (blue) and the
    speaker on the left (grey) without ever mixing them.

    This class mirrors the public surface of DeepgramLiveTranscriptionService so the
    route and session manager can treat it as a drop-in replacement.
    """

    # Large offset keeps speaker message IDs from ever colliding with mic IDs.
    SPEAKER_ID_OFFSET = 1_000_000

    # Echo-suppression tuning.
    ECHO_WINDOW_SECONDS = 6.0   # how long a speaker final stays comparable
    ECHO_SIMILARITY = 0.80      # similarity ratio above which mic text is an echo

    def __init__(
        self,
        api_key: Optional[str] = None,
        model: str = settings.DEEPGRAM_MODEL,
        language: str = settings.DEEPGRAM_LANGUAGE,
        endpointing_ms: int = 300,
        vad_events: bool = True,
        username: Optional[str] = None,
    ):
        self.mic = DeepgramLiveTranscriptionService(
            api_key=api_key,
            model=model,
            language=language,
            endpointing_ms=endpointing_ms,
            vad_events=vad_events,
            source="microphone",
            id_offset=0,
            username=username,
        )
        self.speaker = DeepgramLiveTranscriptionService(
            api_key=api_key,
            model=model,
            language=language,
            endpointing_ms=endpointing_ms,
            vad_events=vad_events,
            source="speaker",
            id_offset=self.SPEAKER_ID_OFFSET,
            username=username,
        )
        # Shared so only one AI answer streams at a time across both streams.
        self._ai_lock = asyncio.Lock()

        # Recent speaker finals (normalized_text, timestamp) for echo comparison.
        self._recent_speaker: List[Tuple[str, float]] = []
        # Wire echo suppression: mic transcripts are checked against speaker finals.
        self.mic._echo_check = self._is_echo
        self.speaker._record_final = self._record_speaker_final

    @staticmethod
    def _normalize(text: str) -> str:
        """Lowercase, strip punctuation, collapse whitespace for fuzzy comparison."""
        text = (text or "").lower()
        text = re.sub(r"[^\w\s]", "", text)
        return re.sub(r"\s+", " ", text).strip()

    def _record_speaker_final(self, text: str):
        norm = self._normalize(text)
        if not norm:
            return
        now = time.time()
        self._recent_speaker.append((norm, now))
        # Prune anything outside the comparison window.
        cutoff = now - self.ECHO_WINDOW_SECONDS
        self._recent_speaker = [(t, ts) for (t, ts) in self._recent_speaker if ts >= cutoff]

    def _is_echo(self, text: str) -> bool:
        norm = self._normalize(text)
        if not norm:
            return False
        now = time.time()
        cutoff = now - self.ECHO_WINDOW_SECONDS
        for spoken, ts in self._recent_speaker:
            if ts < cutoff:
                continue
            # Containment handles the common case where one side has the fuller
            # utterance (e.g. mic caught a fragment of what the speaker said).
            if norm in spoken or spoken in norm:
                return True
            if difflib.SequenceMatcher(None, norm, spoken).ratio() >= self.ECHO_SIMILARITY:
                return True
        return False

    @property
    def is_connected(self) -> bool:
        return self.mic.is_connected or self.speaker.is_connected

    async def connect(
        self,
        on_transcript: Callable,
        on_error: Callable,
        on_metadata: Optional[Callable] = None,
    ) -> bool:
        """Connect BOTH underlying streams. Both share the same callbacks; each
        stamps its own fixed source so the client receives correctly-tagged data."""
        # Open both Deepgram streams concurrently so total connect time is the slower of
        # the two handshakes, not their sum (each is a separate network roundtrip).
        results = await asyncio.gather(
            self.mic.connect(on_transcript, on_error, on_metadata),
            self.speaker.connect(on_transcript, on_error, on_metadata),
            return_exceptions=True,
        )
        mic_ok = results[0] is True
        spk_ok = results[1] is True
        if isinstance(results[0], Exception):
            logger.error(f"Mic stream connect error: {results[0]}")
        if isinstance(results[1], Exception):
            logger.warning(f"Speaker stream connect error: {results[1]}")
        # Microphone is the essential stream; speaker is best-effort (loopback may be
        # unavailable on some machines). Succeed as long as the mic connected.
        if not mic_ok:
            # Tear down whatever did connect to avoid leaks.
            await self.disconnect()
            return False
        if not spk_ok:
            logger.warning("Speaker stream failed to connect; continuing with microphone only")
        return True

    async def send_audio(self, audio_data: bytes, source: str = "microphone") -> bool:
        """Route an audio chunk to the connection that owns its source."""
        if source == "speaker":
            return await self.speaker.send_audio(audio_data)
        return await self.mic.send_audio(audio_data)

    def enable_ai_pipeline(
        self,
        keyword_detector=None,
        context_manager=None,
        on_ai_suggestion=None,
    ):
        """Enable the AI pipeline on both streams, sharing one context manager and a
        single lock so answers don't interleave between mic and speaker."""
        self.mic.enable_ai_pipeline(
            keyword_detector=keyword_detector,
            context_manager=context_manager,
            on_ai_suggestion=on_ai_suggestion,
            ai_lock=self._ai_lock,
        )
        self.speaker.enable_ai_pipeline(
            keyword_detector=keyword_detector,
            context_manager=context_manager,
            on_ai_suggestion=on_ai_suggestion,
            ai_lock=self._ai_lock,
        )

    async def cancel_ai(self) -> bool:
        """Cancel any in-flight AI suggestion on either stream (user hit Stop)."""
        results = await asyncio.gather(
            self.mic.cancel_ai(),
            self.speaker.cancel_ai(),
            return_exceptions=True,
        )
        return any(r is True for r in results)

    def get_all_messages(self) -> List[Dict]:
        """Merge both streams' messages, ordered by creation time."""
        combined = list(self.mic.messages) + list(self.speaker.messages)
        combined.sort(key=lambda m: m.created_at)
        return [m.to_dict() for m in combined]

    def get_current_message(self) -> Optional[Dict]:
        if self.mic.current_message:
            return self.mic.current_message.to_dict()
        if self.speaker.current_message:
            return self.speaker.current_message.to_dict()
        return None

    def finalize_current_message(self):
        self.mic.finalize_current_message()
        self.speaker.finalize_current_message()

    def reset_session(self):
        self.mic.reset_session()
        self.speaker.reset_session()
        self._recent_speaker.clear()

    async def disconnect(self):
        # Disconnect both; never let one failure prevent the other from cleaning up.
        try:
            await self.mic.disconnect()
        except Exception as e:
            logger.error(f"Mic disconnect error: {e}")
        try:
            await self.speaker.disconnect()
        except Exception as e:
            logger.error(f"Speaker disconnect error: {e}")


class DeepgramSessionManager:
    """
    Manages multiple Deepgram Live transcription sessions
    """
    
    def __init__(self):
        self._sessions: Dict[str, DualDeepgramSession] = {}
        logger.info("Initialized DeepgramSessionManager")
    
    async def create_session(
        self,
        session_id: str,
        on_transcript: Callable,
        on_error: Callable,
        api_key: Optional[str] = None,
        model: str = settings.DEEPGRAM_MODEL,
        language: str = settings.DEEPGRAM_LANGUAGE,
        endpointing_ms: int = 300,
        vad_events: bool = True,
        username: Optional[str] = None,
    ) -> Optional[DualDeepgramSession]:
        """
        Create a new dual-stream Deepgram session (separate mic + speaker connections).

        Args:
            session_id: Unique session identifier
            on_transcript: Callback for transcript updates
            on_error: Callback for errors
            api_key: Deepgram API key (uses default if None)
            model: Deepgram model
            language: Language code
            endpointing_ms: Endpointing threshold in milliseconds
            vad_events: Enable VAD events
            username: Owning user, threaded down for per-user RAG/experience scoping

        Returns:
            Session instance or None if creation failed
        """
        try:
            service = DualDeepgramSession(
                api_key=api_key,
                model=model,
                language=language,
                endpointing_ms=endpointing_ms,
                vad_events=vad_events,
                username=username,
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
    
    def get_session(self, session_id: str) -> Optional[DualDeepgramSession]:
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
