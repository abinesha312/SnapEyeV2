"""
SnapEye AI - Main Application
FastAPI application entry point
"""
import asyncio
import logging
from datetime import datetime
from contextlib import asynccontextmanager

from fastapi import FastAPI, Request
from fastapi.middleware.cors import CORSMiddleware
from fastapi.middleware.trustedhost import TrustedHostMiddleware
from fastapi.responses import JSONResponse
from fastapi.exceptions import RequestValidationError
from starlette.exceptions import HTTPException as StarletteHTTPException

from config import settings
from api import routes_auth, routes_search, routes_realtime, routes_deepgram_live
from api import routes_llm, routes_rag, routes_ocr, routes_profile, routes_conversations
from models import HealthResponse, ErrorResponse

# Configure logging
handlers = [logging.StreamHandler()]

# Add file handler if LOG_FILE is specified
if settings.LOG_FILE:
    import os
    log_dir = os.path.dirname(settings.LOG_FILE)
    if log_dir and not os.path.exists(log_dir):
        os.makedirs(log_dir, exist_ok=True)
    handlers.append(logging.FileHandler(settings.LOG_FILE))

logging.basicConfig(
    level=getattr(logging, settings.LOG_LEVEL),
    format='%(asctime)s - %(name)s - %(levelname)s - %(message)s',
    handlers=handlers
)

logger = logging.getLogger(__name__)


# Application lifespan
@asynccontextmanager
async def lifespan(app: FastAPI):
    """
    Application lifespan context manager
    
    Handles startup and shutdown events
    """
    # Startup
    logger.info(f"Starting {settings.APP_NAME} v{settings.APP_VERSION}")
    logger.info(f"Environment: {'Development' if settings.DEBUG else 'Production'}")
    logger.info(f"OpenAI Model: {settings.OPENAI_MODEL}")
    
    # Ensure data directories exist
    import os
    os.makedirs("./data", exist_ok=True)
    os.makedirs("./logs", exist_ok=True)
    
    # Initialize storage
    try:
        from services.storage_service import storage_service
        await storage_service.initialize()
        logger.info("SQLite storage initialized")
    except Exception as e:
        logger.warning(f"Storage initialization skipped: {e}")
    
    # Log available LLM providers
    try:
        from services.llm_router import llm_router
        logger.info(f"LLM providers: {llm_router.available_providers}")
    except Exception as e:
        logger.warning(f"LLM router status: {e}")

    # Warm the primary LLM provider in the background so /health can start serving
    # immediately (this must never block startup), while the first *real* user query
    # doesn't pay the SDK's own one-time lazy-init cost (observed ~5s: SDK import,
    # client construction, and internal setup that only happens on the first actual
    # call). A single 1-token completion is the only reliable way to warm all of
    # that - constructing the client alone left ~3s of latency on the first real call.
    async def _warm_primary_provider():
        try:
            from services.llm_router import llm_router, LLMMessage
            provider_name = llm_router.available_providers[0] if llm_router.available_providers else None
            if not provider_name:
                return
            await llm_router.generate(
                [LLMMessage(role="user", content="Hi")],
                max_tokens=1,
                provider=None,
            )
            logger.info(f"Warmed LLM provider: {provider_name}")
        except Exception as e:
            logger.debug(f"LLM provider warm-up skipped: {e}")

    # Same idea for RAG: the first real query with use_profile=true triggers
    # rag_service's lazy chromadb import + collection setup inside the request's
    # 0.6s retrieval budget (see routes_llm.py), which can eat into that budget
    # unnecessarily. Doing it once here means the first real query gets a fully
    # warm vector store instead of racing a cold import against a timeout.
    async def _warm_rag_service():
        try:
            from services.rag_service import rag_service
            rag_service._ensure_initialized()
            logger.info("Warmed RAG service (ChromaDB)")
        except Exception as e:
            logger.debug(f"RAG service warm-up skipped: {e}")

    asyncio.create_task(_warm_rag_service())

    asyncio.create_task(_warm_primary_provider())

    yield
    
    # Shutdown
    logger.info(f"Shutting down {settings.APP_NAME}")


# Create FastAPI application
app = FastAPI(
    title=settings.APP_NAME,
    description="""
    # SnapEye AI Backend API
    
    Enterprise-grade AI-powered image analysis, text search, and real-time transcription service.
    
    ## Features
    
    ###Image Search
    - Advanced image analysis using GPT-4 Vision
    - Object detection and identification
    - Text extraction (OCR)
    - Scene understanding
    
    ### Text Search
    - Natural language question answering
    - Information retrieval
    - Contextual explanations
    
    ### Real-time Transcription
    - Live speech-to-text
    - Conversational AI
    - Multiple voice options
    - Automatic turn detection
    
    ### Security
    - JWT authentication
    - End-to-end encryption
    - API key management
    - Rate limiting
    
    ## Getting Started
    
    1. **Authenticate**: POST `/auth/login` with your credentials
    2. **Get Token**: Receive JWT access token
    3. **Use API**: Include token in `Authorization: Bearer <token>` header
    4. **Search**: Use `/api/search/image` or `/api/search/text` endpoints
    5. **Transcribe**: Connect to WebSocket at `/api/transcribe/live`
    
    ## Rate Limits
    - 60 requests per minute
    - 1000 requests per hour
    
    ## Support
    - Documentation: Full API docs available at `/docs`
    - Contact: support@snapeye.ai
    """,
    version=settings.APP_VERSION,
    lifespan=lifespan,
    docs_url="/docs",
    redoc_url="/redoc",
    openapi_url="/openapi.json"
)


# ==================== Middleware ====================

# CORS Middleware
app.add_middleware(
    CORSMiddleware,
    allow_origins=settings.CORS_ORIGINS,
    allow_credentials=True,
    allow_methods=["*"],
    allow_headers=["*"],
)

# Trusted Host Middleware (Production)
if not settings.DEBUG:
    app.add_middleware(
        TrustedHostMiddleware,
        allowed_hosts=["localhost", "127.0.0.1", "*.snapeye.ai"]
    )


# Request ID Middleware
@app.middleware("http")
async def add_request_id(request: Request, call_next):
    """Add unique request ID to each request"""
    import uuid
    request_id = str(uuid.uuid4())
    request.state.request_id = request_id
    
    response = await call_next(request)
    response.headers["X-Request-ID"] = request_id
    
    return response


# Logging Middleware
@app.middleware("http")
async def log_requests(request: Request, call_next):
    """Log all requests"""
    start_time = datetime.utcnow()
    
    response = await call_next(request)
    
    duration = (datetime.utcnow() - start_time).total_seconds()
    logger.info(
        f"{request.method} {request.url.path} - "
        f"Status: {response.status_code} - "
        f"Duration: {duration:.3f}s - "
        f"Request ID: {getattr(request.state, 'request_id', 'N/A')}"
    )
    
    return response


# ==================== Error Handlers ====================

@app.exception_handler(StarletteHTTPException)
async def http_exception_handler(request: Request, exc: StarletteHTTPException):
    """Handle HTTP exceptions"""
    return JSONResponse(
        status_code=exc.status_code,
        content=ErrorResponse(
            error=exc.detail,
            error_code=f"HTTP_{exc.status_code}"
        ).dict()
    )


@app.exception_handler(RequestValidationError)
async def validation_exception_handler(request: Request, exc: RequestValidationError):
    """Handle validation errors"""
    return JSONResponse(
        status_code=422,
        content=ErrorResponse(
            error="Validation error",
            error_code="VALIDATION_ERROR",
            timestamp=datetime.utcnow().isoformat()
        ).dict()
    )


@app.exception_handler(Exception)
async def general_exception_handler(request: Request, exc: Exception):
    """Handle all other exceptions"""
    logger.exception(f"Unhandled exception: {str(exc)}")
    
    return JSONResponse(
        status_code=500,
        content=ErrorResponse(
            error="Internal server error",
            error_code="INTERNAL_ERROR"
        ).dict()
    )


# ==================== Routes ====================

# Include routers
app.include_router(routes_auth.router)
app.include_router(routes_search.router)
app.include_router(routes_realtime.router)
app.include_router(routes_deepgram_live.router, prefix="/deepgram")
app.include_router(routes_llm.router)
app.include_router(routes_rag.router)
app.include_router(routes_ocr.router)
app.include_router(routes_profile.router)
app.include_router(routes_conversations.router)


# Root endpoint
@app.get(
    "/",
    tags=["Root"],
    summary="API Root",
    response_model=dict
)
async def root():
    """
    API root endpoint with basic information
    """
    return {
        "name": settings.APP_NAME,
        "version": settings.APP_VERSION,
        "status": "operational",
        "docs": "/docs",
        "health": "/health",
        "timestamp": datetime.utcnow().isoformat()
    }


# Health check endpoint
@app.get(
    "/health",
    tags=["Health"],
    summary="Health Check",
    response_model=HealthResponse
)
async def health_check():
    """
    Health check endpoint for monitoring
    
    **Returns:**
    - Service status
    - Version information
    - Timestamp
    - Service availability
    """
    return HealthResponse(
        status="healthy",
        timestamp=datetime.utcnow().isoformat(),
        version=settings.APP_VERSION,
        services={
            "openai": "connected" if settings.OPENAI_API_KEY else "not_configured",
            "database": "not_configured",
            "cache": "not_configured"
        }
    )


# ==================== Main Entry Point ====================

if __name__ == "__main__":
    import uvicorn
    
    # SSL configuration
    ssl_config = {}
    if settings.SSL_KEYFILE and settings.SSL_CERTFILE:
        ssl_config = {
            "ssl_keyfile": settings.SSL_KEYFILE,
            "ssl_certfile": settings.SSL_CERTFILE
        }
        logger.info("SSL enabled")
    
    # Run server
    uvicorn.run(
        "main:app",
        host=settings.API_HOST,
        port=settings.API_PORT,
        reload=settings.DEBUG,
        log_level=settings.LOG_LEVEL.lower(),
        **ssl_config
    )

