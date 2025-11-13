"""
Simple server startup script with error handling
"""
import sys
import os

# Add current directory to path
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

print("=" * 60)
print("SnapEye AI Backend Server")
print("=" * 60)

try:
    print("\n[1/5] Loading configuration...")
    from config import settings
    print(f"    OK - {settings.APP_NAME} v{settings.APP_VERSION}")
    
    print("\n[2/5] Initializing security...")
    from security import security_manager
    print("    OK - Security manager initialized")
    
    print("\n[3/5] Loading services...")
    from services import image_service, text_service
    print("    OK - AI services loaded")
    
    print("\n[4/5] Loading API routes...")
    from api import routes_auth, routes_search, routes_realtime
    print("    OK - API routes loaded")
    
    print("\n[5/5] Creating FastAPI application...")
    from main import app
    print(f"    OK - {app.title} ready")
    
    print("\n" + "=" * 60)
    print("Starting server...")
    print("=" * 60)
    print(f"\nServer will be available at:")
    print(f"  - API: http://{settings.API_HOST}:{settings.API_PORT}")
    print(f"  - Docs: http://localhost:{settings.API_PORT}/docs")
    print(f"  - Health: http://localhost:{settings.API_PORT}/health")
    print("\nPress CTRL+C to stop the server\n")
    
    import uvicorn
    uvicorn.run(
        "main:app",
        host=settings.API_HOST,
        port=settings.API_PORT,
        reload=settings.DEBUG,
        log_level=settings.LOG_LEVEL.lower()
    )
    
except KeyboardInterrupt:
    print("\n\nServer stopped by user")
    sys.exit(0)
    
except Exception as e:
    print(f"\n[ERROR] Failed to start server:")
    print(f"  {type(e).__name__}: {str(e)}")
    
    import traceback
    print("\nFull traceback:")
    traceback.print_exc()
    
    print("\n" + "=" * 60)
    print("Troubleshooting:")
    print("=" * 60)
    print("1. Check if all dependencies are installed:")
    print("   pip install -r requirements.txt")
    print("\n2. Check your .env file configuration")
    print("\n3. Make sure OPENAI_API_KEY is set")
    
    sys.exit(1)

