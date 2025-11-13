"""Test if the server can start"""
import sys
import os

# Add current directory to path
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

print("=" * 50)
print("Testing SnapEye Backend Startup")
print("=" * 50)

try:
    print("\n1. Testing config import...")
    from config import settings
    print(f"   ✓ Settings loaded: {settings.APP_NAME}")
    
    print("\n2. Testing models import...")
    from models import HealthResponse, ErrorResponse
    print("   ✓ Models loaded")
    
    print("\n3. Testing security import...")
    from security import security_manager, auth_handler
    print("   ✓ Security loaded")
    
    print("\n4. Testing services import...")
    from services import image_service, text_service
    print("   ✓ Services loaded")
    
    print("\n5. Testing API routes import...")
    from api import routes_auth, routes_search, routes_realtime
    print("   ✓ API routes loaded")
    
    print("\n6. Testing FastAPI app...")
    from main import app
    print(f"   ✓ FastAPI app loaded: {app.title}")
    
    print("\n" + "=" * 50)
    print("✓ ALL TESTS PASSED!")
    print("=" * 50)
    print("\nYou can now run: python main.py")
    print("Or with uvicorn: uvicorn main:app --reload")
    
except Exception as e:
    print(f"\n✗ ERROR: {str(e)}")
    import traceback
    traceback.print_exc()
    sys.exit(1)

