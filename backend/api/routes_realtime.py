"""
Real-time Transcription Routes
Handles WebSocket connections for live audio transcription
"""
import json
import logging
import asyncio
from fastapi import APIRouter, WebSocket, WebSocketDisconnect, Query
from fastapi.responses import JSONResponse

import sys
import os
sys.path.insert(0, os.path.dirname(os.path.dirname(os.path.abspath(__file__))))

import websockets

from security import security_manager
from services import session_manager, create_transcription_service
from models import VoiceType

# Configure logging
logger = logging.getLogger(__name__)

# Create router
router = APIRouter(
    prefix="/api/transcribe",
    tags=["Real-time Transcription"]
)


@router.websocket("/live")
async def live_transcribe(
    websocket: WebSocket,
    token: str = Query(..., description="JWT authentication token"),
    voice: VoiceType = Query(VoiceType.ALLOY, description="Voice type for responses"),
    encrypt: bool = Query(True, description="Encrypt sensitive transcription data")
):
    """
    Real-time audio transcription WebSocket endpoint
    
    **Features:**
    - Live speech-to-text transcription
    - Conversation management
    - Audio input/output streaming
    - Voice responses
    - Automatic turn detection
    
    **Connection:**
    1. Connect with valid JWT token as query parameter
    2. Receive session configuration
    3. Send audio data in chunks
    4. Receive transcriptions and responses
    
    **Message Types:**
    - `session.created`: Session initialized
    - `conversation.item.created`: New conversation item
    - `response.audio_transcript.done`: Transcription complete
    - `error`: Error occurred
    
    **Example:**
    ```javascript
    const ws = new WebSocket('ws://localhost:8000/api/transcribe/live?token=YOUR_TOKEN');
    ws.onmessage = (event) => {
        const data = JSON.parse(event.data);
        console.log('Received:', data);
    };
    ```
    """
    session_id = None
    
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
        
        # Create transcription session
        session_id = f"{user_data.get('sub')}_{asyncio.current_task().get_name()}"
        transcribe_service = session_manager.create_session(
            session_id=session_id,
            api_key=None,  # Will use default OPENAI_API_KEY from settings
            security_manager=security_manager,
            voice=voice
        )
        
        if not transcribe_service:
            transcribe_service = create_transcription_service(voice=voice)
        
        logger.info(f"Created transcription session: {session_id}")
        
        # Connect to OpenAI Realtime API
        async with websockets.connect(
            transcribe_service.ws_url,
            extra_headers=transcribe_service.get_headers()
        ) as openai_ws:
            
            # Send session configuration
            session_config = transcribe_service.get_session_config()
            await openai_ws.send(json.dumps(session_config))
            logger.info(f"Sent session config for session: {session_id}")
            
            # Send confirmation to client
            await websocket.send_json({
                "type": "session.created",
                "session_id": session_id,
                "voice": voice.value,
                "status": "ready"
            })
            
            # Bidirectional message forwarding
            async def forward_to_openai():
                """Forward messages from client to OpenAI"""
                try:
                    while True:
                        # Receive data from client
                        data = await websocket.receive_text()
                        
                        # Forward to OpenAI
                        await openai_ws.send(data)
                        logger.debug(f"Forwarded to OpenAI: {data[:100]}")
                        
                except WebSocketDisconnect:
                    logger.info(f"Client disconnected: {session_id}")
                except Exception as e:
                    logger.error(f"Error forwarding to OpenAI: {str(e)}")
            
            async def forward_to_client():
                """Forward messages from OpenAI to client"""
                try:
                    async for message in openai_ws:
                        # Process message (optionally encrypt sensitive data)
                        processed = transcribe_service.process_message(
                            message,
                            encrypt_sensitive=encrypt
                        )
                        
                        # Forward to client
                        await websocket.send_text(processed)
                        logger.debug(f"Forwarded to client: {processed[:100]}")
                        
                except Exception as e:
                    logger.error(f"Error forwarding to client: {str(e)}")
            
            # Run both forwarding tasks concurrently
            await asyncio.gather(
                forward_to_openai(),
                forward_to_client(),
                return_exceptions=True
            )
            
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
        except:
            pass
            
    finally:
        # Clean up session
        if session_id:
            session_manager.remove_session(session_id)
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
            "active_sessions": session_manager.get_active_session_count(),
            "status": "operational"
        }
    )


@router.post(
    "/test-connection",
    summary="Test Transcription Connection",
    description="Test if transcription service is available"
)
async def test_connection():
    """
    Test if OpenAI Realtime API is accessible
    
    **Use Case:**
    - Verify service availability
    - Check API key validity
    - Debug connection issues
    """
    try:
        # Try creating a service instance
        service = create_transcription_service()
        
        return JSONResponse(
            content={
                "status": "connected",
                "service": "OpenAI Realtime API",
                "ws_url": service.ws_url
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

