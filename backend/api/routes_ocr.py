"""
OCR API Routes
Accepts screenshots and returns extracted text via Tesseract or GPT-4 Vision.
"""
import logging
from typing import Optional

from fastapi import APIRouter, Depends
from fastapi.responses import JSONResponse
from pydantic import BaseModel, Field

from security import auth_handler
from services.ocr_service import ocr_service

logger = logging.getLogger(__name__)

router = APIRouter(
    prefix="/api/ocr",
    tags=["OCR"],
)


class OCRRequest(BaseModel):
    """Request body for OCR extraction."""
    image: str = Field(..., description="Base64-encoded PNG or JPEG image")
    format: str = Field(default="png", description="Image format (png, jpeg)")
    use_ai: bool = Field(default=False, description="Force GPT-4 Vision instead of Tesseract")
    query: Optional[str] = Field(default=None, description="Optional query for AI analysis")


@router.post(
    "/extract",
    summary="Extract text from image",
    description="Send a base64-encoded screenshot and get extracted text back. "
                "Uses Tesseract OCR by default, GPT-4 Vision as fallback or when use_ai=True."
)
async def extract_text(
    request: OCRRequest,
    user_data=Depends(auth_handler.verify_auth),
):
    """Extract text from a screenshot image."""
    try:
        result = await ocr_service.extract_text(
            image_base64=request.image,
            use_ai=request.use_ai,
            query=request.query,
        )

        if not result.get("success", False):
            return JSONResponse(
                status_code=500,
                content={"error": result.get("error", "OCR failed"), "text": ""},
            )

        return JSONResponse(content=result)

    except Exception as e:
        logger.exception(f"OCR endpoint error: {e}")
        return JSONResponse(
            status_code=500,
            content={"error": str(e), "text": ""},
        )
