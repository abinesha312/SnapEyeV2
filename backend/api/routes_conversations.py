"""
Conversation Persistence API Routes

A user's activity - typed questions, voice transcripts, screen-capture OCR, quick
actions, and assistant answers - all appends to a single ongoing "conversation"
that persists across app restarts, instead of being lost when the WebSocket
session ends or reset every time the app is relaunched. "New Conversation"
explicitly closes the current thread and starts a fresh one (ChatGPT-style).

The active conversation is always resolved from the caller's authenticated
identity (the JWT `sub` claim) - the client never has to pass a conversation_id
on writes, only to fetch history for resuming the view after a restart.
"""
import logging

from fastapi import APIRouter, Depends
from fastapi.responses import JSONResponse

from models import AppendMessageRequest
from security import auth_handler
from services.storage_service import storage_service

logger = logging.getLogger(__name__)

router = APIRouter(
    prefix="/api/conversations",
    tags=["Conversations"],
)


@router.get(
    "/active",
    summary="Get (or create) the caller's active conversation",
)
async def get_active_conversation(user_data=Depends(auth_handler.verify_auth)):
    try:
        username = user_data.get("sub")
        conversation_id = await storage_service.get_or_create_active_conversation(username)
        messages = await storage_service.get_conversation_messages(conversation_id)
        return JSONResponse(content={"conversation_id": conversation_id, "messages": messages})
    except Exception as e:
        logger.exception(f"Get active conversation error: {e}")
        return JSONResponse(status_code=500, content={"error": str(e)})


@router.post(
    "/new",
    summary="Close the current conversation and start a fresh one",
)
async def new_conversation(user_data=Depends(auth_handler.verify_auth)):
    try:
        username = user_data.get("sub")
        conversation_id = await storage_service.start_new_conversation(username)
        return JSONResponse(content={"conversation_id": conversation_id})
    except Exception as e:
        logger.exception(f"New conversation error: {e}")
        return JSONResponse(status_code=500, content={"error": str(e)})


@router.get(
    "",
    summary="List the caller's conversations",
)
async def list_conversations(user_data=Depends(auth_handler.verify_auth)):
    try:
        username = user_data.get("sub")
        conversations = await storage_service.list_conversations(username)
        return JSONResponse(content={"conversations": conversations})
    except Exception as e:
        logger.exception(f"List conversations error: {e}")
        return JSONResponse(status_code=500, content={"error": str(e)})


@router.get(
    "/{conversation_id}/messages",
    summary="Get all messages in a conversation",
)
async def get_conversation_messages(conversation_id: str, user_data=Depends(auth_handler.verify_auth)):
    try:
        messages = await storage_service.get_conversation_messages(conversation_id)
        return JSONResponse(content={"messages": messages})
    except Exception as e:
        logger.exception(f"Get conversation messages error: {e}")
        return JSONResponse(status_code=500, content={"error": str(e)})


@router.post(
    "/messages",
    summary="Append a message to the caller's active conversation",
)
async def append_message(request: AppendMessageRequest, user_data=Depends(auth_handler.verify_auth)):
    try:
        username = user_data.get("sub")
        conversation_id = await storage_service.get_or_create_active_conversation(username)
        await storage_service.save_conversation_message(
            conversation_id,
            request.kind,
            request.text,
            label=request.label,
            confidence=request.confidence,
            client_message_id=request.client_message_id,
        )
        # Index into the user's vector memory (best-effort, non-blocking).
        try:
            import asyncio
            from services.rag_service import rag_service
            asyncio.create_task(rag_service.index_user_memory(
                username=username or "",
                conversation_id=conversation_id,
                message_id=request.client_message_id or "",
                kind=request.kind,
                text=request.text,
            ))
        except Exception:
            pass
        return JSONResponse(content={"ok": True, "conversation_id": conversation_id})
    except Exception as e:
        logger.exception(f"Append message error: {e}")
        return JSONResponse(status_code=500, content={"error": str(e)})
