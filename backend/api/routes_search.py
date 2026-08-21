"""
Search Routes
Handles image and text search endpoints
"""
import logging
from fastapi import APIRouter, File, UploadFile, HTTPException, Depends
from fastapi.responses import JSONResponse

import sys
import os
sys.path.insert(0, os.path.dirname(os.path.dirname(os.path.abspath(__file__))))

from models import SearchRequest, ImageSearchRequest, SearchResponse, ErrorResponse, DecryptRequest
from security import auth_handler, security_manager
from config import settings

# image_service/text_service are OpenAI-SDK-backed singletons. They're imported
# lazily inside each route handler below (not here) so that simply loading this
# router module - which happens at backend startup - doesn't force the OpenAI SDK
# import before the server has even bound its port.

# Configure logging
logger = logging.getLogger(__name__)

# Create router
router = APIRouter(
    prefix="/api/search",
    tags=["Search"],
    dependencies=[Depends(auth_handler.verify_auth)],
    responses={
        401: {"model": ErrorResponse, "description": "Unauthorized"},
        400: {"model": ErrorResponse, "description": "Bad Request"},
        500: {"model": ErrorResponse, "description": "Internal Server Error"}
    }
)


@router.post(
    "/image",
    response_model=SearchResponse,
    summary="Image Search",
    description="Analyze images and answer questions about them"
)
async def image_search(
    file: UploadFile = File(..., description="Image file (JPEG, PNG, WebP)"),
    query: str = "Analyze this image in detail",
    temperature: float = 0.7,
    encrypt: bool = False,
    user_data=Depends(auth_handler.verify_auth)
):
    """
    Search and analyze images using GPT-4 Vision
    
    **Features:**
    - Object detection and identification
    - Scene understanding
    - Text extraction (OCR)
    - Question answering about image content
    - Contextual analysis
    
    **Supported Formats:**
    - JPEG/JPG
    - PNG
    - WebP
    
    **Max File Size:** 10MB
    
    **Returns:**
    - Encrypted analysis result
    - Metadata (tokens used, timestamp, etc.)
    """
    try:
        # Validate file type
        if file.content_type not in settings.ALLOWED_IMAGE_TYPES:
            raise HTTPException(
                status_code=400,
                detail=f"Invalid image format. Allowed: {', '.join(settings.ALLOWED_IMAGE_TYPES)}"
            )
        
        # Read image data
        image_data = await file.read()
        
        # Validate file size
        size_mb = len(image_data) / (1024 * 1024)
        if size_mb > settings.MAX_FILE_SIZE_MB:
            raise HTTPException(
                status_code=400,
                detail=f"Image too large: {size_mb:.2f}MB (max {settings.MAX_FILE_SIZE_MB}MB)"
            )
        
        logger.info(f"Image search request from user: {user_data.get('sub')} - {query[:50]}")

        from services import image_service

        # Process image search
        result = await image_service.process(
            image_data=image_data,
            query=query,
            temperature=temperature
        )
        
        return JSONResponse(content=result)
        
    except HTTPException:
        raise
    except Exception as e:
        logger.exception(f"Image search error: {str(e)}")
        raise HTTPException(
            status_code=500,
            detail=f"Image search failed: {str(e)}"
        )


@router.post(
    "/text",
    response_model=SearchResponse,
    summary="Text Search",
    description="Search and get answers to text-based questions"
)
async def text_search(
    request: SearchRequest,
    encrypt: bool = False,
    user_data=Depends(auth_handler.verify_auth)
):
    """
    Text-based search and question answering
    
    **Features:**
    - Natural language understanding
    - Factual question answering
    - Information retrieval
    - Contextual explanations
    - Structured responses
    
    **Parameters:**
    - **query**: Your question or search query
    - **temperature**: Response creativity (0=focused, 2=creative)
    - **max_results**: Number of results (1-20)
    
    **Returns:**
    - Encrypted search result
    - Metadata (tokens used, timestamp, etc.)
    """
    try:
        logger.info(f"Text search request from user: {user_data.get('sub')} - {request.query[:50]}")

        from services import text_service

        # Process text search
        result = await text_service.process(
            query=request.query,
            temperature=request.temperature
        )
        
        return JSONResponse(content=result)
        
    except Exception as e:
        logger.exception(f"Text search error: {str(e)}")
        raise HTTPException(
            status_code=500,
            detail=f"Text search failed: {str(e)}"
        )


@router.post(
    "/decrypt",
    summary="Decrypt Result",
    description="Decrypt encrypted search results"
)
async def decrypt_result(
    request: DecryptRequest,
    user_data=Depends(auth_handler.verify_auth)
):
    """
    Decrypt encrypted search results
    
    **Use Case:**
    - Decrypt results received from search endpoints
    - Client-side can't handle encryption
    - Debugging encrypted responses
    
    **Security:**
    - Requires valid authentication token
    - Uses Fernet symmetric encryption
    - Original encryption key required
    """
    try:
        decrypted = security_manager.decrypt_data(request.encrypted_data)
        
        return JSONResponse(
            content={
                "success": True,
                "decrypted_result": decrypted
            }
        )
        
    except Exception as e:
        logger.error(f"Decryption error: {str(e)}")
        raise HTTPException(
            status_code=400,
            detail=f"Decryption failed. Invalid data or key: {str(e)}"
        )


@router.get(
    "/history",
    summary="Search History",
    description="Get user's search history (placeholder)"
)
async def get_search_history(
    limit: int = 10,
    user_data=Depends(auth_handler.verify_auth)
):
    """
    Get user's search history
    
    **Note:** This is a placeholder. Implement with database in production.
    """
    return JSONResponse(
        content={
            "message": "Search history endpoint - implement with database",
            "user": user_data.get("sub"),
            "limit": limit
        }
    )

