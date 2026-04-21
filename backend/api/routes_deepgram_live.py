"""
Alternate WebSocket entry for live transcription.

**Clients should prefer `/api/transcribe/audio`** (same protocol). This route exists for
backward compatibility and uses the same Deepgram session, AI pipeline, and message shape
as `routes_realtime.audio_transcribe`.
"""
import asyncio
import base64
import json
import logging

from fastapi import APIRouter, Query, WebSocket, WebSocketDisconnect

from config import settings
from security import security_manager
from services.context_service import context_session_manager
from services.deepgram_live_service import deepgram_session_manager

logger = logging.getLogger(__name__)

router = APIRouter(
    prefix="/api/transcribe",
    tags=["Deepgram Live Transcription"],
)


@router.websocket("/live")
async def live_transcribe(
    websocket: WebSocket,
    token: str = Query(..., description="JWT authentication token"),
    model: str = Query("nova-3"),
    language: str = Query(settings.DEEPGRAM_LANGUAGE),
    endpointing_ms: int = Query(500),
):
    session_id = None
    deepgram_service = None
    context_mgr = None

    try:
        try:
            user_data = security_manager.verify_token(token)
            logger.info("WebSocket /live authenticated: %s", user_data.get("sub"))
        except Exception as e:
            logger.error("Auth failed: %s", e)
            await websocket.close(code=1008, reason="Unauthorized")
            return

        await websocket.accept()
        session_id = f"{user_data.get('sub')}_{asyncio.current_task().get_name()}"

        async def on_transcript(result):
            try:
                await websocket.send_json(result)
            except Exception as e:
                logger.error("Send error: %s", e)

        async def on_error(error_msg):
            try:
                await websocket.send_json({"type": "error", "error": error_msg})
            except Exception as e:
                logger.error("Error send failed: %s", e)

        deepgram_service = await deepgram_session_manager.create_session(
            session_id=session_id,
            on_transcript=on_transcript,
            on_error=on_error,
            api_key=None,
            model=model,
            language=language,
            endpointing_ms=endpointing_ms,
            vad_events=True,
        )

        if not deepgram_service:
            await websocket.send_json({"type": "error", "error": "Session creation failed"})
            return

        context_mgr = context_session_manager.get_or_create(session_id)
        try:
            from services.keyword_service import keyword_detector

            async def on_ai_suggestion(msg):
                try:
                    await websocket.send_json(msg)
                except Exception as e:
                    logger.error("AI suggestion send error: %s", e)

            deepgram_service.enable_ai_pipeline(
                keyword_detector=keyword_detector,
                context_manager=context_mgr,
                on_ai_suggestion=on_ai_suggestion,
            )
        except Exception as e:
            logger.warning("AI pipeline setup skipped: %s", e)

        await websocket.send_json(
            {
                "type": "session.created",
                "session_id": session_id,
                "model": model,
                "language": language,
                "endpointing_ms": endpointing_ms,
                "status": "ready",
                "api_type": "Deepgram Live Streaming (/live)",
            }
        )

        while True:
            message_data = await websocket.receive()

            if "text" in message_data:
                try:
                    message = json.loads(message_data["text"])
                except json.JSONDecodeError as e:
                    logger.warning("Invalid JSON: %s", e)
                    await on_error(f"Invalid JSON: {e}")
                    continue

                if message.get("type") == "audio":
                    audio_base64 = message.get("data", "")
                    audio_data = base64.b64decode(audio_base64)
                    source = message.get("source", "microphone")
                    await deepgram_service.send_audio(audio_data, source)

                elif message.get("action") == "get_messages":
                    messages = deepgram_service.get_all_messages()
                    await websocket.send_json(
                        {
                            "type": "session.messages",
                            "messages": messages,
                            "total": len(messages),
                        }
                    )

                elif message.get("action") == "reset":
                    deepgram_service.reset_session()
                    await websocket.send_json({"type": "session.reset", "status": "ok"})

                elif message.get("action") == "finalize":
                    deepgram_service.finalize_current_message()
                    current_msg = deepgram_service.get_current_message()
                    await websocket.send_json(
                        {"type": "message.finalized", "message": current_msg}
                    )

                elif message.get("action") == "set_screen_context":
                    raw = message.get("text") or message.get("ocr") or ""
                    text = (raw or "")[:12000]
                    if context_mgr:
                        context_mgr.set_screen_context(text)
                    await websocket.send_json(
                        {"type": "screen_context.ack", "length": len(text), "status": "ok"}
                    )

            elif "bytes" in message_data:
                await deepgram_service.send_audio(message_data["bytes"], "microphone")

    except WebSocketDisconnect:
        logger.info("Disconnected: %s", session_id)
    except Exception as e:
        logger.exception("WebSocket /live error: %s", e)
    finally:
        if session_id and deepgram_service:
            await deepgram_session_manager.remove_session(session_id)
        if session_id:
            context_session_manager.remove(session_id)
        if session_id:
            logger.info("Cleanup /live: %s", session_id)
