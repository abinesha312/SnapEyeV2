"""
Profile / Experience API Routes

Manage the user's professional experience and education entries. Each entry is stored
in a per-user JSON file (canonical) and indexed into ChromaDB for semantic matching.
"""
import logging
from typing import Optional

from fastapi import APIRouter, Depends
from fastapi.responses import JSONResponse
from pydantic import BaseModel, Field

from security import auth_handler
from services.rag_service import rag_service

logger = logging.getLogger(__name__)

router = APIRouter(
    prefix="/api/profile",
    tags=["Profile / Experience"],
)


class ExperienceRequest(BaseModel):
    """A single experience or education entry."""
    company: str = Field(default="", description="Company or school name")
    role: str = Field(default="", description="Role / title / degree")
    start_date: str = Field(default="", description="Start (e.g. 'Jan 2022')")
    end_date: str = Field(default="", description="End (e.g. 'Present')")
    summary: str = Field(default="", description="What you did / accomplished")
    entry_type: str = Field(default="work", description="'work' or 'education'")


@router.get(
    "/experiences",
    summary="List experience & education entries",
)
async def list_experiences(user_data=Depends(auth_handler.verify_auth)):
    """Return all saved experience/education entries."""
    try:
        entries = rag_service.list_experiences(username=user_data.get("sub"))
        return JSONResponse(content={"experiences": entries, "total": len(entries)})
    except Exception as e:
        logger.exception(f"List experiences error: {e}")
        return JSONResponse(status_code=500, content={"error": str(e)})


@router.post(
    "/experiences",
    summary="Add an experience or education entry",
)
async def add_experience(
    request: ExperienceRequest,
    user_data=Depends(auth_handler.verify_auth),
):
    """Create a new entry and embed it into the experience vector store."""
    try:
        result = await rag_service.add_experience(
            company=request.company,
            role=request.role,
            start_date=request.start_date,
            end_date=request.end_date,
            summary=request.summary,
            entry_type=request.entry_type,
            username=user_data.get("sub"),
        )
        if "error" in result:
            return JSONResponse(status_code=400, content=result)
        return JSONResponse(content=result)
    except Exception as e:
        logger.exception(f"Add experience error: {e}")
        return JSONResponse(status_code=500, content={"error": str(e)})


@router.put(
    "/experiences/{entry_id}",
    summary="Update an experience or education entry",
)
async def update_experience(
    entry_id: str,
    request: ExperienceRequest,
    user_data=Depends(auth_handler.verify_auth),
):
    """Update an entry (re-embeds the new content) keeping its id."""
    try:
        result = await rag_service.update_experience(
            entry_id,
            company=request.company,
            role=request.role,
            start_date=request.start_date,
            end_date=request.end_date,
            summary=request.summary,
            entry_type=request.entry_type,
            username=user_data.get("sub"),
        )
        if "error" in result:
            return JSONResponse(status_code=400, content=result)
        return JSONResponse(content=result)
    except Exception as e:
        logger.exception(f"Update experience error: {e}")
        return JSONResponse(status_code=500, content={"error": str(e)})


@router.delete(
    "/experiences/{entry_id}",
    summary="Delete an experience or education entry",
)
async def delete_experience(
    entry_id: str,
    user_data=Depends(auth_handler.verify_auth),
):
    """Remove an entry from the experience vector store."""
    try:
        ok = rag_service.delete_experience(entry_id, username=user_data.get("sub"))
        if ok:
            return JSONResponse(content={"deleted": True, "entry_id": entry_id})
        return JSONResponse(status_code=404, content={"error": "Entry not found"})
    except Exception as e:
        logger.exception(f"Delete experience error: {e}")
        return JSONResponse(status_code=500, content={"error": str(e)})


class ExperienceSearchRequest(BaseModel):
    query: str = Field(..., description="Natural-language query")
    top_k: int = Field(default=4, ge=1, le=20)


@router.post(
    "/search",
    summary="Semantic search over experience",
)
async def search_experience(
    request: ExperienceSearchRequest,
    user_data=Depends(auth_handler.verify_auth),
):
    """Vector search over the user's experience/education entries."""
    try:
        chunks = await rag_service.search_experience(
            request.query, top_k=request.top_k, username=user_data.get("sub")
        )
        return JSONResponse(content={
            "query": request.query,
            "results": [c.to_dict() for c in chunks],
            "total": len(chunks),
        })
    except Exception as e:
        logger.exception(f"Experience search error: {e}")
        return JSONResponse(status_code=500, content={"error": str(e)})
