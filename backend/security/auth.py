"""
Authentication and Security Module
Handles JWT tokens, encryption, and API key management
"""
import hashlib
import secrets
from datetime import datetime, timedelta
from typing import Dict, Optional

import jwt
from cryptography.fernet import Fernet
from fastapi import HTTPException, Depends
from fastapi.security import HTTPBearer, HTTPAuthorizationCredentials

import sys
import os
sys.path.insert(0, os.path.dirname(os.path.dirname(os.path.abspath(__file__))))

from config import settings


class SecurityManager:
    """
    Centralized security management for encryption and authentication
    
    This class provides:
    - Data encryption/decryption
    - JWT token generation and verification
    - API key hashing
    - Secure token management
    """
    
    def __init__(self, encryption_key):
        """
        Initialize security manager
        
        Args:
            encryption_key: Fernet encryption key (bytes or str)
        """
        # Ensure encryption_key is bytes
        if isinstance(encryption_key, str):
            encryption_key = encryption_key.encode()
        
        try:
            self.cipher = Fernet(encryption_key)
        except Exception:
            # If invalid, generate a new key
            self.cipher = Fernet(Fernet.generate_key())
            
        self._token_blacklist = set()  # In production, use Redis
    
    def encrypt_data(self, data: str) -> str:
        """
        Encrypt sensitive data using Fernet symmetric encryption
        
        Args:
            data: Plain text data to encrypt
            
        Returns:
            Encrypted data as string
            
        Example:
            >>> security = SecurityManager(Fernet.generate_key())
            >>> encrypted = security.encrypt_data("secret message")
        """
        try:
            return self.cipher.encrypt(data.encode()).decode()
        except Exception as e:
            raise ValueError(f"Encryption failed: {str(e)}")
    
    def decrypt_data(self, encrypted_data: str) -> str:
        """
        Decrypt encrypted data
        
        Args:
            encrypted_data: Encrypted string
            
        Returns:
            Decrypted plain text
            
        Raises:
            ValueError: If decryption fails
        """
        try:
            return self.cipher.decrypt(encrypted_data.encode()).decode()
        except Exception as e:
            raise ValueError(f"Decryption failed: {str(e)}")
    
    def create_access_token(
        self,
        data: Dict,
        expires_delta: Optional[timedelta] = None
    ) -> str:
        """
        Generate JWT access token
        
        Args:
            data: Data to encode in token (e.g., user_id, permissions)
            expires_delta: Custom expiration time
            
        Returns:
            JWT token string
            
        Example:
            >>> token = security.create_access_token({"sub": "user123"})
        """
        to_encode = data.copy()
        
        if expires_delta:
            expire = datetime.utcnow() + expires_delta
        else:
            expire = datetime.utcnow() + timedelta(
                minutes=settings.ACCESS_TOKEN_EXPIRE_MINUTES
            )
        
        to_encode.update({
            "exp": expire,
            "iat": datetime.utcnow(),
            "jti": secrets.token_urlsafe(16)  # Unique token ID
        })
        
        return jwt.encode(
            to_encode,
            settings.SECRET_KEY,
            algorithm=settings.JWT_ALGORITHM
        )
    
    def create_refresh_token(self, data: Dict) -> str:
        """
        Generate JWT refresh token with longer expiration
        
        Args:
            data: Data to encode
            
        Returns:
            Refresh token string
        """
        expires_delta = timedelta(days=settings.REFRESH_TOKEN_EXPIRE_DAYS)
        return self.create_access_token(data, expires_delta)
    
    def verify_token(self, token: str) -> Dict:
        """
        Verify and decode JWT token
        
        Args:
            token: JWT token string
            
        Returns:
            Decoded token payload
            
        Raises:
            HTTPException: If token is invalid or expired
        """
        try:
            # Check if token is blacklisted
            if token in self._token_blacklist:
                raise HTTPException(
                    status_code=401,
                    detail="Token has been revoked"
                )
            
            payload = jwt.decode(
                token,
                settings.SECRET_KEY,
                algorithms=[settings.JWT_ALGORITHM]
            )
            return payload
            
        except jwt.ExpiredSignatureError:
            raise HTTPException(
                status_code=401,
                detail="Token has expired"
            )
        except jwt.JWTError as e:
            raise HTTPException(
                status_code=401,
                detail=f"Invalid token: {str(e)}"
            )
    
    def revoke_token(self, token: str) -> None:
        """
        Revoke/blacklist a token
        
        Args:
            token: Token to revoke
        """
        self._token_blacklist.add(token)
    
    @staticmethod
    def hash_api_key(api_key: str) -> str:
        """
        Hash API key for secure storage
        
        Args:
            api_key: API key to hash
            
        Returns:
            SHA-256 hash of the API key
        """
        return hashlib.sha256(api_key.encode()).hexdigest()
    
    @staticmethod
    def generate_api_key() -> str:
        """
        Generate a secure random API key
        
        Returns:
            Secure random API key
        """
        return f"sk_{secrets.token_urlsafe(32)}"
    
    def validate_api_key(self, api_key: str, stored_hash: str) -> bool:
        """
        Validate API key against stored hash
        
        Args:
            api_key: API key to validate
            stored_hash: Stored hash to compare against
            
        Returns:
            True if valid, False otherwise
        """
        return self.hash_api_key(api_key) == stored_hash


# Security scheme for FastAPI
security_scheme = HTTPBearer()


class AuthHandler:
    """
    Authentication handler for FastAPI dependencies
    """
    
    def __init__(self, security_manager: SecurityManager):
        self.security = security_manager
    
    async def verify_auth(
        self,
        credentials: HTTPAuthorizationCredentials = Depends(security_scheme)
    ) -> Dict:
        """
        Dependency for verifying JWT tokens in protected endpoints
        
        Args:
            credentials: Bearer token from request header
            
        Returns:
            Decoded token payload
            
        Usage in FastAPI:
            @app.get("/protected")
            async def protected_route(user=Depends(auth_handler.verify_auth)):
                return {"user": user}
        """
        token = credentials.credentials
        payload = self.security.verify_token(token)
        return payload
    
    async def get_current_user(
        self,
        credentials: HTTPAuthorizationCredentials = Depends(security_scheme)
    ) -> str:
        """
        Get current user from token
        
        Returns:
            User identifier from token
        """
        payload = await self.verify_auth(credentials)
        user_id = payload.get("sub")
        
        if not user_id:
            raise HTTPException(
                status_code=401,
                detail="Invalid user token"
            )
        
        return user_id


# Global security instance
try:
    _encryption_key = settings.get_encryption_key_bytes() if hasattr(settings, 'get_encryption_key_bytes') else settings.ENCRYPTION_KEY
    security_manager = SecurityManager(_encryption_key)
except Exception:
    # Fallback to generating a new key
    security_manager = SecurityManager(Fernet.generate_key())
    
auth_handler = AuthHandler(security_manager)

