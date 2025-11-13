# SnapEye AI Backend

Enterprise-grade AI-powered backend service for image analysis, text search, and real-time transcription.

## 🌟 Features

### 🖼️ Image Analysis
- **GPT-4 Vision Integration**: Advanced image understanding
- **Object Detection**: Identify objects, people, and activities
- **OCR**: Extract text from images
- **Scene Understanding**: Contextual analysis
- **Question Answering**: Answer specific questions about images

### 📝 Text Search
- **Natural Language Processing**: Understanding complex queries
- **Information Retrieval**: Accurate, fact-based answers
- **Structured Responses**: Well-formatted markdown output
- **Contextual Explanations**: Detailed reasoning

### 🎙️ Real-time Transcription
- **Live Speech-to-Text**: Real-time audio transcription
- **Conversational AI**: Interactive dialogue management
- **Multiple Voices**: Choose from 6 different voice options
- **Auto Turn Detection**: Intelligent conversation handling

### 🔒 Security
- **JWT Authentication**: Secure token-based auth
- **End-to-End Encryption**: Fernet symmetric encryption
- **API Key Management**: Secure key hashing and validation
- **Rate Limiting**: Built-in protection against abuse

## 🏗️ Architecture

```
backend/
├── api/                    # API routes
│   ├── routes_auth.py      # Authentication endpoints
│   ├── routes_search.py    # Search endpoints
│   └── routes_realtime.py  # WebSocket endpoints
├── config/                 # Configuration
│   └── settings.py         # App settings & environment
├── models/                 # Data models
│   └── schemas.py          # Pydantic models
├── security/               # Security layer
│   └── auth.py             # Authentication & encryption
├── services/               # Business logic
│   ├── openai_service.py   # OpenAI integration
│   ├── realtime_service.py # Transcription service
│   └── prompts.py          # AI prompt templates
└── main.py                 # Application entry point
```

## 🚀 Quick Start

### Prerequisites
- Python 3.10+
- OpenAI API Key
- pip or conda

### Installation

1. **Clone the repository**
```bash
cd backend
```

2. **Create virtual environment**
```bash
python -m venv venv

# Windows
venv\Scripts\activate

# Unix/MacOS
source venv/bin/activate
```

3. **Install dependencies**
```bash
pip install -r requirements.txt
```

4. **Configure environment**
```bash
cp .env.example .env
# Edit .env with your configuration
```

5. **Run the server**
```bash
# Development
python main.py

# OR with uvicorn directly
uvicorn main:app --reload --host 0.0.0.0 --port 8000
```

6. **Access API documentation**
- Swagger UI: http://localhost:8000/docs
- ReDoc: http://localhost:8000/redoc

## ⚙️ Configuration

### Environment Variables

Create a `.env` file with the following variables:

```env
# OpenAI
OPENAI_API_KEY=sk-your-api-key-here

# Security
SECRET_KEY=your-secret-key-here
ENCRYPTION_KEY=your-encryption-key-here

# App Settings
DEBUG=False
API_HOST=0.0.0.0
API_PORT=8000
LOG_LEVEL=INFO

# SSL (Optional)
SSL_KEYFILE=path/to/key.pem
SSL_CERTFILE=path/to/cert.pem
```

## 📚 API Usage

### Authentication

**Login**
```bash
curl -X POST "http://localhost:8000/auth/login" \
  -H "Content-Type: application/json" \
  -d '{
    "username": "user123",
    "api_key": "your-api-key"
  }'
```

**Response:**
```json
{
  "access_token": "eyJ...",
  "token_type": "bearer",
  "expires_in": 3600,
  "refresh_token": "eyJ..."
}
```

### Image Search

```bash
curl -X POST "http://localhost:8000/api/search/image" \
  -H "Authorization: Bearer YOUR_TOKEN" \
  -F "file=@image.jpg" \
  -F "query=What objects are in this image?" \
  -F "temperature=0.7"
```

### Text Search

```bash
curl -X POST "http://localhost:8000/api/search/text" \
  -H "Authorization: Bearer YOUR_TOKEN" \
  -H "Content-Type: application/json" \
  -d '{
    "query": "What is artificial intelligence?",
    "temperature": 0.7,
    "max_results": 5
  }'
```

### Real-time Transcription (WebSocket)

```javascript
// JavaScript example
const ws = new WebSocket(
  'ws://localhost:8000/api/transcribe/live?token=YOUR_TOKEN&voice=alloy'
);

ws.onopen = () => {
  console.log('Connected');
};

ws.onmessage = (event) => {
  const data = JSON.parse(event.data);
  console.log('Received:', data);
};

// Send audio data
ws.send(JSON.stringify({
  type: 'input_audio_buffer.append',
  audio: base64AudioData
}));
```

## 🧩 OOP Design Patterns Used

### 1. **Factory Pattern**
- `OpenAIServiceFactory`: Creates service instances
- `create_transcription_service()`: Factory function

### 2. **Strategy Pattern**
- `BaseAIService`: Abstract base class
- `ImageSearchService`, `TextSearchService`: Concrete strategies

### 3. **Singleton Pattern**
- `settings`: Global configuration instance
- `security_manager`: Global security instance

### 4. **Dependency Injection**
- Services injected via constructors
- FastAPI dependencies for authentication

### 5. **Repository Pattern**
- Separation of data access logic (prepared for database integration)

### 6. **Facade Pattern**
- `OpenAIServiceFactory`: Simplifies service creation
- `PromptTemplates`: Centralizes prompt management

## 🧪 Testing

```bash
# Run all tests
pytest

# Run with coverage
pytest --cov=backend --cov-report=html

# Run specific test file
pytest tests/test_auth.py
```

## 📦 Project Structure Principles

### SOLID Principles

1. **Single Responsibility**: Each class has one reason to change
2. **Open/Closed**: Open for extension, closed for modification
3. **Liskov Substitution**: Subclasses can replace base classes
4. **Interface Segregation**: Small, specific interfaces
5. **Dependency Inversion**: Depend on abstractions, not concretions

### Clean Code Principles

- **DRY**: Don't Repeat Yourself
- **KISS**: Keep It Simple, Stupid
- **YAGNI**: You Aren't Gonna Need It
- **Separation of Concerns**: Each module has distinct responsibility
- **Dependency Injection**: Loose coupling between components

## 🔧 Development

### Code Quality

```bash
# Format code
black backend/

# Lint code
flake8 backend/
pylint backend/

# Type checking
mypy backend/
```

### Adding New Features

1. **Define models** in `models/schemas.py`
2. **Create service** in `services/`
3. **Add routes** in `api/routes_*.py`
4. **Update main.py** to include new router
5. **Add tests** in `tests/`
6. **Update documentation**

## 📝 License

MIT License - See LICENSE file for details

## 🤝 Contributing

1. Fork the repository
2. Create feature branch (`git checkout -b feature/amazing-feature`)
3. Commit changes (`git commit -m 'Add amazing feature'`)
4. Push to branch (`git push origin feature/amazing-feature`)
5. Open Pull Request

## 📧 Support

- **Email**: support@snapeye.ai
- **Documentation**: [Full Docs](https://docs.snapeye.ai)
- **Issues**: [GitHub Issues](https://github.com/snapeye/backend/issues)

---

Built with ❤️ using FastAPI, OpenAI, and modern Python practices.

