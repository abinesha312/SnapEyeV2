"""
OpenAI Integration Service
Handles all interactions with OpenAI API
"""
import base64
import logging
from datetime import datetime
from typing import Dict, Optional, List
from abc import ABC, abstractmethod

import openai
from openai import OpenAI

import sys
import os
sys.path.insert(0, os.path.dirname(os.path.dirname(os.path.abspath(__file__))))

from config import settings, model_config
from security import SecurityManager
from services.prompts import PromptTemplates
from models import SearchType

# Configure logging
logger = logging.getLogger(__name__)


class BaseAIService(ABC):
    """
    Abstract base class for AI services
    
    Provides common functionality for all AI service implementations
    """
    
    def __init__(self, api_key: str, security_manager: SecurityManager):
        """
        Initialize AI service
        
        Args:
            api_key: OpenAI API key
            security_manager: Security manager instance
        """
        self.api_key = api_key
        self.security = security_manager
        self.client = OpenAI(api_key=api_key)
        self.model = settings.OPENAI_MODEL
        logger.info(f"Initialized {self.__class__.__name__}")
    
    @abstractmethod
    async def process(self, *args, **kwargs) -> Dict:
        """Process request - to be implemented by subclasses"""
        pass
    
    def _create_response(
        self,
        success: bool,
        result: Optional[str] = None,
        error: Optional[str] = None,
        search_type: SearchType = SearchType.TEXT,
        metadata: Optional[Dict] = None,
        encrypt: bool = False
    ) -> Dict:
        """
        Create standardized response
        
        Args:
            success: Whether request was successful
            result: Result data
            error: Error message if failed
            search_type: Type of search performed
            metadata: Additional metadata
            encrypt: Whether to encrypt the result
            
        Returns:
            Standardized response dictionary
        """
        response = {
            "success": success,
            "timestamp": datetime.utcnow().isoformat(),
            "model": self.model,
            "search_type": search_type.value
        }
        
        if success and result:
            # Always provide plain result
            response["result"] = result
            
            # Optionally provide encrypted result
            if encrypt:
                response["encrypted_result"] = self.security.encrypt_data(result)
                
            if metadata:
                response["metadata"] = metadata
        elif error:
            response["error"] = error
            logger.error(f"Request failed: {error}")
        
        return response


class ImageSearchService(BaseAIService):
    """
    Image analysis and search service
    
    Handles image-based queries using GPT-4 Vision
    """
    
    async def process(
        self,
        image_data: bytes,
        query: str,
        temperature: float = model_config.TEMPERATURE_DEFAULT,
        include_objects: bool = True,
        include_text: bool = True
    ) -> Dict:
        """
        Process image search request
        
        Args:
            image_data: Raw image bytes
            query: User's question about the image
            temperature: Response creativity (0-2)
            include_objects: Whether to detect objects
            include_text: Whether to extract text
            
        Returns:
            Search response dictionary
        """
        try:
            # Validate image size
            image_size_mb = len(image_data) / (1024 * 1024)
            if image_size_mb > settings.MAX_FILE_SIZE_MB:
                return self._create_response(
                    success=False,
                    error=f"Image too large: {image_size_mb:.2f}MB (max {settings.MAX_FILE_SIZE_MB}MB)",
                    search_type=SearchType.IMAGE
                )
            
            # Encode image
            base64_image = base64.b64encode(image_data).decode('utf-8')
            
            # Prepare prompt
            prompt = PromptTemplates.get_prompt("IMAGE_ANALYSIS", query=query)
            
            # Call OpenAI API
            logger.info(f"Processing image search: {query[:50]}...")
            response = self.client.chat.completions.create(
                model=self.model,
                messages=[
                    {
                        "role": "system",
                        "content": "You are a professional image analysis assistant with expertise in computer vision."
                    },
                    {
                        "role": "user",
                        "content": [
                            {"type": "text", "text": prompt},
                            {
                                "type": "image_url",
                                "image_url": {
                                    "url": f"data:image/jpeg;base64,{base64_image}",
                                    "detail": "high"
                                }
                            }
                        ]
                    }
                ],
                temperature=temperature,
                max_tokens=model_config.MAX_TOKENS_DEFAULT
            )
            
            result = response.choices[0].message.content
            
            # Extract metadata
            metadata = {
                "image_size_mb": round(image_size_mb, 2),
                "query": query,
                "temperature": temperature,
                "tokens_used": response.usage.total_tokens,
                "finish_reason": response.choices[0].finish_reason
            }
            
            logger.info(f"Image search successful. Tokens used: {metadata['tokens_used']}")
            
            return self._create_response(
                success=True,
                result=result,
                search_type=SearchType.IMAGE,
                metadata=metadata
            )
            
        except openai.APIError as e:
            logger.error(f"OpenAI API error: {str(e)}")
            return self._create_response(
                success=False,
                error=f"OpenAI API error: {str(e)}",
                search_type=SearchType.IMAGE
            )
        except Exception as e:
            logger.exception(f"Unexpected error in image search: {str(e)}")
            return self._create_response(
                success=False,
                error=f"Internal error: {str(e)}",
                search_type=SearchType.IMAGE
            )


class TextSearchService(BaseAIService):
    """
    Text-based search and query service
    
    Handles text queries using GPT-4
    """
    
    async def process(
        self,
        query: str,
        temperature: float = model_config.TEMPERATURE_DEFAULT,
        max_tokens: Optional[int] = None
    ) -> Dict:
        """
        Process text search request
        
        Args:
            query: User's text query
            temperature: Response creativity (0-2)
            max_tokens: Maximum tokens in response
            
        Returns:
            Search response dictionary
        """
        try:
            # Prepare prompt
            prompt = PromptTemplates.get_prompt("TEXT_SEARCH", query=query)
            
            # Call OpenAI API
            logger.info(f"Processing text search: {query[:50]}...")
            response = self.client.chat.completions.create(
                model=self.model,
                messages=[
                    {
                        "role": "system",
                        "content": "You are a knowledgeable AI assistant providing accurate and helpful information."
                    },
                    {
                        "role": "user",
                        "content": prompt
                    }
                ],
                temperature=temperature,
                max_tokens=max_tokens or model_config.MAX_TOKENS_DEFAULT
            )
            
            result = response.choices[0].message.content
            
            # Extract metadata
            metadata = {
                "query": query,
                "temperature": temperature,
                "tokens_used": response.usage.total_tokens,
                "finish_reason": response.choices[0].finish_reason
            }
            
            logger.info(f"Text search successful. Tokens used: {metadata['tokens_used']}")
            
            return self._create_response(
                success=True,
                result=result,
                search_type=SearchType.TEXT,
                metadata=metadata
            )
            
        except openai.APIError as e:
            logger.error(f"OpenAI API error: {str(e)}")
            return self._create_response(
                success=False,
                error=f"OpenAI API error: {str(e)}",
                search_type=SearchType.TEXT
            )
        except Exception as e:
            logger.exception(f"Unexpected error in text search: {str(e)}")
            return self._create_response(
                success=False,
                error=f"Internal error: {str(e)}",
                search_type=SearchType.TEXT
            )


class OpenAIServiceFactory:
    """
    Factory class for creating AI services
    
    Implements the Factory design pattern for service creation
    """
    
    @staticmethod
    def create_image_service(
        api_key: Optional[str] = None,
        security_manager: Optional[SecurityManager] = None
    ) -> ImageSearchService:
        """Create image search service instance"""
        from security import security_manager as default_security
        
        return ImageSearchService(
            api_key=api_key or settings.OPENAI_API_KEY,
            security_manager=security_manager or default_security
        )
    
    @staticmethod
    def create_text_service(
        api_key: Optional[str] = None,
        security_manager: Optional[SecurityManager] = None
    ) -> TextSearchService:
        """Create text search service instance"""
        from security import security_manager as default_security
        
        return TextSearchService(
            api_key=api_key or settings.OPENAI_API_KEY,
            security_manager=security_manager or default_security
        )


# Global service instances
image_service = OpenAIServiceFactory.create_image_service()
text_service = OpenAIServiceFactory.create_text_service()

