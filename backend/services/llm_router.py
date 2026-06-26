"""
Multi-Provider LLM Router for SnapEye
Supports OpenAI, Anthropic, Google Gemini, and xAI (Grok) with optional automatic
failover and unified streaming. Every generation method accepts per-request
``api_key`` and ``model`` overrides so the desktop client's user-chosen credentials
take precedence over the server's environment-variable defaults.
"""
import asyncio
import logging
import time
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
        api_key_override: Optional[str] = None,
        model_override: Optional[str] = None,
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
        api_key_override: Optional[str] = None,
        model_override: Optional[str] = None,
    ) -> AsyncGenerator[str, None]:
        """Generate a streaming response, yielding tokens."""
        pass

    @abstractmethod
    async def generate_embeddings(self, texts: List[str]) -> List[List[float]]:
        """Generate embeddings for a list of texts."""
        pass

    def is_available(self) -> bool:
        """Check if the provider has a server-side default key configured."""
        return False


class OpenAIProvider(BaseLLMProvider):
    """OpenAI GPT provider."""

    name = "openai"

    def __init__(self):
        self.api_key = settings.OPENAI_API_KEY
        self.model = settings.OPENAI_MODEL
        self.embedding_model = settings.OPENAI_EMBEDDING_MODEL
        self._client = None
        self._override_clients: Dict[str, Any] = {}

    def _build_client(self, api_key_override: Optional[str] = None):
        from openai import AsyncOpenAI
        if api_key_override:
            # Cache per-key clients so each request reuses the HTTP connection pool
            # instead of paying a fresh TLS handshake (lower time-to-first-token).
            client = self._override_clients.get(api_key_override)
            if client is None:
                client = AsyncOpenAI(api_key=api_key_override)
                self._override_clients[api_key_override] = client
            return client
        if self._client is None:
            self._client = AsyncOpenAI(api_key=self.api_key)
        return self._client

    def is_available(self) -> bool:
        return bool(self.api_key)

    @staticmethod
    def _is_reasoning_model(model: str) -> bool:
        """
        GPT-5, o1, o3, o4 reasoning models no longer accept `max_tokens` or a non-default
        `temperature`; they use `max_completion_tokens` and only support temperature=1.
        """
        if not model:
            return False
        lower = model.lower()
        return (
            lower.startswith("gpt-5")
            or lower.startswith("o1")
            or lower.startswith("o3")
            or lower.startswith("o4")
        )

    def _build_chat_kwargs(
        self,
        model: str,
        messages: List[Dict],
        max_tokens: int,
        temperature: float,
        stream: bool,
    ) -> Dict[str, Any]:
        kwargs: Dict[str, Any] = {
            "model": model,
            "messages": messages,
            "stream": stream,
        }
        if self._is_reasoning_model(model):
            # gpt-5 / o-series only accept `max_completion_tokens` (not `max_tokens`) and
            # reject non-default `temperature`. We pass the new param via `extra_body` so it
            # works even on older openai SDKs (<1.35) that don't yet know the keyword.
            kwargs["extra_body"] = {"max_completion_tokens": max_tokens}
        else:
            kwargs["max_tokens"] = max_tokens
            kwargs["temperature"] = temperature
        return kwargs

    async def generate(
        self,
        messages: List[LLMMessage],
        stream: bool = False,
        max_tokens: int = 4096,
        temperature: float = 0.7,
        system_prompt: Optional[str] = None,
        api_key_override: Optional[str] = None,
        model_override: Optional[str] = None,
    ) -> str:
        client = self._build_client(api_key_override)
        model = model_override or self.model
        oai_messages: List[Dict[str, Any]] = []
        if system_prompt:
            oai_messages.append({"role": "system", "content": system_prompt})
        oai_messages.extend([m.to_openai() for m in messages])

        kwargs = self._build_chat_kwargs(model, oai_messages, max_tokens, temperature, stream=False)
        response = await client.chat.completions.create(**kwargs)
        return response.choices[0].message.content or ""

    async def generate_stream(
        self,
        messages: List[LLMMessage],
        max_tokens: int = 4096,
        temperature: float = 0.7,
        system_prompt: Optional[str] = None,
        api_key_override: Optional[str] = None,
        model_override: Optional[str] = None,
    ) -> AsyncGenerator[str, None]:
        client = self._build_client(api_key_override)
        model = model_override or self.model
        oai_messages: List[Dict[str, Any]] = []
        if system_prompt:
            oai_messages.append({"role": "system", "content": system_prompt})
        oai_messages.extend([m.to_openai() for m in messages])

        kwargs = self._build_chat_kwargs(model, oai_messages, max_tokens, temperature, stream=True)
        stream = await client.chat.completions.create(**kwargs)

        async for chunk in stream:
            delta = chunk.choices[0].delta
            if delta.content:
                yield delta.content

    async def generate_embeddings(self, texts: List[str]) -> List[List[float]]:
        client = self._build_client()
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
        self._override_clients: Dict[str, Any] = {}

    def _build_client(self, api_key_override: Optional[str] = None):
        from anthropic import AsyncAnthropic
        if api_key_override:
            # Reuse cached clients per key to keep the HTTP connection pool warm.
            client = self._override_clients.get(api_key_override)
            if client is None:
                client = AsyncAnthropic(api_key=api_key_override)
                self._override_clients[api_key_override] = client
            return client
        if self._client is None:
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
        api_key_override: Optional[str] = None,
        model_override: Optional[str] = None,
    ) -> str:
        client = self._build_client(api_key_override)
        model = model_override or self.model
        ant_messages = [m.to_anthropic() for m in messages]

        kwargs: Dict[str, Any] = {
            "model": model,
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
        api_key_override: Optional[str] = None,
        model_override: Optional[str] = None,
    ) -> AsyncGenerator[str, None]:
        client = self._build_client(api_key_override)
        model = model_override or self.model
        ant_messages = [m.to_anthropic() for m in messages]

        kwargs: Dict[str, Any] = {
            "model": model,
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


class GeminiProvider(BaseLLMProvider):
    """Google Gemini provider (uses google-generativeai SDK)."""

    name = "gemini"

    def __init__(self):
        self.api_key = settings.GEMINI_API_KEY
        self.model = settings.GEMINI_MODEL

    def is_available(self) -> bool:
        return bool(self.api_key)

    def _configure(self, api_key_override: Optional[str] = None):
        """Configure the global genai client with the chosen api key."""
        try:
            import google.generativeai as genai
        except ImportError as e:
            raise RuntimeError(
                "google-generativeai is not installed. Run `pip install google-generativeai`."
            ) from e
        genai.configure(api_key=api_key_override or self.api_key)
        return genai

    def _map_messages(self, messages: List[LLMMessage]) -> List[Dict[str, Any]]:
        """Gemini's `contents` schema uses role in {user, model} and parts[].text."""
        out = []
        for m in messages:
            role = "user" if m.role == "user" else "model"
            out.append({"role": role, "parts": [{"text": m.content}]})
        return out

    async def generate(
        self,
        messages: List[LLMMessage],
        stream: bool = False,
        max_tokens: int = 4096,
        temperature: float = 0.7,
        system_prompt: Optional[str] = None,
        api_key_override: Optional[str] = None,
        model_override: Optional[str] = None,
    ) -> str:
        genai = self._configure(api_key_override)
        model_name = model_override or self.model

        # google-generativeai exposes a sync API; run it in a thread so we don't block
        # the FastAPI event loop.
        def _call() -> str:
            model = genai.GenerativeModel(
                model_name=model_name,
                system_instruction=system_prompt,
            )
            resp = model.generate_content(
                self._map_messages(messages),
                generation_config={
                    "max_output_tokens": max_tokens,
                    "temperature": temperature,
                },
            )
            return getattr(resp, "text", "") or ""

        return await asyncio.to_thread(_call)

    async def generate_stream(
        self,
        messages: List[LLMMessage],
        max_tokens: int = 4096,
        temperature: float = 0.7,
        system_prompt: Optional[str] = None,
        api_key_override: Optional[str] = None,
        model_override: Optional[str] = None,
    ) -> AsyncGenerator[str, None]:
        genai = self._configure(api_key_override)
        model_name = model_override or self.model

        # Gemini's stream iterator is a sync generator; bridge it to async by pumping
        # chunks through a queue from a background thread.
        queue: asyncio.Queue = asyncio.Queue()
        sentinel = object()

        def _pump():
            try:
                model = genai.GenerativeModel(
                    model_name=model_name,
                    system_instruction=system_prompt,
                )
                stream = model.generate_content(
                    self._map_messages(messages),
                    generation_config={
                        "max_output_tokens": max_tokens,
                        "temperature": temperature,
                    },
                    stream=True,
                )
                for chunk in stream:
                    text = getattr(chunk, "text", None)
                    if text:
                        queue.put_nowait(text)
            except Exception as exc:  # surface errors to the async side
                queue.put_nowait(exc)
            finally:
                queue.put_nowait(sentinel)

        loop = asyncio.get_event_loop()
        fut = loop.run_in_executor(None, _pump)

        try:
            while True:
                item = await queue.get()
                if item is sentinel:
                    break
                if isinstance(item, Exception):
                    raise item
                yield item
        finally:
            await fut

    async def generate_embeddings(self, texts: List[str]) -> List[List[float]]:
        # Prefer the user's configured embeddings provider (OpenAI) since Gemini
        # embeddings use a different model family that we don't currently expose.
        if settings.OPENAI_API_KEY:
            return await OpenAIProvider().generate_embeddings(texts)
        raise NotImplementedError("Gemini embeddings not wired in; configure OPENAI_API_KEY")


class GrokProvider(BaseLLMProvider):
    """xAI Grok provider (OpenAI-compatible chat completions at api.x.ai)."""

    name = "grok"

    def __init__(self):
        self.api_key = settings.XAI_API_KEY
        self.model = settings.XAI_MODEL
        self.base_url = settings.XAI_BASE_URL
        self._client = None
        self._override_clients: Dict[str, Any] = {}

    def _build_client(self, api_key_override: Optional[str] = None):
        from openai import AsyncOpenAI
        if api_key_override:
            # Reuse cached clients per key to keep the HTTP connection pool warm.
            client = self._override_clients.get(api_key_override)
            if client is None:
                client = AsyncOpenAI(api_key=api_key_override, base_url=self.base_url)
                self._override_clients[api_key_override] = client
            return client
        if self._client is None:
            self._client = AsyncOpenAI(api_key=self.api_key, base_url=self.base_url)
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
        api_key_override: Optional[str] = None,
        model_override: Optional[str] = None,
    ) -> str:
        client = self._build_client(api_key_override)
        model = model_override or self.model
        oai_messages = []
        if system_prompt:
            oai_messages.append({"role": "system", "content": system_prompt})
        oai_messages.extend([m.to_openai() for m in messages])

        response = await client.chat.completions.create(
            model=model,
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
        api_key_override: Optional[str] = None,
        model_override: Optional[str] = None,
    ) -> AsyncGenerator[str, None]:
        client = self._build_client(api_key_override)
        model = model_override or self.model
        oai_messages = []
        if system_prompt:
            oai_messages.append({"role": "system", "content": system_prompt})
        oai_messages.extend([m.to_openai() for m in messages])

        stream = await client.chat.completions.create(
            model=model,
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
        # xAI doesn't expose a public embeddings endpoint yet.
        if settings.OPENAI_API_KEY:
            return await OpenAIProvider().generate_embeddings(texts)
        raise NotImplementedError("Grok does not offer embeddings; configure OPENAI_API_KEY")


class AllProvidersFailedError(Exception):
    """Raised when all LLM providers have failed."""
    pass


class NoProviderConfiguredError(Exception):
    """Raised when no LLM provider is available (no env key and no per-request override)."""
    pass


# Registry of every provider class we know about. Used for per-request overrides where
# the caller passes their own api_key and we build a fresh provider instance on the fly.
_PROVIDER_REGISTRY = {
    "openai": OpenAIProvider,
    "anthropic": AnthropicProvider,
    "gemini": GeminiProvider,
    "grok": GrokProvider,
}


class LLMRouter:
    """
    Routes LLM requests to the best available provider with automatic failover.

    Order: primary provider first, then fallback(s).
    On failure (API error, rate limit, timeout), automatically tries the next provider.

    When the caller passes ``provider`` + ``api_key`` explicitly (i.e. the desktop client
    has a user-chosen provider+key), failover is disabled: we call only that provider so
    we never silently leak the user's intent to a different backend.
    """

    def __init__(self):
        self._providers: Dict[str, BaseLLMProvider] = {}
        self._provider_order: List[str] = []
        # Providers built for per-request key overrides; cached so their internal
        # HTTP clients (and connection pools) survive across requests.
        self._override_providers: Dict[str, BaseLLMProvider] = {}

        # Register providers based on server-side env configuration. Per-request overrides
        # can still use providers whose env key is missing; see _build_provider_on_demand.
        for name, cls in _PROVIDER_REGISTRY.items():
            try:
                inst = cls()
                if inst.is_available():
                    self._providers[name] = inst
            except Exception as exc:  # pragma: no cover - defensive
                logger.warning(f"Failed to initialize provider {name}: {exc}")

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

    def _build_provider_on_demand(self, provider_name: str) -> BaseLLMProvider:
        """Get/construct a provider instance for per-request overrides (key supplied by caller)."""
        cls = _PROVIDER_REGISTRY.get(provider_name)
        if cls is None:
            raise NoProviderConfiguredError(f"Unknown provider: {provider_name}")
        inst = self._override_providers.get(provider_name)
        if inst is None:
            inst = cls()
            self._override_providers[provider_name] = inst
        return inst

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
        api_key: Optional[str] = None,
        model: Optional[str] = None,
    ) -> str:
        """Generate a complete response. Honors per-request provider/api_key/model override."""
        max_tokens = max_tokens or settings.LLM_MAX_TOKENS
        temperature = temperature or settings.LLM_TEMPERATURE

        # Per-request override path — no failover.
        if provider and api_key:
            prov = self._build_provider_on_demand(provider)
            return await prov.generate(
                messages,
                max_tokens=max_tokens,
                temperature=temperature,
                system_prompt=system_prompt,
                api_key_override=api_key,
                model_override=model,
            )

        if not self._provider_order:
            raise NoProviderConfiguredError(
                "No AI provider is configured. Open Dashboard > AI Models to set one up."
            )

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
                    model_override=model,
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
        api_key: Optional[str] = None,
        model: Optional[str] = None,
    ) -> AsyncGenerator[str, None]:
        """Generate a streaming response. Honors per-request provider/api_key/model override."""
        max_tokens = max_tokens or settings.LLM_MAX_TOKENS
        temperature = temperature or settings.LLM_TEMPERATURE

        # Per-request override path — user explicitly chose this provider, so no failover.
        if provider and api_key:
            prov = self._build_provider_on_demand(provider)
            async for token in prov.generate_stream(
                messages,
                max_tokens=max_tokens,
                temperature=temperature,
                system_prompt=system_prompt,
                api_key_override=api_key,
                model_override=model,
            ):
                yield token
            return

        if not self._provider_order:
            raise NoProviderConfiguredError(
                "No AI provider is configured. Open Dashboard > AI Models to set one up."
            )

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
                    model_override=model,
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

    async def test_connection(
        self,
        provider: str,
        model: str,
        api_key: str,
    ) -> Dict[str, Any]:
        """
        Fire a tiny ping prompt at the given provider/model with the supplied key and
        return ``{ok, latency_ms, error}``. Never raises to the caller.
        """
        if not provider or provider not in _PROVIDER_REGISTRY:
            return {"ok": False, "error": f"Unknown provider: {provider}", "latency_ms": 0}
        if not api_key:
            return {"ok": False, "error": "API key is required", "latency_ms": 0}

        prov = self._build_provider_on_demand(provider)
        messages = [LLMMessage(role="user", content="ping")]

        # Reasoning models (gpt-5, o1/o3/o4) burn output tokens on their internal reasoning
        # before emitting visible text, so a ping with max_tokens=8 returns empty. Give them
        # more headroom and keep temperature at its default 1.0 (they reject 0.0).
        is_reasoning_openai = (
            provider == "openai"
            and OpenAIProvider._is_reasoning_model(model)
        )
        max_tokens = 256 if is_reasoning_openai else 16
        temperature = 1.0 if is_reasoning_openai else 0.0

        start = time.perf_counter()
        try:
            text = await prov.generate(
                messages,
                max_tokens=max_tokens,
                temperature=temperature,
                system_prompt="Reply with a single word.",
                api_key_override=api_key,
                model_override=model,
            )
            latency = int((time.perf_counter() - start) * 1000)
            return {"ok": True, "latency_ms": latency, "sample": (text or "")[:80]}
        except Exception as exc:
            latency = int((time.perf_counter() - start) * 1000)
            return {"ok": False, "error": str(exc), "latency_ms": latency}

    @property
    def available_providers(self) -> List[str]:
        return list(self._provider_order)


# Global router instance
llm_router = LLMRouter()
