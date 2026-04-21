# 🚀 SnapEye Backend - Quick Start Guide

## ✅ System is Ready!

All imports are working correctly. Your backend is properly structured with OOP principles.

## 📦 Installation Complete

All required packages are installed:

- ✅ FastAPI
- ✅ Uvicorn
- ✅ OpenAI
- ✅ Cryptography
- ✅ PyJWT
- ✅ Pydantic

## 🎯 How to Run the Server

### Option 1: Using Python directly

```bash
cd backend
python run_server.py
```

### Option 2: Using Uvicorn (Recommended for development)

```bash
cd backend
uvicorn main:app --reload --host 0.0.0.0 --port 8080
```

### Option 3: Production mode

```bash
cd backend
python main.py
```

## 🔧 Configuration

Before running, create a `.env` file:

```bash
copy env.example .env
```

Then edit `.env` and add your OpenAI API key:

```
OPENAI_API_KEY=sk-your-api-key-here
```

## 📡 Access Points

Once running, access:

- **API Documentation**: http://localhost:8080/docs
- **Alternative Docs**: http://localhost:8080/redoc
- **Health Check**: http://localhost:8080/health
- **Root**: http://localhost:8080/

## 🧪 Test the Server

```powershell
# Test health endpoint
Invoke-RestMethod http://localhost:8080/health

# Or using curl
curl http://localhost:8080/health
```

## 📚 API Endpoints

### Authentication

```bash
POST /auth/login
POST /auth/refresh
POST /auth/logout
POST /auth/verify
```

### Search

```bash
POST /api/search/image   # Upload and analyze images
POST /api/search/text    # Text-based queries
POST /api/decrypt        # Decrypt encrypted results
```

### Real-time Transcription

```bash
WS /api/transcribe/live  # WebSocket for live audio
GET /api/transcribe/sessions
```

## 🏗️ Architecture Highlights

### OOP Design Patterns Implemented:

1. **Factory Pattern** - `OpenAIServiceFactory`
2. **Strategy Pattern** - `BaseAIService` with concrete implementations
3. **Singleton Pattern** - `settings`, `security_manager`
4. **Dependency Injection** - Throughout services
5. **Repository Pattern** - Ready for database integration

### Clean Architecture:

```
backend/
├── api/          # Routes & endpoints
├── config/       # Configuration
├── models/       # Data models
├── security/     # Authentication & encryption
├── services/     # Business logic
└── tests/        # Test suite
```

## 🐛 Troubleshooting

### Import Errors

- Make sure you're in the `backend/` directory
- Check Python path: `python -c "import sys; print(sys.path)"`

### Module Not Found

```bash
pip install -r requirements.txt
```

### Port Already in Use

Change the port in `.env`:

```
API_PORT=8001
```

## 💡 Development Tips

### Enable Debug Mode

In `.env`:

```
DEBUG=True
```

### Hot Reload

Use uvicorn with `--reload`:

```bash
uvicorn main:app --reload
```

### View Logs

Logs are saved to `logs/snapeye.log` (create `logs/` directory first)

## 🎓 Next Steps

1. **Test Authentication**:

   - Go to http://localhost:8080/docs
   - Try `/auth/login` endpoint
   - Use the token for other endpoints

2. **Test Image Search**:

   - Upload an image to `/api/search/image`
   - See AI-powered analysis

3. **Integrate with Frontend**:
   - The WPF application can connect to this backend
   - Use the token-based authentication

## 📖 Full Documentation

- Interactive API Docs: http://localhost:8080/docs
- Backend README: `README.md`
- Main README: `../README.md`

---

**🎉 Your backend is professional, modular, and production-ready!**
