"""API routes package"""
from . import routes_auth, routes_search, routes_realtime, routes_deepgram_live
from . import routes_llm, routes_rag, routes_ocr

__all__ = [
    "routes_auth", "routes_search", "routes_realtime", "routes_deepgram_live",
    "routes_llm", "routes_rag", "routes_ocr",
]

