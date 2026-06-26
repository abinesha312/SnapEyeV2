# 👁️ SnapEye AI - Real-time Vision & Transcription Platform

**Enterprise-grade AI-powered meeting assistant with live transcription, screen capture OCR, and contextual AI suggestions.**

[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](https://opensource.org/licenses/MIT)
[![.NET](https://img.shields.io/badge/.NET-8.0-512BD4)](https://dotnet.microsoft.com/)
[![Python](https://img.shields.io/badge/Python-3.10+-3776AB?logo=python&logoColor=white)](https://www.python.org/)
[![FastAPI](https://img.shields.io/badge/FastAPI-0.109.0-009688)](https://fastapi.tiangolo.com/)

---

## 🌟 Features

### 🎙️ Real-time Transcription
- **Deepgram Live API** (Nova-3 model) for ultra-low latency transcription
- Dual audio source tracking (microphone + speaker/system audio)
- 300ms endpointing for natural conversation segmentation
- Voice Activity Detection (VAD) for accurate speech boundaries
- Automatic punctuation and smart formatting

### 🖼️ Screen Capture & OCR
- Instant screen capture with OCR text extraction
- Contextual AI suggestions based on on-screen content
- Tesseract OCR integration for text recognition
- Automatic context injection into AI conversations

### 🤖 AI-Powered Intelligence
- **Multi-provider support**: OpenAI GPT-4o, Anthropic Claude
- Streaming responses for real-time feedback
- RAG (Retrieval-Augmented Generation) with ChromaDB
- Customizable AI modes (Interview, Sales, Meeting Notes, etc.)
- Quick actions for common tasks

### 🎨 Modern WPF Interface
- Beautiful dark-themed overlay window
- Always-on-top, transparent, draggable
- Global hotkeys (work even when other apps are focused)
- Stealth mode (invisible to screen capture/sharing)
- Live Insights panel with chat and transcript views

### 🔐 Enterprise Security
- JWT authentication with token refresh
- Fernet encryption for API keys
- Local credential storage in Windows AppData
- No credentials in git repository
- Rate limiting and CORS protection

---

## 🚀 Quick Start

### Prerequisites

**Frontend:**
- Windows 10/11
- [.NET 8.0 SDK](https://dotnet.microsoft.com/download)

**Backend:**
- Python 3.10+
- [OpenAI API Key](https://platform.openai.com/api-keys)
- [Deepgram API Key](https://console.deepgram.com/)
- (Optional) [Anthropic API Key](https://console.anthropic.com/)

---

## 📦 Installation

### 1. Clone Repository

```bash
git clone https://github.com/abinesha312/SnapEyeV2.git
cd SnapEyeV2
```

### 2. Backend Setup

```bash
cd backend

# Create virtual environment
python -m venv venv

# Activate virtual environment
# Windows:
venv\Scripts\activate
# macOS/Linux:
source venv/bin/activate

# Install dependencies
pip install -r requirements.txt

# Create configuration files
cp config/config.example.yaml config/config.yaml
cp .env.example .env

# Edit .env with your API keys
notepad .env  # or use your preferred editor
```

**Required `.env` variables:**
```bash
OPENAI_API_KEY=sk-your-openai-key-here
DEEPGRAM_API_KEY=your-deepgram-key-here
SECRET_KEY=generate-with-openssl-rand-hex-32
ENCRYPTION_KEY=generate-with-python-fernet

# Optional
ANTHROPIC_API_KEY=sk-ant-your-anthropic-key-here
```

**Generate secure keys:**
```bash
# Secret Key (JWT signing)
python -c "import secrets; print(secrets.token_hex(32))"

# Encryption Key (Fernet)
python -c "from cryptography.fernet import Fernet; print(Fernet.generate_key().decode())"
```

### 3. Frontend Setup

```bash
cd ../Application

# Copy example config
cp config/config.example.yaml config/config.yaml

# Build and run
dotnet build
dotnet run
```

Or open `Application/SnapEye.sln` in Visual Studio 2022.

### 4. Start Backend Server

```bash
cd backend
python main.py
```

Backend will be available at:
- API: http://localhost:8080
- Docs: http://localhost:8080/docs
- Health: http://localhost:8080/health

---

## 🎮 Usage

### Keyboard Shortcuts (Global)

| Shortcut | Action |
|----------|--------|
| `Ctrl+Shift+L` | Toggle listening (start/stop transcription) |
| `Ctrl+Shift+S` | Screenshot & OCR |
| `Ctrl+Shift+C` | Copy current AI answer |
| `Ctrl+Shift+H` | Hide/show overlay |
| `Ctrl+Shift+Up` | Expand Live Insights panel |
| `Ctrl+Shift+Down` | Collapse Live Insights panel |
| `Ctrl+Alt+←/→/↑/↓` | Nudge overlay position (28px increments) |
| `Ctrl+Enter` | Send message in chat |

### Features

1. **Listen Mode** 🎙️
   - Click the microphone button or press `Ctrl+Shift+L`
   - Speak naturally - transcription appears in real-time
   - AI automatically detects questions and provides suggestions
   - Switch between Chat and Transcript views

2. **Screen Capture** 📷
   - Press `Ctrl+Shift+S` to capture screen
   - Text is extracted via OCR
   - AI analyzes content and provides contextual insights

3. **Quick Actions** ⚡
   - "What should I say?" - Get conversation suggestions
   - "Follow up" - Generate follow-up questions
   - "Fact check" - Verify claims mentioned
   - "Recap" - Summarize conversation

4. **AI Modes** 🤖
   - General Assistant
   - Interview Mode
   - Sales Mode
   - Meeting Notes
   - Custom modes (configurable via Dashboard)

---

## 🏗️ Architecture

```
┌─────────────────────────────────────────────────────────────┐
│                     WPF Frontend (.NET 8)                    │
│  ┌──────────────┐  ┌──────────────┐  ┌──────────────┐      │
│  │ OverlayWindow│  │  Dashboard   │  │   Services   │      │
│  │   (XAML)     │  │   (Config)   │  │  (Audio/OCR) │      │
│  └──────────────┘  └──────────────┘  └──────────────┘      │
└─────────────────────────────────────────────────────────────┘
                           ▼ WebSocket / HTTP
┌─────────────────────────────────────────────────────────────┐
│                   FastAPI Backend (Python)                   │
│  ┌──────────────┐  ┌──────────────┐  ┌──────────────┐      │
│  │   API Routes │  │   Services   │  │  LLM Router  │      │
│  │ (WebSocket)  │  │  (Deepgram)  │  │ (OpenAI/Claude)│    │
│  └──────────────┘  └──────────────┘  └──────────────┘      │
└─────────────────────────────────────────────────────────────┘
                           ▼ API Calls
┌─────────────────────────────────────────────────────────────┐
│                      External APIs                           │
│   Deepgram (Transcription) │ OpenAI (LLM) │ Anthropic       │
└─────────────────────────────────────────────────────────────┘
```

---

## 📂 Project Structure

```
SnapEye/
├── Application/                    # WPF Frontend
│   ├── components/                 # UI Components
│   │   ├── Dashboard/              # Settings & config UI
│   │   └── SolutionRegion/         # AI response display
│   ├── services/                   # Business logic
│   │   ├── RealtimeTranscriptionService.cs
│   │   ├── ScreenCaptureService.cs
│   │   ├── GlobalHotkeyService.cs
│   │   ├── AiModelsService.cs
│   │   └── PromptsService.cs
│   ├── config/                     # Configuration
│   │   ├── AppConfig.cs
│   │   └── config.example.yaml
│   ├── OverlayWindow.xaml          # Main window
│   ├── KeyboardShortcutsWindow.xaml
│   └── SnapEye.csproj
│
├── backend/                        # FastAPI Backend
│   ├── api/                        # API endpoints
│   │   ├── routes_realtime.py      # WebSocket transcription
│   │   ├── routes_llm.py           # LLM streaming
│   │   └── routes_deepgram_live.py
│   ├── services/                   # Business logic
│   │   ├── deepgram_live_service.py
│   │   ├── llm_router.py
│   │   ├── ocr_service.py
│   │   └── rag_service.py
│   ├── config/                     # Configuration
│   │   ├── settings.py
│   │   └── config.example.yaml
│   ├── security/                   # Auth & encryption
│   │   └── auth.py
│   ├── .env.example                # Environment template
│   ├── requirements.txt
│   └── main.py                     # FastAPI app
│
├── .gitignore                      # Git exclusions (credentials)
├── SECURITY.md                     # Security documentation
└── README.md                       # This file
```

---

## 🔧 Configuration

### Backend (`backend/config/config.yaml`)

```yaml
deepgram:
  model: "nova-3"              # Deepgram model
  language: "en"               # Language code
  
llm:
  primary_provider: "openai"   # Primary LLM provider
  fallback_provider: "anthropic"
  max_tokens: 4096
  temperature: 0.7

audio:
  sample_rate: 24000           # 24kHz audio
  channels: 1                  # Mono
```

### Frontend (`Application/config/config.yaml`)

```yaml
backend:
  http_url: "http://localhost:8080"
  websocket_url: "ws://localhost:8080"

transcription:
  model: "nova-3"
  endpointing_ms: 300

ui:
  opacity: 0.95
  invisible_to_capture: true
```

---

## 🔐 Security

**Important**: Never commit credentials to git!

✅ **Protected files** (already in `.gitignore`):
- `.env` - API keys and secrets
- `config.yaml` - May contain sensitive settings
- `session.json` - Authentication tokens
- `ai_models.json` - Encrypted API keys
- `prompts.json` - User customizations
- `conversations/` - Chat history

📖 See [SECURITY.md](SECURITY.md) for complete security guide.

### If You Accidentally Committed Secrets

```bash
# Remove from git history
git filter-branch --force --index-filter \
  "git rm --cached --ignore-unmatch path/to/secret" \
  --prune-empty --tag-name-filter cat -- --all

# Force push (rewrites history)
git push origin --force --all

# IMMEDIATELY rotate exposed credentials!
```

---

## 🧪 Development

### Backend Tests

```bash
cd backend
pytest tests/ -v
pytest --cov=backend --cov-report=html
```

### Code Quality

```bash
# Format
black backend/
autopep8 Application/ --recursive --in-place

# Lint
flake8 backend/
pylint backend/

# Type checking
mypy backend/
```

---

## 📚 API Documentation

Interactive API docs available when backend is running:
- **Swagger UI**: http://localhost:8080/docs
- **ReDoc**: http://localhost:8080/redoc

### Example API Calls

**Authentication:**
```bash
curl -X POST "http://localhost:8080/auth/login" \
  -H "Content-Type: application/json" \
  -d '{"username": "user", "api_key": "your-key"}'
```

**Image Analysis:**
```bash
curl -X POST "http://localhost:8080/api/search/image" \
  -H "Authorization: Bearer YOUR_TOKEN" \
  -F "file=@screenshot.jpg" \
  -F "query=What's in this image?"
```

**WebSocket Transcription:**
```javascript
const ws = new WebSocket('ws://localhost:8080/api/transcribe/audio');
ws.send(JSON.stringify({ type: 'audio', data: base64Audio }));
```

---

## 🛣️ Roadmap

- [ ] Database persistence (PostgreSQL)
- [ ] Redis caching layer
- [ ] Multi-user support
- [ ] Mobile app companion
- [ ] Docker containerization
- [ ] CI/CD pipeline
- [ ] Monitoring & analytics
- [ ] Cloud deployment guides

---

## 🤝 Contributing

1. Fork the repository
2. Create feature branch (`git checkout -b feature/amazing-feature`)
3. Commit changes (`git commit -m 'Add amazing feature'`)
4. Push to branch (`git push origin feature/amazing-feature`)
5. Open Pull Request

---

## 📝 License

MIT License - See [LICENSE](LICENSE) file for details.

---

## 🙏 Acknowledgments

- [Deepgram](https://deepgram.com/) - Real-time speech recognition
- [OpenAI](https://openai.com/) - GPT-4 Vision & ChatGPT
- [Anthropic](https://anthropic.com/) - Claude AI
- [FastAPI](https://fastapi.tiangolo.com/) - Modern Python web framework
- [Microsoft](https://dotnet.microsoft.com/) - .NET & WPF

---

## 📞 Support

- **Issues**: [GitHub Issues](https://github.com/abinesha312/SnapEyeV2/issues)
- **Documentation**: Full docs at `/docs` endpoint when running
- **Security**: See [SECURITY.md](SECURITY.md)

---

**Built with ❤️ using .NET 8.0, Python, and AI**

*Last Updated: June 2026*
