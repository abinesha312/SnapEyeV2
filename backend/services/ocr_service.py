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
import re
import shutil
from typing import Dict, List, Optional

from PIL import Image

from config import settings

logger = logging.getLogger(__name__)

# Full tessdata/main English model (legacy + LSTM, ~23 MB). Much more accurate on screen
# text than the ~4 MB "fast" model the Windows installer ships.
_LOCAL_TESSDATA_DIR = os.path.join(os.path.dirname(os.path.dirname(os.path.abspath(__file__))), "tessdata")


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


_UI_PATTERNS = [
    r'^.+ - Notepad\+\+',
    r'^(length:|lines:|Ln:|Col:|Pos:).*',
    r'^(Windows.*|UTF-8.*|INS|OVR)$',
    r'^(Description|Editorial|Solutions|Submissions)$',
    r'^\d+$',
    # Mostly non-letters (icon debris), but keep code-ish lines with letters/digits.
    lambda s: sum(c.isalpha() for c in s) / max(len(s), 1) < 0.25,
    lambda s: sum(1 for c in s if ord(c) > 127) > len(s) * 0.3,
    lambda s: sum(1 for c in s if c in '[]{}()|\\/~`!@#$%^&*+=<>?') > len(s) * 0.5,
    lambda s: len(re.findall(r'[A-Za-z]{2,}', s)) < 1 and len(s) > 10,
    # Toolbar junk ("sHEHBR") — keep CamelCase / ALLCAPS identifiers (class names, enums).
    lambda s: _plausible_word_ratio(s) < 0.35 and not re.search(r'[A-Z][a-z]+[A-Z]', s),
]

_PLAUSIBLE_WORD = re.compile(r"[(\"']?(?:[A-Za-z][a-z]*|[A-Z]+|\d+(?:[.,:]\d+)*)[.,:;!?'\")]*")


def _plausible_word_ratio(line: str) -> float:
    tokens = line.split()
    if not tokens:
        return 0.0
    return sum(1 for t in tokens if _PLAUSIBLE_WORD.fullmatch(t)) / len(tokens)


def _normalize_line(line: str) -> str:
    return re.sub(r'[\W_]+', '', line.lower())


def _clean_ocr_lines(raw_lines: List[str]) -> List[str]:
    """
    Drop editor chrome, pure numbers, and OCR garbage lines, then strip leading
    line numbers and collapse whitespace. Ported from the original SnapEye.
    """
    cleaned = []
    for line in raw_lines:
        stripped = line.strip()
        if not stripped:
            continue

        drop = False
        for pat in _UI_PATTERNS:
            if isinstance(pat, str):
                if re.match(pat, stripped):
                    drop = True
                    break
            elif pat(stripped):
                drop = True
                break
        if drop:
            continue

        stripped = re.sub(r'^[\d\.\s]+', '', stripped)
        stripped = re.sub(r'\s+', ' ', stripped)
        stripped = re.sub(r'^[^\w\s]+', '', stripped)
        stripped = re.sub(r'[^\w\s]+$', '', stripped)
        cleaned.append(stripped)

    return [
        line.strip()
        for line in cleaned
        if len(line.strip()) >= 3 and not re.match(r'^[^\w\s]*$', line)
    ]


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
        # Upscale helps thin UI fonts; cap size so dual-PSM work stays under ~few seconds
        # instead of 60–90s when Pause/Play queues several full screens at once.
        image = image.convert("RGB")
        scale = 2.0 if image.width * image.height < 2_000_000 else 1.5
        if scale != 1.0:
            image = image.resize(
                (int(image.width * scale), int(image.height * scale)),
                Image.LANCZOS,
            )
        base = "--oem 3"
        if os.path.isfile(os.path.join(_LOCAL_TESSDATA_DIR, "eng.traineddata")):
            # pytesseract keeps literal quotes on Windows, so only quote paths with spaces.
            tessdata = _LOCAL_TESSDATA_DIR.replace("\\", "/")
            if " " in tessdata:
                tessdata = f'"{tessdata}"'
            base = f"--tessdata-dir {tessdata} {base}"

        def run(psm: int) -> List[str]:
            raw = pytesseract.image_to_string(image, lang="eng", config=f"{base} --psm {psm}")
            return _clean_ocr_lines(raw.splitlines())

        # Dense first; only run sparse PSM when dense is thin (avoids always paying 2× CPU).
        dense = run(3)
        merged = list(dense)
        if len("\n".join(dense)) < 180:
            sparse = run(11)
            seen = [_normalize_line(line) for line in dense]
            for line in sparse:
                key = _normalize_line(line)
                if not key or any(key in s for s in seen):
                    continue
                merged.append(line)
                seen.append(key)
        return "\n".join(merged)

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
