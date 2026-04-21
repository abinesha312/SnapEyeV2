"""
OCR Service for SnapEye
Extracts text from screenshots using Tesseract OCR or GPT-4 Vision fallback.
"""
import base64
import io
import logging
from typing import Optional, Dict

from PIL import Image

from config import settings

logger = logging.getLogger(__name__)


class OCRService:
    """
    Optical Character Recognition service.
    
    Primary: Tesseract OCR (fast, local, free)
    Fallback: GPT-4 Vision (slower, paid, more accurate for complex layouts)
    """

    def __init__(self):
        self._tesseract_available = False
        try:
            import pytesseract
            pytesseract.get_tesseract_version()
            self._tesseract_available = True
            logger.info("Tesseract OCR available")
        except Exception:
            logger.warning("Tesseract OCR not available, will use GPT-4 Vision fallback")

    async def extract_text(
        self,
        image_base64: str,
        use_ai: bool = False,
        query: Optional[str] = None
    ) -> Dict:
        """
        Extract text from a base64-encoded image.

        Args:
            image_base64: Base64-encoded PNG/JPEG image
            use_ai: If True, always use GPT-4 Vision instead of Tesseract
            query: Optional query for AI analysis (only used with GPT-4 Vision)

        Returns:
            Dict with 'text', optional 'analysis', and metadata
        """
        try:
            image_bytes = base64.b64decode(image_base64)
            image = Image.open(io.BytesIO(image_bytes))
            width, height = image.size

            result: Dict = {
                "success": True,
                "width": width,
                "height": height,
                "method": "",
                "text": "",
            }

            if use_ai or not self._tesseract_available:
                ai_result = await self._extract_with_vision(image_base64, query)
                result["method"] = "gpt4_vision"
                result["text"] = ai_result.get("text", "")
                result["analysis"] = ai_result.get("analysis", "")
            else:
                ocr_text = self._extract_with_tesseract(image)
                result["method"] = "tesseract"
                result["text"] = ocr_text

                # If Tesseract returns very little text and AI is available, supplement
                if len(ocr_text.strip()) < 10 and settings.OPENAI_API_KEY:
                    ai_result = await self._extract_with_vision(image_base64, query)
                    result["text"] = ai_result.get("text", ocr_text)
                    result["analysis"] = ai_result.get("analysis", "")
                    result["method"] = "tesseract+gpt4_vision"

            logger.info(
                f"OCR extracted {len(result['text'])} chars via {result['method']} "
                f"from {width}x{height} image"
            )
            return result

        except Exception as e:
            logger.exception(f"OCR extraction error: {e}")
            return {
                "success": False,
                "error": str(e),
                "text": "",
                "method": "error",
            }

    def _extract_with_tesseract(self, image: Image.Image) -> str:
        """Extract text using Tesseract OCR."""
        import pytesseract
        text = pytesseract.image_to_string(image)
        return text.strip()

    async def _extract_with_vision(
        self,
        image_base64: str,
        query: Optional[str] = None
    ) -> Dict:
        """Extract text and optionally analyze using GPT-4 Vision."""
        try:
            from openai import OpenAI

            client = OpenAI(api_key=settings.OPENAI_API_KEY)
            prompt = query or "Extract all visible text from this image. Return the text exactly as it appears."

            response = client.chat.completions.create(
                model="gpt-4o",
                messages=[
                    {
                        "role": "user",
                        "content": [
                            {"type": "text", "text": prompt},
                            {
                                "type": "image_url",
                                "image_url": {
                                    "url": f"data:image/png;base64,{image_base64}",
                                    "detail": "high",
                                },
                            },
                        ],
                    }
                ],
                max_tokens=4096,
            )

            content = response.choices[0].message.content or ""
            return {"text": content, "analysis": content if query else ""}

        except Exception as e:
            logger.error(f"GPT-4 Vision OCR error: {e}")
            return {"text": "", "analysis": f"Error: {e}"}


# Global service instance
ocr_service = OCRService()
