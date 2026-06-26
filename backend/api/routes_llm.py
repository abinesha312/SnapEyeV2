"""
LLM Streaming API Routes
Server-Sent Events (SSE) endpoint for streaming LLM responses.
"""
import json
import logging
from typing import Optional, List

from fastapi import APIRouter, Depends
from fastapi.responses import StreamingResponse, JSONResponse
from pydantic import BaseModel, Field

from security import auth_handler
from services.llm_router import (
    llm_router,
    LLMMessage,
    AllProvidersFailedError,
    NoProviderConfiguredError,
)

logger = logging.getLogger(__name__)

router = APIRouter(
    prefix="/api/llm",
    tags=["LLM"],
)


class LLMRequest(BaseModel):
    """Request body for LLM generation."""
    messages: List[dict] = Field(
        ...,
        description="List of messages with 'role' and 'content' fields",
    )
    system_prompt: Optional[str] = Field(
        default=None,
        description="System prompt to guide the LLM",
    )
    max_tokens: int = Field(default=4096, ge=1, le=16384)
    temperature: float = Field(default=0.7, ge=0.0, le=2.0)
    provider: Optional[str] = Field(
        default=None,
        description="Force a specific provider (openai, anthropic, gemini, grok)",
    )
    model: Optional[str] = Field(
        default=None,
        description="Override the provider's default model",
    )
    api_key: Optional[str] = Field(
        default=None,
        description="Per-request API key (supplied by the desktop client's AI Models picker). "
                    "When present, failover is disabled and this key is used directly.",
    )
    stream: bool = Field(default=True, description="Enable streaming response")


class ContextLLMRequest(BaseModel):
    """Request body for context-aware LLM generation (used by auto-trigger pipeline)."""
    question: str = Field(..., description="Detected question from transcript")
    transcript_context: str = Field(default="", description="Rolling transcript window")
    rag_context: str = Field(default="", description="Retrieved RAG chunks")
    screen_context: str = Field(default="", description="OCR text from screen capture")
    system_prompt: Optional[str] = Field(default=None)
    provider: Optional[str] = Field(default=None)
    model: Optional[str] = Field(default=None)
    api_key: Optional[str] = Field(default=None)


class TestConnectionRequest(BaseModel):
    """Request body for /api/llm/test — pings a provider with the user's key."""
    provider: str = Field(..., description="Provider id: openai, anthropic, gemini, grok")
    model: str = Field(..., description="Model to test (must be valid for the provider)")
    api_key: str = Field(..., description="API key to validate")


@router.post(
    "/stream",
    summary="Stream LLM response",
    description="Send messages and receive a streaming response via Server-Sent Events (SSE).",
)
async def stream_llm(
    request: LLMRequest,
    user_data=Depends(auth_handler.verify_auth),
):
    """Stream LLM response token-by-token via SSE."""
    messages = [LLMMessage(role=m["role"], content=m["content"]) for m in request.messages]

    if request.stream:
        return StreamingResponse(
            _stream_tokens(
                messages,
                request.system_prompt,
                request.max_tokens,
                request.temperature,
                request.provider,
                request.api_key,
                request.model,
                user_sub=user_data.get("sub"),
            ),
            media_type="text/event-stream",
            headers={
                "Cache-Control": "no-cache",
                "Connection": "keep-alive",
                "X-Accel-Buffering": "no",
            },
        )
    else:
        try:
            result = await llm_router.generate(
                messages,
                max_tokens=request.max_tokens,
                temperature=request.temperature,
                system_prompt=request.system_prompt,
                provider=request.provider,
                api_key=request.api_key,
                model=request.model,
            )
            return JSONResponse({"response": result, "stream": False})
        except NoProviderConfiguredError as e:
            return JSONResponse(
                status_code=503,
                content={"code": "no_ai_configured", "error": str(e)},
            )
        except AllProvidersFailedError as e:
            return JSONResponse(status_code=503, content={"error": str(e)})


@router.post(
    "/context-stream",
    summary="Stream context-aware LLM response",
    description="Given a detected question, transcript context, and RAG context, "
                "stream an AI suggestion via SSE.",
)
async def context_stream_llm(
    request: ContextLLMRequest,
    user_data=Depends(auth_handler.verify_auth),
):
    """Stream a context-aware LLM response for the auto-trigger pipeline."""
    system_prompt = request.system_prompt or _build_assistant_system_prompt()

    # Build the user message from all contexts
    user_content = _build_context_message(
        question=request.question,
        transcript=request.transcript_context,
        rag=request.rag_context,
        screen=request.screen_context,
    )
    messages = [LLMMessage(role="user", content=user_content)]

    return StreamingResponse(
        _stream_tokens(
            messages,
            system_prompt,
            provider=request.provider,
            api_key=request.api_key,
            model=request.model,
            user_sub=user_data.get("sub"),
        ),
        media_type="text/event-stream",
        headers={
            "Cache-Control": "no-cache",
            "Connection": "keep-alive",
            "X-Accel-Buffering": "no",
        },
    )


@router.post(
    "/test",
    summary="Test an AI provider connection",
    description="Send a tiny ping prompt to verify the provider/model/api_key combination works. "
                "Returns {ok, latency_ms, error?}.",
)
async def test_connection(
    request: TestConnectionRequest,
    user_data=Depends(auth_handler.verify_auth),
):
    """Verify credentials by sending a minimal prompt to the chosen provider."""
    try:
        result = await llm_router.test_connection(
            provider=request.provider,
            model=request.model,
            api_key=request.api_key,
        )
        status = 200 if result.get("ok") else 200  # always 200; body carries ok flag
        return JSONResponse(status_code=status, content=result)
    except Exception as exc:
        logger.exception(f"/api/llm/test failed: {exc}")
        return JSONResponse(
            status_code=200,
            content={"ok": False, "error": str(exc), "latency_ms": 0},
        )


@router.get(
    "/providers",
    summary="List available LLM providers",
)
async def list_providers(user_data=Depends(auth_handler.verify_auth)):
    """Return which LLM providers are configured and available."""
    return JSONResponse({
        "providers": llm_router.available_providers,
        "primary": llm_router.available_providers[0] if llm_router.available_providers else None,
    })


async def _stream_tokens(
    messages: List[LLMMessage],
    system_prompt: Optional[str] = None,
    max_tokens: int = 4096,
    temperature: float = 0.7,
    provider: Optional[str] = None,
    api_key: Optional[str] = None,
    model: Optional[str] = None,
    user_sub: Optional[str] = None,
):
    """Yield SSE-formatted token events."""
    try:
        logger.info(
            "LLM SSE stream start sub=%s provider=%s model=%s",
            user_sub or "unknown",
            provider or "default",
            model or "default",
        )
        yield f"data: {json.dumps({'type': 'start'})}\n\n"

        full_response = ""
        async for token in llm_router.generate_stream(
            messages,
            max_tokens=max_tokens,
            temperature=temperature,
            system_prompt=system_prompt,
            provider=provider,
            api_key=api_key,
            model=model,
        ):
            full_response += token
            yield f"data: {json.dumps({'type': 'token', 'token': token})}\n\n"

        yield f"data: {json.dumps({'type': 'end', 'full_response': full_response})}\n\n"

    except NoProviderConfiguredError as e:
        payload = {"type": "error", "code": "no_ai_configured", "error": str(e)}
        yield f"data: {json.dumps(payload)}\n\n"
    except AllProvidersFailedError as e:
        yield f"data: {json.dumps({'type': 'error', 'error': str(e)})}\n\n"
    except Exception as e:
        logger.exception(f"Streaming error: {e}")
        yield f"data: {json.dumps({'type': 'error', 'error': str(e)})}\n\n"


def _build_assistant_system_prompt() -> str:
    """Build the default system prompt for the AI meeting assistant."""
    return (
        "You are SnapEye, an intelligent real-time meeting assistant. "
        "You help users during live conversations by providing relevant answers, "
        "suggestions, and insights based on the ongoing transcript.\n\n"
        "CRITICAL RULES:\n"
        "- You HAVE access to the conversation transcript. It is provided in the user message.\n"
        "- NEVER say you don't have access to previous conversations. You DO.\n"
        "- NEVER say you need more context if a transcript is provided. USE the transcript.\n"
        "- Be concise and actionable. The user is in a live meeting.\n"
        "- Answer directly first, then elaborate briefly if needed.\n"
        "- Use bullet points and markdown for readability.\n"
        "- Keep responses under 200 words.\n"
        "- Do NOT use large headings (## or #). Use **bold** for emphasis instead."
    )


def _build_context_message(
    question: str,
    transcript: str = "",
    rag: str = "",
    screen: str = "",
) -> str:
    """Build the user message combining all available context."""
    parts = [f"**Detected Question:** {question}"]

    if transcript:
        parts.append(
            f"\n**Recent Conversation Transcript:**\n```\n{transcript[-3000:]}\n```"
        )

    if rag:
        parts.append(
            f"\n**Relevant Knowledge Base Context:**\n{rag}"
        )

    if screen:
        parts.append(
            f"\n**On-Screen Text (OCR):**\n```\n{screen[:2000]}\n```"
        )

    parts.append(
        "\nPlease provide a helpful, concise answer to the detected question "
        "using the context above."
    )

    return "\n".join(parts)
