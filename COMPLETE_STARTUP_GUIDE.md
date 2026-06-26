# SnapEye Complete Startup Guide

## Quick Start (2 Steps)

### Step 1: Start the Backend Server (REQUIRED FIRST)

Open a terminal in the `backend` folder:

```bash
cd D:\Abinesh_Profile\10_Projects\45_SnapEye\backend

# Activate virtual environment (if using one)
# venv\Scripts\activate

# Start the FastAPI backend
python main.py
```

**Expected Output:**
```
INFO:     Started server process
INFO:     Waiting for application startup.
INFO:     Application startup complete.
INFO:     Uvicorn running on http://0.0.0.0:8080
```

**Test Backend is Running:**
Open browser: http://localhost:8080/docs

---

### Step 2: Start the Frontend Application

Open a NEW terminal in the `Application` folder:

```bash
cd D:\Abinesh_Profile\10_Projects\45_SnapEye\Application

# Run the .NET application
dotnet run
```

---

## How to See Transcribed Text

### IMPORTANT: Where to Look for Transcriptions

1. **Click the Listen button** (🎤 icon) to start transcription
2. **Switch to the "🎤 Transcription" tab** (NOT the Chat tab)
3. Speak into your microphone
4. **Transcribed text will appear in the Transcription view**

### UI Layout:
```
┌─────────────────────────────────────────┐
│  🎤  📷  [Opacity Slider]  ⚙️          │  ← Navigation Bar
└─────────────────────────────────────────┘
┌─────────────────────────────────────────┐
│ [💬 Chat] [🎤 Transcription]  ← Tabs   │
│─────────────────────────────────────────│
│                                         │
│  👉 CLICK "🎤 Transcription" TAB       │
│     to see live transcripts!            │
│                                         │
│  - Blue bubbles (right) = Microphone    │
│  - Red bubbles (left) = Speaker         │
│                                         │
└─────────────────────────────────────────┘
```

---

## Troubleshooting

### ❌ Problem: "No transcribed text visible"

**Solution:** You're looking in the wrong tab!
- The "Chat" tab shows AI responses
- The "Transcription" tab shows live speech-to-text
- **Switch to the Transcription tab** after clicking Listen

---

### ❌ Problem: "Connection Failed"

**Cause:** Backend is not running

**Solution:**
1. Open a terminal in `backend` folder
2. Run: `python main.py`
3. Verify backend is running at http://localhost:8080
4. Then run the .NET app

---

### ❌ Problem: "Authentication Failed"

**Cause:** Backend credentials not configured

**Solution:**
Check `backend\.env` has:
```env
DEEPGRAM_API_KEY=337688740d505970403b54ee07886135f967dd74
OPENAI_API_KEY=sk-proj-...
```

---

### ❌ Problem: "No audio detected"

**Cause:** Microphone/Speaker not configured

**Solution:**
1. Check Windows Sound Settings
2. Ensure microphone is set as default input device
3. Test microphone in Windows Settings

---

## How It Works

### Real-time Transcription Flow:

1. **Click Listen Button** → Starts audio capture
2. **Audio Capture Service** → Captures microphone + speaker audio
3. **Send to Backend** → WebSocket connection to `ws://localhost:8080/api/transcribe/audio`
4. **Deepgram Transcription** → Backend transcribes audio using Deepgram API
5. **Receive Transcripts** → Frontend receives transcription messages
6. **Display in UI** → Transcripts appear in the **Transcription tab**

### Message Types:

- **🎤 Microphone (Blue bubble, right side)** - Your voice
- **🔊 Speaker (Red bubble, left side)** - System audio

---

## Configuration

### Backend Configuration (`backend\.env`):

```env
# Deepgram for speech-to-text
DEEPGRAM_API_KEY=337688740d505970403b54ee07886135f967dd74
DEEPGRAM_MODEL=nova-3
DEEPGRAM_LANGUAGE=en

# OpenAI for image analysis
OPENAI_API_KEY=sk-proj-...
```

### Frontend Configuration (`Application\config\AppConfig.cs`):

```csharp
public static string BackendHttpUrl = "http://localhost:8080";
public static string BackendWebSocketUrl = "ws://localhost:8080";
public static string DefaultUsername = "snapeye_user";
public static string DefaultApiKey = "sk-test-api-key-1234567890";
```

---

## Common Commands

### Backend:
```bash
cd backend

# Start server
python main.py

# Install dependencies
pip install -r requirements.txt

# Test Deepgram connection
curl http://localhost:8080/api/transcribe/test-connection
```

### Frontend:
```bash
cd Application

# Run app
dotnet run

# Build app
dotnet build

# Clean and rebuild
dotnet clean && dotnet build
```

---

## Features Available

✅ **Real-time Transcription**
- Click 🎤 Listen button
- Switch to Transcription tab
- See live speech-to-text

✅ **Image Analysis** (Coming Soon)
- Click 📷 Camera button
- Analyze screenshots

✅ **Opacity Control**
- Use slider to adjust window transparency

✅ **Settings**
- Click ⚙️ for visibility and quit options

---

## System Requirements

### Backend:
- Python 3.10+
- Deepgram API Key
- OpenAI API Key (for image features)
- Windows/Linux/macOS

### Frontend:
- .NET 8.0+
- Windows (for audio capture)
- Microphone and speaker

---

## Quick Verification Checklist

Before running the app:

- [ ] Backend `.env` file exists with `DEEPGRAM_API_KEY`
- [ ] Backend server is running on port 8080
- [ ] Frontend can access `http://localhost:8080`
- [ ] Microphone is connected and configured
- [ ] You know to switch to **Transcription tab** to see transcripts

---

## Success Indicators

### ✅ Backend Started Successfully:
```
INFO:     Uvicorn running on http://0.0.0.0:8080
INFO:     Starting SnapEye AI v2.0.0
```

### ✅ Frontend Started Successfully:
```
SnapEye: Found valid session for snapeye_user
[Dashboard opens with login/profile]
```

### ✅ Transcription Working:
1. Click Listen button
2. See "🎤 Real-time Transcription Active" in Chat tab
3. Switch to Transcription tab
4. **See blue/red bubbles appearing as you speak**

---

## Need Help?

1. Check console output for errors
2. Verify backend is running: http://localhost:8080/docs
3. Ensure you're on the **Transcription tab**
4. Check Windows audio settings
5. Review this guide

---

**Last Updated:** 2025-11-16
**Version:** 2.0.0
