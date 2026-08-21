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

from config import settings
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
    """Request body for context-aware LLM generation (used by manual chat / quick actions)."""
    question: str = Field(..., description="Detected question from transcript")
    transcript_context: str = Field(default="", description="Rolling transcript window")
    rag_context: str = Field(default="", description="Retrieved RAG chunks")
    screen_context: str = Field(default="", description="OCR text from screen capture")
    system_prompt: Optional[str] = Field(default=None)
    provider: Optional[str] = Field(default=None)
    model: Optional[str] = Field(default=None)
    api_key: Optional[str] = Field(default=None)
    # Experience RAG + interview tailoring (grounding is retrieved server-side).
    use_profile: bool = Field(default=True, description="Ground the answer in the user's saved experience")
    job_description: str = Field(default="", description="Target job description (interview mode)")
    company: str = Field(default="", description="Target company (interview mode)")
    role: str = Field(default="", description="Target role (interview mode)")
    mode: str = Field(default="", description="Active assistant mode id")


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
    """Stream a context-aware LLM response grounded in the user's saved experience."""
    is_interview = bool(request.job_description) or "interview" in (request.mode or "").lower()

    # Retrieve the user's most relevant experience (and knowledge base) in real time.
    # Bounded so a slow vector store never stalls the answer.
    experience_chunks = []
    kb_chunks = []
    memory_chunks = []
    if request.use_profile:
        try:
            import asyncio
            from services.rag_service import rag_service
            username = user_data.get("sub")
            exp_task = rag_service.search_experience(request.question, top_k=3, username=username)
            kb_task = rag_service.search(request.question, top_k=2, username=username)
            mem_task = rag_service.search_user_memory(request.question, username=username or "", top_k=2)
            experience_chunks, kb_chunks, memory_chunks = await asyncio.wait_for(
                asyncio.gather(exp_task, kb_task, mem_task, return_exceptions=True),
                timeout=0.6,
            )
            experience_chunks = experience_chunks if isinstance(experience_chunks, list) else []
            kb_chunks = kb_chunks if isinstance(kb_chunks, list) else []
            memory_chunks = memory_chunks if isinstance(memory_chunks, list) else []
            # Only ground answers in experience matches that clear a minimum relevance
            # bar - a weak match invites the model to fabricate a connection rather
            # than answering from general knowledge.
            experience_chunks = [
                c for c in experience_chunks
                if getattr(c, "score", 0.0) >= settings.RAG_EXPERIENCE_MIN_SCORE
            ]
        except Exception as e:
            logger.debug(f"Profile retrieval skipped: {e}")

    from services.rag_service import confidence_from_chunks
    confidence = confidence_from_chunks(list(experience_chunks) + list(kb_chunks) + list(memory_chunks))

    # Always apply the grounding/human-answer rules, and strictly prepend the selected mode's
    # template (sent by the client as system_prompt) so the chosen mode governs every query.
    system_prompt = _build_assistant_system_prompt(is_interview, mode_prompt=request.system_prompt)

    # Build the user message from all contexts
    user_content = _build_context_message(
        question=request.question,
        transcript=request.transcript_context,
        rag=request.rag_context,
        screen=request.screen_context,
        experience_chunks=experience_chunks,
        kb_chunks=kb_chunks,
        memory_chunks=memory_chunks,
        job_description=request.job_description,
        company=request.company,
        role=request.role,
        is_interview=is_interview,
        experience_searched=request.use_profile,
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
            confidence=confidence,
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
    confidence: Optional[float] = None,
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

        end_payload = {'type': 'end', 'full_response': full_response}
        if confidence is not None:
            end_payload['confidence'] = confidence
        yield f"data: {json.dumps(end_payload)}\n\n"

    except NoProviderConfiguredError as e:
        payload = {"type": "error", "code": "no_ai_configured", "error": str(e)}
        yield f"data: {json.dumps(payload)}\n\n"
    except AllProvidersFailedError as e:
        yield f"data: {json.dumps({'type': 'error', 'error': str(e)})}\n\n"
    except Exception as e:
        logger.exception(f"Streaming error: {e}")
        yield f"data: {json.dumps({'type': 'error', 'error': str(e)})}\n\n"


def _build_assistant_system_prompt(is_interview: bool = False, mode_prompt: str = "") -> str:
    """Build the default system prompt: a human, first-person, story-driven assistant.

    When ``mode_prompt`` (the client's selected-mode template) is supplied it is strictly
    prepended so the active mode governs the answer, on top of the grounding rules below.
    """
    base = (
        "You are SnapEye, a real-time assistant that speaks AS the user (first person, \"I\"). "
        "You HAVE access to the conversation transcript and the user's real background provided "
        "in the user message. Never say you lack access.\n\n"
        "When the question is about the user's experience, answer like a real person telling "
        "their story: set the situation and the real challenge, explain what YOU did (the workflow "
        "you designed and the technical architecture, tools, and key decisions), share the human "
        "side (what was hard, what you learned), and finish with the outcome and the value it "
        "added to the business or organization.\n"
        "RULES:\n"
        "- Answer the actual question directly first; stay strictly on-topic.\n"
        "- Be concrete and practical — specific steps, tools, numbers — no filler or padding.\n"
        "- If the detected question is garbled or ambiguous, answer the most likely intent from "
        "the transcript instead of guessing wildly or asking for clarification.\n"
        "- Ground every claim in the background/context provided; never invent facts. If something "
        "isn't in my background or the context, say so briefly rather than fabricating.\n"
        "- Sound warm, natural, and confident — reflective, not a robotic bullet dump.\n"
        "- Keep responses under ~220 words.\n"
        "- Use light markdown (**bold**, short paragraphs). No large headings (## or #)."
    )
    if is_interview:
        base += (
            "\n\nINTERVIEW MODE: Follow the ACTIVE MODE instructions above. Present me as a "
            "strong, likeable candidate while staying truthful to my real experience."
        )
    if mode_prompt and mode_prompt.strip():
        base = (
            "ACTIVE MODE (follow these instructions strictly):\n"
            f"{mode_prompt.strip()}\n\n" + base
        )
    return base


def _build_context_message(
    question: str,
    transcript: str = "",
    rag: str = "",
    screen: str = "",
    experience_chunks: Optional[List] = None,
    kb_chunks: Optional[List] = None,
    memory_chunks: Optional[List] = None,
    job_description: str = "",
    company: str = "",
    role: str = "",
    is_interview: bool = False,
    experience_searched: bool = False,
) -> str:
    """Build the user message combining all available context."""
    parts = [f"**Question:** {question}"]

    if experience_chunks:
        exp_lines = []
        for c in experience_chunks[:5]:
            meta = getattr(c, "metadata", None) or {}
            text = c.text if hasattr(c, "text") else str(c)
            role_c = meta.get("role", "")
            company_c = meta.get("company", "")
            period = ""
            if meta.get("start_date") or meta.get("end_date"):
                period = f" ({meta.get('start_date', '?')} - {meta.get('end_date', 'Present')})"
            header_bits = [b for b in [role_c, company_c] if b]
            header = " at ".join(header_bits) + period if header_bits else "Experience"
            body = meta.get("summary") or text
            exp_lines.append(f"- {header}:\n  {body}")
        parts.append(
            "\n**My Relevant Background (answer in first person from this — my real experience):**\n"
            + "\n".join(exp_lines)
        )
    elif experience_searched and not experience_chunks:
        parts.append(
            "\n**Background note:** No experience entry matched this question closely. "
            "Do NOT invent prior roles, projects, or employers — answer from general knowledge "
            "or say briefly you don't have a matching example in your background."
        )

    if memory_chunks:
        mem_lines = []
        for c in memory_chunks[:3]:
            text = c.text if hasattr(c, "text") else str(c)
            mem_lines.append(f"- {text}")
        parts.append(
            "\n**Relevant Past Conversation (same user):**\n" + "\n".join(mem_lines)
        )

    if is_interview and job_description:
        target = ""
        if role or company:
            target = f" (target role: {role} at {company})"
        parts.append(
            f"\n**Target Job Description{target}:**\n```\n{job_description[:3000]}\n```"
            "\nTailor the answer to the skills, tools, and standards this role expects."
        )

    if kb_chunks:
        kb_lines = []
        for i, c in enumerate(kb_chunks[:5]):
            text = c.text if hasattr(c, "text") else str(c)
            score = c.score if hasattr(c, "score") else 0.0
            kb_lines.append(f"[Source {i+1}, relevance: {score:.2f}]\n{text}")
        parts.append("\n**Relevant Knowledge Base:**\n" + "\n\n".join(kb_lines))

    if transcript:
        parts.append(
            f"\n**Recent Conversation Transcript:**\n```\n{transcript[-3000:]}\n```"
        )

    if rag:
        parts.append(
            f"\n**Additional Knowledge Base Context:**\n{rag}"
        )

    if screen:
        parts.append(
            f"\n**On-Screen Text (OCR):**\n```\n{screen[:2000]}\n```"
        )

    parts.append(
        "\nAnswer the question using my background and the context above. If my background is "
        "relevant, answer in the first person about what I actually did."
    )

    return "\n".join(parts)
