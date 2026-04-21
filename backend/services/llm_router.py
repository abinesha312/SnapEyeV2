"""
Multi-Provider LLM Router for SnapEye
Supports OpenAI and Anthropic with automatic failover and unified streaming.
"""
import logging
from abc import ABC, abstractmethod
from typing import AsyncGenerator, Dict, List, Optional, Any

from config import settings

logger = logging.getLogger(__name__)


class LLMMessage:
    """Standardized message format for LLM providers."""

    def __init__(self, role: str, content: str):
        self.role = role
        self.content = content

    def to_openai(self) -> Dict:
        return {"role": self.role, "content": self.content}

    def to_anthropic(self) -> Dict:
        role = "user" if self.role == "user" else "assistant"
        return {"role": role, "content": self.content}


class BaseLLMProvider(ABC):
    """Abstract base class for LLM providers."""

    name: str = "base"

    @abstractmethod
    async def generate(
        self,
        messages: List[LLMMessage],
        stream: bool = False,
        max_tokens: int = 4096,
        temperature: float = 0.7,
        system_prompt: Optional[str] = None,
    ) -> str:
        """Generate a complete response."""
        pass

    @abstractmethod
    async def generate_stream(
        self,
        messages: List[LLMMessage],
        max_tokens: int = 4096,
        temperature: float = 0.7,
        system_prompt: Optional[str] = None,
    ) -> AsyncGenerator[str, None]:
        """Generate a streaming response, yielding tokens."""
        pass

    @abstractmethod
    async def generate_embeddings(self, texts: List[str]) -> List[List[float]]:
        """Generate embeddings for a list of texts."""
        pass

    def is_available(self) -> bool:
        """Check if the provider is configured and available."""
        return False


class OpenAIProvider(BaseLLMProvider):
    """OpenAI GPT provider."""

    name = "openai"

    def __init__(self):
        self.api_key = settings.OPENAI_API_KEY
        self.model = settings.OPENAI_MODEL
        self.embedding_model = settings.OPENAI_EMBEDDING_MODEL
        self._client = None

    def _get_client(self):
        if self._client is None:
            from openai import AsyncOpenAI
            self._client = AsyncOpenAI(api_key=self.api_key)
        return self._client

    def is_available(self) -> bool:
        return bool(self.api_key)

    async def generate(
        self,
        messages: List[LLMMessage],
        stream: bool = False,
        max_tokens: int = 4096,
        temperature: float = 0.7,
        system_prompt: Optional[str] = None,
    ) -> str:
        client = self._get_client()
        oai_messages = []
        if system_prompt:
            oai_messages.append({"role": "system", "content": system_prompt})
        oai_messages.extend([m.to_openai() for m in messages])

        response = await client.chat.completions.create(
            model=self.model,
            messages=oai_messages,
            max_tokens=max_tokens,
            temperature=temperature,
            stream=False,
        )
        return response.choices[0].message.content or ""

    async def generate_stream(
        self,
        messages: List[LLMMessage],
        max_tokens: int = 4096,
        temperature: float = 0.7,
        system_prompt: Optional[str] = None,
    ) -> AsyncGenerator[str, None]:
        client = self._get_client()
        oai_messages = []
        if system_prompt:
            oai_messages.append({"role": "system", "content": system_prompt})
        oai_messages.extend([m.to_openai() for m in messages])

        stream = await client.chat.completions.create(
            model=self.model,
            messages=oai_messages,
            max_tokens=max_tokens,
            temperature=temperature,
            stream=True,
        )

        async for chunk in stream:
            delta = chunk.choices[0].delta
            if delta.content:
                yield delta.content

    async def generate_embeddings(self, texts: List[str]) -> List[List[float]]:
        client = self._get_client()
        response = await client.embeddings.create(
            model=self.embedding_model,
            input=texts,
        )
        return [item.embedding for item in response.data]


class AnthropicProvider(BaseLLMProvider):
    """Anthropic Claude provider."""

    name = "anthropic"

    def __init__(self):
        self.api_key = settings.ANTHROPIC_API_KEY
        self.model = settings.ANTHROPIC_MODEL
        self._client = None

    def _get_client(self):
        if self._client is None:
            from anthropic import AsyncAnthropic
            self._client = AsyncAnthropic(api_key=self.api_key)
        return self._client

    def is_available(self) -> bool:
        return bool(self.api_key)

    async def generate(
        self,
        messages: List[LLMMessage],
        stream: bool = False,
        max_tokens: int = 4096,
        temperature: float = 0.7,
        system_prompt: Optional[str] = None,
    ) -> str:
        client = self._get_client()
        ant_messages = [m.to_anthropic() for m in messages]

        kwargs: Dict[str, Any] = {
            "model": self.model,
            "messages": ant_messages,
            "max_tokens": max_tokens,
            "temperature": temperature,
        }
        if system_prompt:
            kwargs["system"] = system_prompt

        response = await client.messages.create(**kwargs)
        return response.content[0].text if response.content else ""

    async def generate_stream(
        self,
        messages: List[LLMMessage],
        max_tokens: int = 4096,
        temperature: float = 0.7,
        system_prompt: Optional[str] = None,
    ) -> AsyncGenerator[str, None]:
        client = self._get_client()
        ant_messages = [m.to_anthropic() for m in messages]

        kwargs: Dict[str, Any] = {
            "model": self.model,
            "messages": ant_messages,
            "max_tokens": max_tokens,
            "temperature": temperature,
        }
        if system_prompt:
            kwargs["system"] = system_prompt

        async with client.messages.stream(**kwargs) as stream:
            async for text in stream.text_stream:
                yield text

    async def generate_embeddings(self, texts: List[str]) -> List[List[float]]:
        # Anthropic doesn't have an embeddings API; fall back to OpenAI
        if settings.OPENAI_API_KEY:
            openai_provider = OpenAIProvider()
            return await openai_provider.generate_embeddings(texts)
        raise NotImplementedError("Anthropic does not support embeddings; configure OpenAI API key for embeddings")


class AllProvidersFailedError(Exception):
    """Raised when all LLM providers have failed."""
    pass


class LLMRouter:
    """
    Routes LLM requests to the best available provider with automatic failover.

    Order: primary provider first, then fallback(s).
    On failure (API error, rate limit, timeout), automatically tries the next provider.
    """

    def __init__(self):
        self._providers: Dict[str, BaseLLMProvider] = {}
        self._provider_order: List[str] = []

        # Register providers
        openai_provider = OpenAIProvider()
        anthropic_provider = AnthropicProvider()

        if openai_provider.is_available():
            self._providers["openai"] = openai_provider
        if anthropic_provider.is_available():
            self._providers["anthropic"] = anthropic_provider

        # Set priority order from config
        primary = settings.LLM_PRIMARY_PROVIDER
        fallback = settings.LLM_FALLBACK_PROVIDER
        seen = set()
        for name in [primary, fallback]:
            if name in self._providers and name not in seen:
                self._provider_order.append(name)
                seen.add(name)
        # Add any remaining registered providers
        for name in self._providers:
            if name not in seen:
                self._provider_order.append(name)

        available = [f"{n} ({'primary' if i == 0 else 'fallback'})"
                     for i, n in enumerate(self._provider_order)]
        logger.info(f"LLM Router initialized: {', '.join(available) or 'no providers available'}")

    def get_provider(self, name: Optional[str] = None) -> BaseLLMProvider:
        """Get a specific provider by name, or the primary provider."""
        if name and name in self._providers:
            return self._providers[name]
        if self._provider_order:
            return self._providers[self._provider_order[0]]
        raise AllProvidersFailedError("No LLM providers are configured")

    async def generate(
        self,
        messages: List[LLMMessage],
        max_tokens: int = 0,
        temperature: float = 0.0,
        system_prompt: Optional[str] = None,
        provider: Optional[str] = None,
    ) -> str:
        """Generate a complete response with automatic failover."""
        max_tokens = max_tokens or settings.LLM_MAX_TOKENS
        temperature = temperature or settings.LLM_TEMPERATURE

        # If a specific provider is requested, try it first
        order = list(self._provider_order)
        if provider and provider in self._providers:
            order = [provider] + [p for p in order if p != provider]

        errors = []
        for name in order:
            prov = self._providers[name]
            try:
                logger.debug(f"Trying LLM provider: {name}")
                result = await prov.generate(
                    messages, max_tokens=max_tokens,
                    temperature=temperature, system_prompt=system_prompt,
                )
                logger.info(f"LLM response from {name}: {len(result)} chars")
                return result
            except Exception as e:
                logger.warning(f"LLM provider {name} failed: {e}")
                errors.append(f"{name}: {e}")

        raise AllProvidersFailedError(f"All providers failed: {'; '.join(errors)}")

    async def generate_stream(
        self,
        messages: List[LLMMessage],
        max_tokens: int = 0,
        temperature: float = 0.0,
        system_prompt: Optional[str] = None,
        provider: Optional[str] = None,
    ) -> AsyncGenerator[str, None]:
        """Generate a streaming response with automatic failover."""
        max_tokens = max_tokens or settings.LLM_MAX_TOKENS
        temperature = temperature or settings.LLM_TEMPERATURE

        order = list(self._provider_order)
        if provider and provider in self._providers:
            order = [provider] + [p for p in order if p != provider]

        errors = []
        for name in order:
            prov = self._providers[name]
            try:
                logger.debug(f"Trying streaming LLM provider: {name}")
                async for token in prov.generate_stream(
                    messages, max_tokens=max_tokens,
                    temperature=temperature, system_prompt=system_prompt,
                ):
                    yield token
                return  # success
            except Exception as e:
                logger.warning(f"LLM streaming provider {name} failed: {e}")
                errors.append(f"{name}: {e}")

        raise AllProvidersFailedError(f"All streaming providers failed: {'; '.join(errors)}")

    async def generate_embeddings(self, texts: List[str]) -> List[List[float]]:
        """Generate embeddings (uses OpenAI preferentially)."""
        for name in self._provider_order:
            prov = self._providers[name]
            try:
                return await prov.generate_embeddings(texts)
            except NotImplementedError:
                continue
            except Exception as e:
                logger.warning(f"Embeddings from {name} failed: {e}")
        raise AllProvidersFailedError("No provider could generate embeddings")

    @property
    def available_providers(self) -> List[str]:
        return list(self._provider_order)


# Global router instance
llm_router = LLMRouter()
