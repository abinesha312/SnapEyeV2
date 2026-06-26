"""
OCR Service for SnapEye

Tesseract-only text extraction. The pytesseract Python wrapper needs the actual
Tesseract binary, which on Windows is usually installed at
``C:\\Program Files\\Tesseract-OCR\\tesseract.exe`` but isn't always added to PATH.
We auto-detect that location (and a few other common ones) and configure
``pytesseract.pytesseract.tesseract_cmd`` directly so OCR works without forcing
the user to edit their PATH or reinstall.
"""
import asyncio
import base64
import io
import logging
import os
import shutil
from typing import Dict, Optional

from PIL import Image

from config import settings

logger = logging.getLogger(__name__)


def _resolve_tesseract_cmd() -> Optional[str]:
    """
    Locate the tesseract executable. Returns the first candidate that exists, or
    ``None`` if no Tesseract install can be found. Order:

    1. ``TESSERACT_CMD`` env var (and the matching settings value).
    2. Standard Windows install locations.
    3. ``shutil.which("tesseract")`` (PATH lookup, mostly catches Linux/macOS).
    """
    env_cmd = (
        os.environ.get("TESSERACT_CMD")
        or getattr(settings, "TESSERACT_CMD", "")
        or ""
    ).strip()

    candidates = [
        env_cmd or None,
        r"C:\Program Files\Tesseract-OCR\tesseract.exe",
        r"C:\Program Files (x86)\Tesseract-OCR\tesseract.exe",
        os.path.expanduser(r"~\AppData\Local\Programs\Tesseract-OCR\tesseract.exe"),
        shutil.which("tesseract"),
    ]
    for c in candidates:
        if c and os.path.isfile(c):
            return c
    return None


# Standard install hint shown to the user when Tesseract is not detected.
_INSTALL_HINT = (
    "Tesseract OCR is not installed or not detected. "
    "Install it from https://github.com/UB-Mannheim/tesseract/wiki "
    "(default location C:\\Program Files\\Tesseract-OCR\\) and restart the backend, "
    "or set the TESSERACT_CMD environment variable to the full path of tesseract.exe."
)


class OCRService:
    """Tesseract-backed OCR service. No external API calls."""

    def __init__(self):
        self._tesseract_available = False
        self._tesseract_version: Optional[str] = None
        self._tesseract_cmd: Optional[str] = None

        try:
            import pytesseract

            cmd = _resolve_tesseract_cmd()
            if cmd:
                pytesseract.pytesseract.tesseract_cmd = cmd
                self._tesseract_cmd = cmd

            version = pytesseract.get_tesseract_version()
            self._tesseract_available = True
            self._tesseract_version = str(version)
            logger.info(
                f"Tesseract OCR available (v{version}) at {cmd or 'PATH'}"
            )
        except Exception as e:
            logger.warning(f"Tesseract OCR not available: {e}. {_INSTALL_HINT}")

    async def extract_text(
        self,
        image_base64: str,
        use_ai: bool = False,  # kept in signature for API compatibility, ignored
        query: Optional[str] = None,  # ignored — Tesseract has no analysis mode
    ) -> Dict:
        """
        Extract text from a base64-encoded image using Tesseract only.

        Returns a dict with:
            - success (bool)
            - text (str)
            - method ("tesseract" | "unavailable" | "error")
            - width / height (only on success)
            - error (only on failure)
        """
        if not self._tesseract_available:
            return {
                "success": False,
                "error": _INSTALL_HINT,
                "text": "",
                "method": "unavailable",
            }

        try:
            image_bytes = base64.b64decode(image_base64)
            image = Image.open(io.BytesIO(image_bytes))
            width, height = image.size

            # Tesseract is CPU-bound and synchronous; run it on a thread so we
            # don't block the FastAPI event loop while a (potentially large)
            # screenshot is being processed.
            text = await asyncio.to_thread(self._extract_with_tesseract, image)

            logger.info(
                f"OCR extracted {len(text)} chars via tesseract from {width}x{height} image"
            )
            return {
                "success": True,
                "text": text,
                "method": "tesseract",
                "width": width,
                "height": height,
            }

        except Exception as e:
            logger.exception(f"OCR extraction error: {e}")
            return {
                "success": False,
                "error": str(e),
                "text": "",
                "method": "error",
            }

    @staticmethod
    def _extract_with_tesseract(image: Image.Image) -> str:
        import pytesseract
        text = pytesseract.image_to_string(image)
        return text.strip()

    @property
    def is_available(self) -> bool:
        return self._tesseract_available

    @property
    def info(self) -> Dict:
        return {
            "available": self._tesseract_available,
            "version": self._tesseract_version,
            "cmd": self._tesseract_cmd,
        }


ocr_service = OCRService()
