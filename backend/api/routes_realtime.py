"""
Audio-to-Text Transcription Routes using Deepgram Live Streaming API
Real-time WebSocket streaming with endpointing and interim results
"""
import json
import logging
import base64
import uuid
import asyncio
from fastapi import APIRouter, WebSocket, WebSocketDisconnect, Query
from fastapi.responses import JSONResponse

import sys
import os
sys.path.insert(0, os.path.dirname(os.path.dirname(os.path.abspath(__file__))))

from security import security_manager
from services.deepgram_live_service import deepgram_session_manager
from services.context_service import context_session_manager
from config import settings

# Configure logging
logger = logging.getLogger(__name__)

# Create router
router = APIRouter(
    prefix="/api/transcribe",
    tags=["Audio Transcription"]
)


@router.websocket("/audio")
async def audio_transcribe(
    websocket: WebSocket,
    token: str = Query(..., description="JWT authentication token"),
    model: str = Query("nova-3", description="Deepgram model (nova-3, nova-2, nova, base, enhanced)"),
    language: str = Query(settings.DEEPGRAM_LANGUAGE, description="Language code (en, es, fr, etc.)"),
    endpointing_ms: int = Query(300, description="Endpointing threshold in milliseconds (default: 300ms)")
):
    """
    Real-time Audio-to-Text transcription WebSocket endpoint using Deepgram Live Streaming API
    
    **How it works:**
    - Uses Deepgram Live WebSocket API (wss://api.deepgram.com/v1/listen)
    - Client sends small audio chunks (200-300ms recommended)
    - Server streams audio to Deepgram in real-time
    - Receives interim results for fast UI updates
    - Uses endpointing for automatic message segmentation
    - All messages remain in the same chat session
    
    **Features:**
    - Real-time streaming (not batch)
    - Interim results for low-latency UI updates
    - Endpointing-based message segmentation
    - Automatic reconnection on failures
    - Multi-message chat session
    - Speaker diarization (optional)
    - Smart formatting and punctuation
    
    **Connection Flow:**
    1. Connect with valid JWT token
    2. Receive session.created confirmation
    3. Send audio chunks (PCM16, 24kHz, mono, 200-300ms chunks)
    4. Receive transcript.updated (interim) and transcript.final (final)
    5. Receive message.finalized when endpointing detected
    
    **Message Types:**
    - `session.created`: Session initialized
    - `transcript.updated`: Interim transcript (for fast UI updates)
    - `transcript.final`: Final transcript segment
    - `message.finalized`: Current message finalized (endpointing detected)
    - `session.messages`: All messages in session
    - `error`: Error occurred
    
    **Audio Format:**
    - Encoding: PCM16 (linear16)
    - Sample Rate: 24kHz (24000 Hz)
    - Channels: Mono (1 channel)
    - Chunk Size: 200-300ms recommended (9.6KB-14.4KB for 24kHz PCM16)
    
    **Example (JavaScript):**
    ```javascript
    const ws = new WebSocket('ws://localhost:8080/api/transcribe/audio?token=YOUR_TOKEN&endpointing_ms=500');
    
    ws.onopen = () => {
        console.log('Connected');
    };
    
    ws.onmessage = (event) => {
        const data = JSON.parse(event.data);
        
        if (data.type === 'transcript.updated') {
            console.log('Interim:', data.transcript);
        } else if (data.type === 'transcript.final') {
            console.log('Final:', data.transcript);
        } else if (data.type === 'message.finalized') {
            console.log('Message finalized:', data.message.transcript);
        }
    };
    
    // Send small audio chunks (200-300ms)
    function sendAudioChunk(audioBuffer) {
        ws.send(audioBuffer);
    }
    
    // Send audio with source info
    ws.send(JSON.stringify({
        type: 'audio',
        data: base64AudioData,
        source: 'microphone' // or 'speaker'
    }));
    
    // Get all messages
    ws.send(JSON.stringify({ action: 'get_messages' }));
    
    // Reset session
    ws.send(JSON.stringify({ action: 'reset' }));
    ```
    """
    session_id = None
    deepgram_service = None
    
    try:
        # Verify authentication token
        try:
            user_data = security_manager.verify_token(token)
            logger.info(f"WebSocket connection authenticated: {user_data.get('sub')}")
        except Exception as e:
            logger.error(f"WebSocket authentication failed: {str(e)}")
            await websocket.close(code=1008, reason="Unauthorized")
            return
        
        # Accept WebSocket connection
        await websocket.accept()
        session_id = f"{user_data.get('sub')}_{uuid.uuid4().hex}"

        # The session now runs TWO Deepgram streams (mic + speaker), each with its own
        # receive loop. Starlette's WebSocket.send is not safe for concurrent callers,
        # so serialize every outbound frame through this lock.
        send_lock = asyncio.Lock()

        async def safe_send(payload: dict):
            async with send_lock:
                await websocket.send_json(payload)

        # Callbacks for transcript updates
        async def on_transcript(result):
            try:
                # Forward transcript updates to client
                await safe_send(result)
                logger.debug(f"Sent transcript: {result.get('type')} - {result.get('transcript', '')[:50]}...")
            except Exception as e:
                logger.error(f"Error sending transcript: {e}")
        
        async def on_error(error_msg):
            try:
                await safe_send({
                    "type": "error",
                    "error": error_msg
                })
            except Exception as e:
                logger.error(f"Error send failed: {e}")
        
        # Create Deepgram Live session
        deepgram_service = await deepgram_session_manager.create_session(
            session_id=session_id,
            on_transcript=on_transcript,
            on_error=on_error,
            api_key=None,  # Uses default from settings
            model=model,
            language=language,
            endpointing_ms=endpointing_ms,
            vad_events=True,
            username=user_data.get("sub"),
        )
        
        if not deepgram_service:
            await safe_send({
                "type": "error",
                "error": "Failed to create Deepgram session"
            })
            return
        
        logger.info(f"Created Deepgram Live session: {session_id}")
        
        # Enable AI pipeline (keyword detection -> context -> LLM -> streaming response)
        context_mgr = context_session_manager.get_or_create(session_id)
        try:
            from services.keyword_service import keyword_detector
            
            async def on_ai_suggestion(msg):
                try:
                    await safe_send(msg)
                except Exception as e:
                    logger.error(f"AI suggestion send error: {e}")
            
            deepgram_service.enable_ai_pipeline(
                keyword_detector=keyword_detector,
                context_manager=context_mgr,
                on_ai_suggestion=on_ai_suggestion,
            )
            logger.info(f"AI pipeline enabled for session: {session_id}")
        except Exception as e:
            logger.warning(f"AI pipeline setup skipped: {e}")
        
        # Send session created confirmation
        await safe_send({
            "type": "session.created",
            "session_id": session_id,
            "model": model,
            "language": language,
            "endpointing_ms": endpointing_ms,
            "status": "ready",
            "api_type": "Deepgram Live Streaming",
            "instructions": {
                "audio_format": "PCM16, 24kHz, Mono",
                "chunk_size": "200-300ms recommended (9.6KB-14.4KB)",
                "endpointing": f"{endpointing_ms}ms silence triggers new message",
                "interim_results": "Enabled for fast UI updates",
                "actions": [
                    "get_messages",
                    "reset",
                    "finalize",
                    "set_screen_context",
                ]
            }
        })
        
        # Main message loop
        try:
            while True:
                message_data = await websocket.receive()
                
                # Handle binary audio data (defaults to microphone)
                if "bytes" in message_data:
                    audio_data = message_data["bytes"]
                    logger.debug(f"Received {len(audio_data)} bytes of audio")
                    await deepgram_service.send_audio(audio_data, source="microphone")
                
                # Handle text commands
                elif "text" in message_data:
                    try:
                        command = json.loads(message_data["text"])
                        
                        # Handle audio data with source info
                        if command.get("type") == "audio":
                            audio_base64 = command.get("data", "")
                            source = command.get("source", "microphone")
                            
                            if audio_base64:
                                audio_data = base64.b64decode(audio_base64)
                                logger.debug(f"Received {len(audio_data)} bytes from {source}")
                                await deepgram_service.send_audio(audio_data, source=source)
                            continue
                        
                        # Handle actions
                        action = command.get("action")
                        
                        if action == "get_messages":
                            messages = deepgram_service.get_all_messages()
                            await safe_send({
                                "type": "session.messages",
                                "messages": messages,
                                "total": len(messages)
                            })
                            logger.info(f"Sent {len(messages)} messages to client")
                        
                        elif action == "reset":
                            deepgram_service.reset_session()
                            await safe_send({
                                "type": "session.reset",
                                "status": "Session reset successfully"
                            })
                            logger.info(f"Session reset: {session_id}")
                        
                        elif action == "finalize":
                            deepgram_service.finalize_current_message()
                            current_msg = deepgram_service.get_current_message()
                            await safe_send({
                                "type": "message.finalized",
                                "message": current_msg
                            })
                            logger.info(f"Message manually finalized: {session_id}")

                        elif action == "set_screen_context":
                            raw = command.get("text") or command.get("ocr") or ""
                            text = (raw or "")[:12000]
                            context_mgr.set_screen_context(text)
                            await safe_send({
                                "type": "screen_context.ack",
                                "length": len(text),
                                "status": "ok",
                            })
                            logger.debug(
                                "Screen context updated for %s (%d chars)",
                                session_id,
                                len(text),
                            )

                        elif action == "set_interview_context":
                            context_mgr.set_interview_context(
                                job_description=(command.get("job_description") or "")[:8000],
                                company=command.get("company") or "",
                                role=command.get("role") or "",
                                mode=command.get("mode") or "",
                                mode_system_prompt=(command.get("mode_system_prompt") or "")[:4000],
                            )
                            await safe_send({
                                "type": "interview_context.ack",
                                "mode": command.get("mode") or "",
                                "has_jd": bool(command.get("job_description")),
                                "status": "ok",
                            })
                            logger.info(
                                "Interview context updated for %s (mode=%s, jd=%s chars)",
                                session_id,
                                command.get("mode") or "",
                                len(command.get("job_description") or ""),
                            )

                        elif action == "cancel_ai":
                            # User hit Stop: abort any in-flight auto AI suggestion so the
                            # backend stops generating and frees the shared AI lock.
                            cancelled = await deepgram_service.cancel_ai()
                            await safe_send({
                                "type": "ai.suggestion.cancelled",
                                "cancelled": bool(cancelled),
                                "status": "ok",
                            })
                            logger.info(f"AI suggestion cancel requested for {session_id} (cancelled={cancelled})")

                        else:
                            await safe_send({
                                "type": "error",
                                "error": f"Unknown action: {action}",
                                "valid_actions": [
                                    "get_messages",
                                    "reset",
                                    "finalize",
                                    "set_screen_context",
                                    "set_interview_context",
                                    "cancel_ai",
                                ],
                            })
                    
                    except json.JSONDecodeError:
                        await safe_send({
                            "type": "error",
                            "error": "Invalid JSON command"
                        })
                    except Exception as e:
                        logger.error(f"Error processing command: {str(e)}")
                        await safe_send({
                            "type": "error",
                            "error": str(e)
                        })
                        
        except WebSocketDisconnect:
            logger.info(f"Client disconnected: {session_id}")
        except Exception as e:
            logger.error(f"Error in message loop: {str(e)}")
            
    except WebSocketDisconnect:
        logger.info(f"WebSocket disconnected: {session_id}")
        
    except Exception as e:
        logger.exception(f"WebSocket error: {str(e)}")
        try:
            await websocket.send_json({
                "type": "error",
                "error": str(e),
                "session_id": session_id
            })
        except Exception:
            pass
            
    finally:
        # Clean up session
        if session_id and deepgram_service:
            try:
                messages = deepgram_service.get_all_messages()
                await safe_send({
                    "type": "session.closing",
                    "final_messages": messages,
                    "total_messages": len(messages)
                })
            except Exception:
                pass
            
            await deepgram_session_manager.remove_session(session_id)
            context_session_manager.remove(session_id)
            logger.info(f"Cleaned up session: {session_id}")


@router.get(
    "/sessions",
    summary="Active Sessions",
    description="Get count of active transcription sessions"
)
async def get_active_sessions():
    """
    Get information about active transcription sessions
    
    **Returns:**
    - Number of active sessions
    - Server status
    """
    return JSONResponse(
        content={
            "active_sessions": deepgram_session_manager.get_active_count(),
            "status": "operational",
            "service": "Deepgram Audio-to-Text",
            "api_type": "Live Streaming (Real-time)"
        }
    )


@router.post(
    "/test-connection",
    summary="Test Transcription Connection",
    description="Test if Deepgram service is available"
)
async def test_connection():
    """
    Test if Deepgram API is accessible
    
    **Use Case:**
    - Verify service availability
    - Check API key validity
    - Debug connection issues
    """
    try:
        # Test Deepgram API connection
        from deepgram import DeepgramClient
        client = DeepgramClient(api_key=settings.DEEPGRAM_API_KEY)
        
        # Test with a simple URL transcription
        response = client.listen.v1.media.transcribe_url(
            {"url": "https://dpgr.am/bueller.wav"},
            {
                "model": settings.DEEPGRAM_MODEL,
                "smart_format": True,
            }
        )
        
        transcript = "N/A"
        if response.results and response.results.channels:
            channel = response.results.channels[0]
            if channel.alternatives:
                transcript = channel.alternatives[0].transcript
        
        return JSONResponse(
            content={
                "status": "connected",
                "service": "Deepgram Audio-to-Text",
                "api_type": "Live Streaming (Real-time)",
                "model": settings.DEEPGRAM_MODEL,
                "language": settings.DEEPGRAM_LANGUAGE,
                "test_transcription": transcript
            }
        )
        
    except Exception as e:
        logger.error(f"Connection test failed: {str(e)}")
        return JSONResponse(
            content={
                "status": "failed",
                "error": str(e)
            },
            status_code=500
        )


@router.get(
    "/info",
    summary="API Information",
    description="Get information about the transcription API"
)
async def get_api_info():
    """
    Get information about the transcription API
    
    **Returns:**
    - API type and features
    - Supported audio formats
    - Configuration options
    """
    return JSONResponse(
        content={
            "api_name": "SnapEye Audio-to-Text Transcription",
            "api_type": "Deepgram Live Streaming (Real-time)",
            "version": "2.0.0",
            "features": {
                "real_time_streaming": True,
                "interim_results": True,
                "endpointing": True,
                "automatic_reconnection": True,
                "multi_message_session": True,
                "speaker_diarization": settings.DEEPGRAM_DIARIZE,
                "smart_formatting": settings.DEEPGRAM_SMART_FORMAT,
                "punctuation": settings.DEEPGRAM_PUNCTUATE,
                "voice_activity_detection": True
            },
            "audio_format": {
                "encoding": "PCM16 (linear16)",
                "sample_rate": "24000 Hz (24kHz)",
                "channels": "1 (Mono)",
                "recommended_chunk_size": "200-300ms (9.6KB-14.4KB for 24kHz PCM16)"
            },
            "configuration": {
                "default_model": settings.DEEPGRAM_MODEL,
                "default_language": settings.DEEPGRAM_LANGUAGE,
                "default_endpointing_ms": "500ms"
            },
            "endpoints": {
                "websocket": "/api/transcribe/audio",
                "sessions": "/api/transcribe/sessions",
                "test": "/api/transcribe/test-connection",
                "info": "/api/transcribe/info"
            }
        }
    )
