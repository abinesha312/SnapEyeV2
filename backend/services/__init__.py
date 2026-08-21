"""
Services package.

Lazily loads submodules on first attribute access (PEP 562) instead of importing
everything eagerly. Several submodules (openai_service, deepgram_live_service) pull
in heavy third-party SDKs at their own module scope; importing this package used to
force all of that onto the critical startup path even when only one lightweight
service (e.g. storage_service) was actually needed. `from services import X` and
`from services.X import Y` both still work exactly as before - only the timing of
the underlying submodule's import changes.
"""
from importlib import import_module

_LAZY_ATTRS = {
    "ImageSearchService": ".openai_service",
    "TextSearchService": ".openai_service",
    "OpenAIServiceFactory": ".openai_service",
    "image_service": ".openai_service",
    "text_service": ".openai_service",
    "AudioMessage": ".realtime_service",
    "DeepgramTranscriptionService": ".realtime_service",
    "DeepgramSessionManager": ".realtime_service",
    "session_manager": ".realtime_service",
    "create_transcription_service": ".realtime_service",
    "DeepgramLiveTranscriptionService": ".deepgram_live_service",
    "deepgram_session_manager": ".deepgram_live_service",
    "PromptTemplates": ".prompts",
    "llm_router": ".llm_router",
    "LLMRouter": ".llm_router",
    "LLMMessage": ".llm_router",
    "rag_service": ".rag_service",
    "RAGService": ".rag_service",
    "keyword_detector": ".keyword_service",
    "KeywordDetector": ".keyword_service",
    "context_session_manager": ".context_service",
    "ContextManager": ".context_service",
    "storage_service": ".storage_service",
    "StorageService": ".storage_service",
    "ocr_service": ".ocr_service",
    "OCRService": ".ocr_service",
}

# DeepgramSessionManager is exported under two names: the plain class (from
# realtime_service, mapped above) and this alias (the same-named class from
# deepgram_live_service, which is a distinct type).
_ALIASED_ATTR = "DeepgramLiveSessionManager"
_ALIASED_MODULE = ".deepgram_live_service"
_ALIASED_SOURCE_NAME = "DeepgramSessionManager"

__all__ = list(_LAZY_ATTRS.keys()) + [_ALIASED_ATTR]


def __getattr__(name):
    if name == _ALIASED_ATTR:
        module = import_module(_ALIASED_MODULE, __name__)
        value = getattr(module, _ALIASED_SOURCE_NAME)
    else:
        module_path = _LAZY_ATTRS.get(name)
        if module_path is None:
            raise AttributeError(f"module {__name__!r} has no attribute {name!r}")
        module = import_module(module_path, __name__)
        value = getattr(module, name)
    globals()[name] = value
    return value


def __dir__():
    return sorted(__all__)
