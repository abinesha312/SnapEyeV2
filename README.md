# 👁️ SnapEye AI

**Enterprise-grade AI-powered vision and transcription platform**

Seamlessly integrate image analysis, text search, and real-time audio transcription into your applications with a beautiful WPF frontend and powerful FastAPI backend.

---

## 🌟 Features

### Frontend (WPF Application)

- **🎨 Modern UI**: Beautiful overlay interface with opacity control
- **📷 Camera Integration**: Capture and analyze images in real-time
- **🎙️ Listen Mode**: Real-time audio transcription
- **📊 Alpha Region**: Markdown-rendered AI responses
- **🎯 Draggable Overlay**: Always-on-top, transparent window
- **🎛️ Opacity Control**: Adjust transparency with slider

### Backend (FastAPI)

- **🖼️ Image Analysis**: GPT-4 Vision powered image understanding
- **📝 Text Search**: Natural language question answering
- **🎙️ Real-time Transcription**: Live speech-to-text with WebSocket
- **🔒 Enterprise Security**: JWT auth, encryption, rate limiting
- **📚 Auto-generated Docs**: Swagger UI and ReDoc
- **🏗️ OOP Architecture**: Clean, modular, reusable code

---

## 🏗️ Project Structure

```
SnapEye/
├── Application/                    # WPF Frontend
│   ├── navbar/                     # Navigation controls
│   │   ├── CameraButton.xaml
│   │   ├── ListenButton.xaml
│   │   └── RangeSliderControl.xaml
│   ├── SolutionRegion/             # AI Response Display
│   │   ├── SolutionRegionAlpha.xaml
│   │   └── SolutionRegionAlpha.xaml.cs
│   ├── OverlayWindow.xaml          # Main overlay window
│   ├── App.xaml                    # Application entry
│   └── SnapEye.csproj              # Project file
│
└── backend/                        # FastAPI Backend
    ├── api/                        # API routes
    │   ├── routes_auth.py          # Authentication
    │   ├── routes_search.py        # Search endpoints
    │   └── routes_realtime.py      # WebSocket transcription
    ├── config/                     # Configuration
    │   └── settings.py             # App settings
    ├── models/                     # Data models
    │   └── schemas.py              # Pydantic schemas
    ├── security/                   # Security layer
    │   └── auth.py                 # JWT & encryption
    ├── services/                   # Business logic
    │   ├── openai_service.py       # OpenAI integration
    │   ├── realtime_service.py     # Transcription
    │   └── prompts.py              # AI prompts
    ├── main.py                     # FastAPI app
    └── requirements.txt            # Dependencies
```

---

## 🚀 Quick Start

### Prerequisites

**Frontend:**

- .NET 8.0 SDK
- Windows 10/11

**Backend:**

- Python 3.10+
- OpenAI API Key

### Installation

#### 1. Backend Setup

```bash
# Navigate to backend
cd backend

# Create virtual environment
python -m venv venv
venv\Scripts\activate  # Windows

# Install dependencies
pip install -r requirements.txt

# Configure environment
copy env.example .env
# Edit .env with your OpenAI API key

# Run server
python main.py
```

Backend will be available at: http://localhost:8000

- **API Docs**: http://localhost:8000/docs
- **Health Check**: http://localhost:8000/health

#### 2. Frontend Setup

```bash
# Navigate to Application folder
cd Application

# Build project
dotnet build

# Run application
dotnet run
```

The SnapEye overlay window will appear on your screen!

---

## 💻 Usage

### Frontend Features

1. **Camera Button** 📷

   - Click to show sample AI analysis
   - Displays rich markdown content in Alpha Region

2. **Listen Button** 🎧

   - Shows loading animation
   - Simulates AI transcription response
   - Real-time status updates

3. **Opacity Slider** 🎛️

   - Adjust window transparency (20%-100%)
   - Controls both navbar and Alpha Region
   - Smooth real-time updates

4. **Drag to Move** ✋
   - Click and drag the header to reposition
   - Always stays on top of other windows

### Backend API

#### Authentication

```bash
curl -X POST "http://localhost:8000/auth/login" \
  -H "Content-Type: application/json" \
  -d '{"username": "user", "api_key": "your-key"}'
```

#### Image Search

```bash
curl -X POST "http://localhost:8000/api/search/image" \
  -H "Authorization: Bearer YOUR_TOKEN" \
  -F "file=@image.jpg" \
  -F "query=What's in this image?"
```

#### Text Search

```bash
curl -X POST "http://localhost:8000/api/search/text" \
  -H "Authorization: Bearer YOUR_TOKEN" \
  -H "Content-Type: application/json" \
  -d '{"query": "Explain quantum computing"}'
```

---

## 🧩 OOP Design Patterns

### Backend Architecture

1. **Factory Pattern**

   - `OpenAIServiceFactory`: Create service instances
   - Easy to extend with new AI providers

2. **Strategy Pattern**

   - `BaseAIService`: Abstract base class
   - `ImageSearchService`, `TextSearchService`: Concrete implementations

3. **Singleton Pattern**

   - `settings`: Global configuration
   - `security_manager`: Centralized security

4. **Dependency Injection**

   - Services injected via constructors
   - Loose coupling between components

5. **Repository Pattern**
   - Prepared for database integration
   - Clean separation of data access

### Frontend Architecture

1. **MVVM Pattern** (WPF Standard)

   - View (XAML)
   - ViewModel (Code-behind)
   - Model (Data classes)

2. **Observer Pattern**

   - Event-driven button clicks
   - Slider value change notifications

3. **Composite Pattern**
   - UserControls composition
   - Modular UI components

---

## 🔧 Configuration

### Backend Environment Variables

```env
OPENAI_API_KEY=your-api-key
SECRET_KEY=your-secret-key
ENCRYPTION_KEY=your-encryption-key
DEBUG=False
API_PORT=8000
```

See `backend/env.example` for full configuration options.

---

## 📚 API Documentation

### Endpoints Overview

| Method | Endpoint               | Description             |
| ------ | ---------------------- | ----------------------- |
| POST   | `/auth/login`          | User authentication     |
| POST   | `/auth/refresh`        | Refresh access token    |
| POST   | `/api/search/image`    | Analyze images          |
| POST   | `/api/search/text`     | Text-based search       |
| WS     | `/api/transcribe/live` | Real-time transcription |
| GET    | `/health`              | Health check            |

Full interactive documentation at: http://localhost:8000/docs

---

## 🧪 Testing

### Backend Tests

```bash
cd backend

# Run all tests
pytest

# Run with coverage
pytest --cov=backend --cov-report=html

# Run specific test
pytest tests/test_auth.py -v
```

---

## 🔒 Security Features

- ✅ JWT-based authentication
- ✅ Fernet symmetric encryption
- ✅ API key hashing (SHA-256)
- ✅ Token expiration and refresh
- ✅ Rate limiting ready
- ✅ CORS configuration
- ✅ SSL/TLS support

---

## 🛠️ Development

### Code Quality

```bash
# Format code
black backend/

# Lint
flake8 backend/
pylint backend/

# Type checking
mypy backend/
```

### Adding New Features

**Backend:**

1. Define models in `models/schemas.py`
2. Create service in `services/`
3. Add routes in `api/routes_*.py`
4. Update `main.py` to include router
5. Write tests

**Frontend:**

1. Create XAML component
2. Implement code-behind
3. Add to OverlayWindow
4. Wire up event handlers

---

## 📦 Dependencies

### Frontend

- .NET 8.0
- Markdig.Wpf 0.5.0.1

### Backend

- FastAPI 0.109.0
- OpenAI 1.12.0
- Cryptography 42.0.2
- PyJWT 2.8.0
- Pydantic 2.6.0
- Websockets 12.0

---

## 🎯 Roadmap

- [ ] Database integration (PostgreSQL)
- [ ] Redis caching layer
- [ ] User management system
- [ ] Search history tracking
- [ ] Export functionality
- [ ] Mobile app integration
- [ ] Docker containerization
- [ ] CI/CD pipeline
- [ ] Monitoring & analytics

---

## 📝 License

MIT License - See LICENSE file for details

---

## 🤝 Contributing

1. Fork the repository
2. Create feature branch (`git checkout -b feature/amazing-feature`)
3. Commit changes (`git commit -m 'Add amazing feature'`)
4. Push to branch (`git push origin feature/amazing-feature`)
5. Open Pull Request

---

## 💬 Support

- **Email**: support@snapeye.ai
- **Documentation**: Full docs available at `/docs` endpoint
- **Issues**: GitHub Issues

---

## 🙏 Acknowledgments

- OpenAI for GPT-4 Vision and Realtime API
- FastAPI for the amazing web framework
- Microsoft for WPF framework
- Markdig for markdown rendering

---

<div align="center">

**Built with ❤️ using .NET, Python, and AI**

[![FastAPI](https://img.shields.io/badge/FastAPI-009688?style=for-the-badge&logo=fastapi&logoColor=white)](https://fastapi.tiangolo.com/)
[![.NET](https://img.shields.io/badge/.NET-512BD4?style=for-the-badge&logo=dotnet&logoColor=white)](https://dotnet.microsoft.com/)
[![OpenAI](https://img.shields.io/badge/OpenAI-412991?style=for-the-badge&logo=openai&logoColor=white)](https://openai.com/)
[![Python](https://img.shields.io/badge/Python-3776AB?style=for-the-badge&logo=python&logoColor=white)](https://python.org/)

</div>
