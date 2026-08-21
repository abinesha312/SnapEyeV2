"""
Configuration Management Module
Loads settings from config.yaml, then overrides with environment variables / .env.
Fail-safe: uses hardcoded defaults if YAML is missing or malformed.
"""
import os
import logging
import secrets
from pathlib import Path
from typing import Optional
from pydantic_settings import BaseSettings
from cryptography.fernet import Fernet

logger = logging.getLogger(__name__)


def _load_yaml_defaults() -> dict:
    """Load defaults from config/config.yaml if available."""
    try:
        import yaml
    except ImportError:
        return {}

    yaml_paths = [
        Path(__file__).parent / "config.yaml",
        Path(__file__).parent.parent / "config.yaml",
    ]
    for yp in yaml_paths:
        if yp.exists():
            try:
                with open(yp, "r") as f:
                    data = yaml.safe_load(f) or {}
                logger.info(f"Loaded config from {yp}")
                return data
            except Exception as e:
                logger.warning(f"Failed to parse {yp}: {e}")
    return {}


_yaml = _load_yaml_defaults()


class Settings(BaseSettings):
    """Application settings with validation"""
    
    # API Configuration (YAML -> env -> default)
    APP_NAME: str = _yaml.get("app", {}).get("name", "SnapEye AI")
    APP_VERSION: str = _yaml.get("app", {}).get("version", "2.0.0")
    API_HOST: str = _yaml.get("server", {}).get("host", "0.0.0.0")
    API_PORT: int = _yaml.get("server", {}).get("port", 8080)
    DEBUG: bool = _yaml.get("app", {}).get("debug", False)
    
    # Security
    SECRET_KEY: str = os.getenv("SECRET_KEY", secrets.token_urlsafe(32))
    ENCRYPTION_KEY: str = os.getenv("ENCRYPTION_KEY", Fernet.generate_key().decode())
    JWT_ALGORITHM: str = "HS256"
    ACCESS_TOKEN_EXPIRE_MINUTES: int = 60
    REFRESH_TOKEN_EXPIRE_DAYS: int = 7
    
    def get_encryption_key_bytes(self) -> bytes:
        """Get encryption key as bytes for Fernet"""
        if isinstance(self.ENCRYPTION_KEY, bytes):
            return self.ENCRYPTION_KEY
        return self.ENCRYPTION_KEY.encode() if isinstance(self.ENCRYPTION_KEY, str) else Fernet.generate_key()
    
    # Deepgram Configuration (Audio-to-Text Transcription)
    DEEPGRAM_API_KEY: str = os.getenv("DEEPGRAM_API_KEY", "")
    DEEPGRAM_MODEL: str = "nova-3"  # nova-3 is the latest and most accurate model
    DEEPGRAM_LANGUAGE: str = "en"
    DEEPGRAM_SMART_FORMAT: bool = True
    DEEPGRAM_PUNCTUATE: bool = True
    DEEPGRAM_DIARIZE: bool = True
    
    # Audio Transcription Settings
    AUDIO_CHUNK_DURATION: float = 0.1  # Process audio every 100ms (matches buffer_size_ms)
    PAUSE_THRESHOLD: float = 5.0  # Create new message after 5 seconds of silence
    AUDIO_SAMPLE_RATE: int = 24000  # 24kHz
    AUDIO_CHANNELS: int = 1  # Mono
    AUDIO_ENCODING: str = "linear16"  # PCM16
    
    # OpenAI Configuration
    OPENAI_API_KEY: str = os.getenv("OPENAI_API_KEY", "")
    OPENAI_MODEL: str = "gpt-4o"
    OPENAI_EMBEDDING_MODEL: str = "text-embedding-3-small"
    
    # Anthropic Configuration
    ANTHROPIC_API_KEY: str = os.getenv("ANTHROPIC_API_KEY", "")
    ANTHROPIC_MODEL: str = "claude-haiku-4-5-20251001"

    # Google Gemini Configuration (server-side default; per-request override supported)
    GEMINI_API_KEY: str = os.getenv("GEMINI_API_KEY", "")
    GEMINI_MODEL: str = "gemini-2.5-flash"

    # xAI / Grok Configuration (OpenAI-compatible; per-request override supported)
    XAI_API_KEY: str = os.getenv("XAI_API_KEY", "")
    XAI_MODEL: str = "grok-4.20"
    XAI_BASE_URL: str = "https://api.x.ai/v1"

    # OCR Configuration
    # Optional override for the Tesseract executable path. Leave blank to let the
    # OCR service auto-detect common Windows install locations.
    TESSERACT_CMD: str = os.getenv("TESSERACT_CMD", "")

    # LLM Router Configuration
    LLM_PRIMARY_PROVIDER: str = "anthropic"  # "openai", "anthropic", "gemini", or "grok"
    LLM_FALLBACK_PROVIDER: str = "openai"  # fallback when primary fails
    LLM_STREAM_ENABLED: bool = True
    LLM_MAX_TOKENS: int = 4096
    LLM_TEMPERATURE: float = 0.7
    # Per-provider request timeout for the failover loop, in seconds. Bounds how long
    # a hung/unresponsive provider can stall before we move to the next one, instead
    # of relying on the SDK's own (often much longer) default timeout. Applies to
    # NON-streaming (full-response) calls, where the whole answer takes this long.
    LLM_PROVIDER_TIMEOUT_SECONDS: float = 20.0
    # Hard budget for the FIRST streamed token, in seconds - the user-facing
    # responsiveness SLA. Normal first-token latency is ~0.5s; if a provider hasn't
    # produced anything by this deadline the attempt is abandoned and retried/failed-
    # over immediately rather than leaving the user staring at a spinner.
    LLM_FIRST_TOKEN_TIMEOUT_SECONDS: float = 3.0
    
    # RAG Configuration
    RAG_CHUNK_SIZE: int = 500  # tokens per chunk
    RAG_CHUNK_OVERLAP: int = 50  # overlap between chunks
    RAG_TOP_K: int = 5  # number of results to retrieve
    RAG_DATA_DIR: str = "./data/chromadb"  # used only when CHROMA_HOST is empty (embedded mode)
    # When set, rag_service connects to a standalone ChromaDB server (e.g. the
    # "chromadb" container in podman-compose.yml) via HttpClient instead of an
    # embedded PersistentClient. Empty by default so local dev without Podman
    # still works out of the box.
    CHROMA_HOST: str = os.getenv("CHROMA_HOST", "")
    CHROMA_PORT: int = int(os.getenv("CHROMA_PORT", "8000"))
    # Minimum cosine-similarity score (0..1) an experience match must clear before it's
    # used to ground an answer. Below this, the assistant falls back to general
    # knowledge instead of stretching a weak match into a fabricated claim.
    #
    # Calibrated empirically against OpenAI text-embedding-3-small, whose cosine
    # similarities run much lower than a naive 0..1 scale suggests: an unrelated query
    # ("chocolate cake recipe" vs. a Kafka/backend-engineer entry) scored ~0.01, a
    # genuinely relevant but keyword-free query ("distributed streaming systems" vs.
    # the same "Kafka event pipeline" entry) scored ~0.36, and a close/direct match
    # scored ~0.60. A 0.55 threshold would have wrongly discarded the second (clearly
    # relevant) case, so this sits well below "close match" but well above "noise".
    RAG_EXPERIENCE_MIN_SCORE: float = 0.20

    # Context Management
    CONTEXT_MAX_TOKENS: int = 100000  # rolling window size
    CONTEXT_SUMMARY_THRESHOLD: int = 80000  # summarize when exceeded
    
    # Keyword Detection
    KEYWORD_DEBOUNCE_MS: int = 800  # minimum ms between triggers
    
    # Storage
    SQLITE_DB_PATH: str = "./data/snapeye.db"
    
    # Rate Limiting
    RATE_LIMIT_PER_MINUTE: int = 60
    RATE_LIMIT_PER_HOUR: int = 1000
    
    # File Upload
    MAX_FILE_SIZE_MB: int = 10
    ALLOWED_IMAGE_TYPES: list = ["image/jpeg", "image/png", "image/jpg", "image/webp"]
    
    # Database (for future use)
    DATABASE_URL: Optional[str] = None
    
    # SSL Configuration
    SSL_KEYFILE: Optional[str] = None
    SSL_CERTFILE: Optional[str] = None
    
    # CORS
    CORS_ORIGINS: list = ["*"]
    
    # Logging
    LOG_LEVEL: str = "INFO"
    LOG_FILE: Optional[str] = "logs/snapeye.log"
    
    class Config:
        env_file = ".env"
        case_sensitive = True
        extra = "ignore"  # Ignore extra fields from .env


# Global settings instance
settings = Settings()


class ModelConfig:
    """AI Model Configuration"""
    
    TEMPERATURE_MIN: float = 0.0
    TEMPERATURE_MAX: float = 2.0
    TEMPERATURE_DEFAULT: float = 0.7
    
    MAX_TOKENS_DEFAULT: int = 2000
    MAX_RESULTS_MIN: int = 1
    MAX_RESULTS_MAX: int = 20
    MAX_RESULTS_DEFAULT: int = 5
    
    # Voice options for realtime transcription
    VOICE_OPTIONS: list = ["alloy", "echo", "fable", "onyx", "nova", "shimmer"]
    VOICE_DEFAULT: str = "alloy"
    
    # Audio formats
    AUDIO_INPUT_FORMAT: str = "pcm16"
    AUDIO_OUTPUT_FORMAT: str = "pcm16"


model_config = ModelConfig()

