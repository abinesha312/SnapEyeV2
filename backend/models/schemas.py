"""
Data Models and Schemas
Pydantic models for request/response validation
"""
from pydantic import BaseModel, Field, validator
from typing import Optional, List, Dict, Any
from datetime import datetime
from enum import Enum


class VoiceType(str, Enum):
    """Available voice types for transcription"""
    ALLOY = "alloy"
    ECHO = "echo"
    FABLE = "fable"
    ONYX = "onyx"
    NOVA = "nova"
    SHIMMER = "shimmer"


class SearchType(str, Enum):
    """Search type enumeration"""
    IMAGE = "image"
    TEXT = "text"
    AUDIO = "audio"


# ==================== Request Models ====================

class LoginRequest(BaseModel):
    """User login request"""
    username: str = Field(..., min_length=3, max_length=50)
    api_key: str = Field(..., min_length=10)
    
    class Config:
        json_schema_extra = {
            "example": {
                "username": "user123",
                "api_key": "sk-..."
            }
        }


class SearchRequest(BaseModel):
    """Text search request"""
    query: str = Field(..., min_length=1, max_length=2000, description="Search query")
    max_results: Optional[int] = Field(5, ge=1, le=20, description="Maximum results to return")
    temperature: Optional[float] = Field(0.7, ge=0, le=2, description="Response creativity (0-2)")
    
    @validator('query')
    def query_not_empty(cls, v):
        if not v.strip():
            raise ValueError('Query cannot be empty')
        return v.strip()
    
    class Config:
        json_schema_extra = {
            "example": {
                "query": "What is artificial intelligence?",
                "max_results": 5,
                "temperature": 0.7
            }
        }


class ImageSearchRequest(BaseModel):
    """Image search request"""
    query: str = Field("Analyze this image in detail", max_length=500)
    temperature: Optional[float] = Field(0.7, ge=0, le=2)
    include_objects: Optional[bool] = True
    include_text: Optional[bool] = True
    
    class Config:
        json_schema_extra = {
            "example": {
                "query": "What objects are in this image?",
                "temperature": 0.7,
                "include_objects": True
            }
        }


class TranscribeRequest(BaseModel):
    """Transcription configuration request"""
    voice: Optional[VoiceType] = VoiceType.ALLOY
    language: Optional[str] = "en"
    instructions: Optional[str] = None
    
    class Config:
        json_schema_extra = {
            "example": {
                "voice": "alloy",
                "language": "en"
            }
        }


class DecryptRequest(BaseModel):
    """Decryption request"""
    encrypted_data: str = Field(..., min_length=1)
    
    class Config:
        json_schema_extra = {
            "example": {
                "encrypted_data": "gAAAAABhk..."
            }
        }


# ==================== Response Models ====================

class TokenResponse(BaseModel):
    """JWT token response"""
    access_token: str
    token_type: str = "bearer"
    expires_in: int
    refresh_token: Optional[str] = None
    
    class Config:
        json_schema_extra = {
            "example": {
                "access_token": "eyJ...",
                "token_type": "bearer",
                "expires_in": 3600
            }
        }


class SearchResponse(BaseModel):
    """Generic search response"""
    success: bool
    result: Optional[str] = None  # Plain text result (always provided if successful)
    encrypted_result: Optional[str] = None  # Encrypted result (only if encrypt=True)
    timestamp: str
    model: str
    search_type: SearchType
    metadata: Optional[Dict[str, Any]] = None
    
    class Config:
        json_schema_extra = {
            "example": {
                "success": True,
                "result": "Artificial intelligence is...",
                "timestamp": "2024-01-01T00:00:00",
                "model": "gpt-4o",
                "search_type": "text",
                "metadata": {
                    "tokens_used": 150,
                    "temperature": 0.7
                }
            }
        }


class ErrorResponse(BaseModel):
    """Error response"""
    success: bool = False
    error: str
    error_code: Optional[str] = None
    timestamp: str = Field(default_factory=lambda: datetime.utcnow().isoformat())
    
    class Config:
        json_schema_extra = {
            "example": {
                "success": False,
                "error": "Invalid request",
                "error_code": "INVALID_INPUT",
                "timestamp": "2024-01-01T00:00:00"
            }
        }


class HealthResponse(BaseModel):
    """Health check response"""
    status: str
    timestamp: str
    version: str
    services: Dict[str, str]
    
    class Config:
        json_schema_extra = {
            "example": {
                "status": "healthy",
                "timestamp": "2024-01-01T00:00:00",
                "version": "2.0.0",
                "services": {
                    "openai": "connected",
                    "database": "connected"
                }
            }
        }


# ==================== Internal Models ====================

class UserSession(BaseModel):
    """User session data"""
    user_id: str
    username: str
    created_at: datetime
    last_active: datetime
    permissions: List[str] = []


class TranscriptionMessage(BaseModel):
    """Transcription message format"""
    type: str
    content: str
    timestamp: str
    encrypted: bool = False
    metadata: Optional[Dict[str, Any]] = None

