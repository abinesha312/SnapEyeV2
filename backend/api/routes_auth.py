"""
Authentication Routes
Handles user authentication and token management
"""
import logging
from fastapi import APIRouter, HTTPException, Depends
from fastapi.responses import JSONResponse

import sys
import os
sys.path.insert(0, os.path.dirname(os.path.dirname(os.path.abspath(__file__))))

from models import LoginRequest, TokenResponse, ErrorResponse
from security import security_manager, auth_handler
from config import settings

# Configure logging
logger = logging.getLogger(__name__)

# Create router
router = APIRouter(
    prefix="/auth",
    tags=["Authentication"],
    responses={
        401: {"model": ErrorResponse, "description": "Unauthorized"},
        400: {"model": ErrorResponse, "description": "Bad Request"}
    }
)


@router.post(
    "/login",
    response_model=TokenResponse,
    summary="User Login",
    description="Authenticate user and receive access token"
)
async def login(request: LoginRequest):
    """
    Authenticate user and generate JWT tokens
    
    **Process:**
    1. Validate API key
    2. Generate access token
    3. Generate refresh token (optional)
    4. Return tokens with expiration info
    
    **Security:**
    - API keys are hashed before storage/comparison
    - Tokens are signed with HS256 algorithm
    - Short-lived access tokens for security
    """
    try:
        # Validate API key (in production, check against database)
        hashed_key = security_manager.hash_api_key(request.api_key)
        logger.info(f"Login attempt for user: {request.username}")
        
        # In production, validate against stored hash in database
        # For now, just validate format
        if len(request.api_key) < 10:
            raise HTTPException(
                status_code=401,
                detail="Invalid API key format"
            )
        
        # Generate access token
        token_data = {
            "sub": request.username,
            "api_key_hash": hashed_key[:16]  # Store partial hash for reference
        }
        access_token = security_manager.create_access_token(token_data)
        
        # Generate refresh token
        refresh_token = security_manager.create_refresh_token(token_data)
        
        logger.info(f"Login successful for user: {request.username}")
        
        return TokenResponse(
            access_token=access_token,
            token_type="bearer",
            expires_in=settings.ACCESS_TOKEN_EXPIRE_MINUTES * 60,
            refresh_token=refresh_token
        )
        
    except HTTPException:
        raise
    except Exception as e:
        logger.error(f"Login error: {str(e)}")
        raise HTTPException(
            status_code=500,
            detail=f"Authentication failed: {str(e)}"
        )


@router.post(
    "/refresh",
    response_model=TokenResponse,
    summary="Refresh Access Token",
    description="Get a new access token using refresh token"
)
async def refresh_token(user_data=Depends(auth_handler.verify_auth)):
    """
    Refresh access token using a valid refresh token
    
    **Use Case:**
    - Access token expired
    - Need to extend session
    - Maintain continuous authentication
    """
    try:
        # Generate new access token
        new_token = security_manager.create_access_token({
            "sub": user_data.get("sub"),
            "api_key_hash": user_data.get("api_key_hash")
        })
        
        logger.info(f"Token refreshed for user: {user_data.get('sub')}")
        
        return TokenResponse(
            access_token=new_token,
            token_type="bearer",
            expires_in=settings.ACCESS_TOKEN_EXPIRE_MINUTES * 60
        )
        
    except Exception as e:
        logger.error(f"Token refresh error: {str(e)}")
        raise HTTPException(
            status_code=500,
            detail=f"Token refresh failed: {str(e)}"
        )


@router.post(
    "/logout",
    summary="User Logout",
    description="Revoke current access token"
)
async def logout(user_data=Depends(auth_handler.verify_auth)):
    """
    Logout user and revoke token
    
    **Note:**
    In production, implement token blacklisting with Redis or database
    """
    try:
        # In production, add token to blacklist
        logger.info(f"Logout successful for user: {user_data.get('sub')}")
        
        return JSONResponse(
            content={
                "message": "Logout successful",
                "user": user_data.get("sub")
            }
        )
        
    except Exception as e:
        logger.error(f"Logout error: {str(e)}")
        raise HTTPException(
            status_code=500,
            detail=f"Logout failed: {str(e)}"
        )


@router.post(
    "/verify",
    summary="Verify Token",
    description="Verify if current token is valid"
)
async def verify_token(user_data=Depends(auth_handler.verify_auth)):
    """
    Verify token validity
    
    **Returns:**
    - User information if token is valid
    - 401 error if token is invalid or expired
    """
    return JSONResponse(
        content={
            "valid": True,
            "user": user_data.get("sub"),
            "expires_at": user_data.get("exp")
        }
    )

