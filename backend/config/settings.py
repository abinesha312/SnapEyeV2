"""
Configuration Management Module
Handles all application settings and environment variables
"""
import os
import secrets
from typing import Optional
from pydantic_settings import BaseSettings
from cryptography.fernet import Fernet


class Settings(BaseSettings):
    """Application settings with validation"""
    
    # API Configuration
    APP_NAME: str = "SnapEye AI"
    APP_VERSION: str = "2.0.0"
    API_HOST: str = "0.0.0.0"
    API_PORT: int = 8000
    DEBUG: bool = False
    
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
    
    # OpenAI Configuration
    OPENAI_API_KEY: str = os.getenv("OPENAI_API_KEY", "")
    OPENAI_MODEL: str = "gpt-4o"
    OPENAI_REALTIME_MODEL: str = "gpt-4o-realtime-preview-2024-10-01"
    OPENAI_WS_URL: str = "wss://api.openai.com/v1/realtime"
    
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

