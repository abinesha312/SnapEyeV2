# 🧪 SnapEye API Testing Guide

Complete guide to test all API endpoints with examples.

---

## 📋 Prerequisites

- Server running at: http://localhost:8000
- Start server: `python run_server.py`

---

## 1️⃣ Health & Status Endpoints

### Health Check

**GET** `/health`

**PowerShell:**

```powershell
Invoke-RestMethod http://localhost:8000/health
```

**Curl:**

```bash
curl http://localhost:8000/health
```

**Expected Response:**

```json
{
  "status": "healthy",
  "timestamp": "2024-01-01T00:00:00.000000",
  "version": "2.0.0",
  "services": {
    "openai": "not_configured",
    "database": "not_configured",
    "cache": "not_configured"
  }
}
```

### Root Endpoint

**GET** `/`

**PowerShell:**

```powershell
Invoke-RestMethod http://localhost:8000/
```

**Expected Response:**

```json
{
  "name": "SnapEye AI",
  "version": "2.0.0",
  "status": "operational",
  "docs": "/docs",
  "health": "/health",
  "timestamp": "2024-01-01T00:00:00.000000"
}
```

---

## 2️⃣ Authentication Endpoints

### Login

**POST** `/auth/login`

**PowerShell:**

```powershell
$body = @{
    username = "testuser"
    api_key = "sk-test-api-key-1234567890"
} | ConvertTo-Json

$response = Invoke-RestMethod -Uri http://localhost:8000/auth/login -Method Post -Body $body -ContentType "application/json"
$token = $response.access_token
Write-Output "Token: $token"
```

**Curl:**

```bash
curl -X POST http://localhost:8000/auth/login \
  -H "Content-Type: application/json" \
  -d '{
    "username": "testuser",
    "api_key": "sk-test-api-key-1234567890"
  }'
```

**Expected Response:**

```json
{
  "access_token": "eyJ0eXAiOiJKV1QiLCJhbGc...",
  "token_type": "bearer",
  "expires_in": 3600,
  "refresh_token": "eyJ0eXAiOiJKV1QiLCJhbGc..."
}
```

**Save the token for next requests!**

### Verify Token

**POST** `/auth/verify`

**PowerShell:**

```powershell
# Use token from login
$headers = @{
    Authorization = "Bearer $token"
}

Invoke-RestMethod -Uri http://localhost:8000/auth/verify -Method Post -Headers $headers
```

**Curl:**

```bash
curl -X POST http://localhost:8000/auth/verify \
  -H "Authorization: Bearer YOUR_TOKEN_HERE"
```

### Refresh Token

**POST** `/auth/refresh`

**PowerShell:**

```powershell
$headers = @{
    Authorization = "Bearer $token"
}

Invoke-RestMethod -Uri http://localhost:8000/auth/refresh -Method Post -Headers $headers
```

### Logout

**POST** `/auth/logout`

**PowerShell:**

```powershell
$headers = @{
    Authorization = "Bearer $token"
}

Invoke-RestMethod -Uri http://localhost:8000/auth/logout -Method Post -Headers $headers
```

---

## 3️⃣ Text Search Endpoint

### Search Text

**POST** `/api/search/text`

**PowerShell:**

```powershell
# First, login and get token
$loginBody = @{
    username = "testuser"
    api_key = "sk-test-api-key-1234567890"
} | ConvertTo-Json

$loginResponse = Invoke-RestMethod -Uri http://localhost:8000/auth/login -Method Post -Body $loginBody -ContentType "application/json"
$token = $loginResponse.access_token

# Then search
$headers = @{
    Authorization = "Bearer $token"
    "Content-Type" = "application/json"
}

$searchBody = @{
    query = "What is artificial intelligence?"
    temperature = 0.7
    max_results = 5
} | ConvertTo-Json

Invoke-RestMethod -Uri http://localhost:8000/api/search/text -Method Post -Headers $headers -Body $searchBody
```

**Curl:**

```bash
# Get token first, then:
curl -X POST http://localhost:8000/api/search/text \
  -H "Authorization: Bearer YOUR_TOKEN" \
  -H "Content-Type: application/json" \
  -d '{
    "query": "What is artificial intelligence?",
    "temperature": 0.7,
    "max_results": 5
  }'
```

**Expected Response:**

```json
{
  "success": true,
  "result": "Artificial intelligence (AI) is the simulation of human intelligence by machines, particularly computer systems. It involves creating algorithms and systems that can perform tasks requiring human-like intelligence such as learning, reasoning, problem-solving, perception, and language understanding.",
  "timestamp": "2024-01-01T00:00:00.000000",
  "model": "gpt-4o",
  "search_type": "text",
  "metadata": {
    "query": "What is artificial intelligence?",
    "temperature": 0.7,
    "tokens_used": 150,
    "finish_reason": "stop"
  }
}
```

**Note:** Response now includes plain text by default. Add `encrypt=true` parameter if you need encrypted results.

---

## 4️⃣ Image Search Endpoint

### Search Image

**POST** `/api/search/image`

**PowerShell:**

```powershell
# Get token first
$loginBody = @{
    username = "testuser"
    api_key = "sk-test-api-key-1234567890"
} | ConvertTo-Json

$loginResponse = Invoke-RestMethod -Uri http://localhost:8000/auth/login -Method Post -Body $loginBody -ContentType "application/json"
$token = $loginResponse.access_token

# Upload image
$headers = @{
    Authorization = "Bearer $token"
}

$imagePath = "C:\path\to\your\image.jpg"
$form = @{
    file = Get-Item -Path $imagePath
    query = "What objects are in this image?"
    temperature = "0.7"
}

Invoke-RestMethod -Uri http://localhost:8000/api/search/image -Method Post -Headers $headers -Form $form
```

**Curl:**

```bash
curl -X POST http://localhost:8000/api/search/image \
  -H "Authorization: Bearer YOUR_TOKEN" \
  -F "file=@/path/to/image.jpg" \
  -F "query=What objects are in this image?" \
  -F "temperature=0.7"
```

**Expected Response:**

```json
{
  "success": true,
  "result": "I can see several objects in this image: a laptop computer, a coffee mug, a notebook, and a pen on a wooden desk. The laptop appears to be open and displaying content. The scene suggests a typical workspace or study area.",
  "timestamp": "2024-01-01T00:00:00.000000",
  "model": "gpt-4o",
  "search_type": "image",
  "metadata": {
    "image_size_mb": 1.23,
    "query": "What objects are in this image?",
    "temperature": 0.7,
    "tokens_used": 200,
    "finish_reason": "stop"
  }
}
```

**Note:** Response now includes plain text by default. Add `encrypt=true` form field if you need encrypted results.

---

## 5️⃣ Decrypt Endpoint (Optional)

### Decrypt Result

**POST** `/api/decrypt`

**Note:** This endpoint is only needed if you request encrypted results using `encrypt=true` parameter.

**PowerShell:**

```powershell
# Only needed if you set encrypt=true in search request
$headers = @{
    Authorization = "Bearer $token"
    "Content-Type" = "application/json"
}

$decryptBody = @{
    encrypted_data = "gAAAAABhk..." # Use encrypted_result from search
} | ConvertTo-Json

$decrypted = Invoke-RestMethod -Uri http://localhost:8000/api/decrypt -Method Post -Headers $headers -Body $decryptBody
Write-Output $decrypted.decrypted_result
```

**Curl:**

```bash
curl -X POST http://localhost:8000/api/decrypt \
  -H "Authorization: Bearer YOUR_TOKEN" \
  -H "Content-Type: application/json" \
  -d '{
    "encrypted_data": "gAAAAABhk..."
  }'
```

**Request Encrypted Results:**

For text search, add query parameter:

```powershell
# Add encrypt=true to URL
Invoke-RestMethod -Uri "http://localhost:8000/api/search/text?encrypt=true" -Method Post ...
```

For image search, add to form:

```powershell
$form = @{
    file = Get-Item "image.jpg"
    query = "Analyze this"
    encrypt = "true"  # Add this
}
```

---

## 6️⃣ Real-time Transcription (WebSocket)

### WebSocket Connection

**WS** `/api/transcribe/live`

**JavaScript Example:**

```javascript
const token = "YOUR_JWT_TOKEN";
const ws = new WebSocket(
  `ws://localhost:8000/api/transcribe/live?token=${token}&voice=alloy&encrypt=true`
);

ws.onopen = () => {
  console.log("Connected to transcription service");
};

ws.onmessage = (event) => {
  const data = JSON.parse(event.data);
  console.log("Received:", data);

  if (data.type === "session.created") {
    console.log("Session ready:", data.session_id);
  }
};

ws.onerror = (error) => {
  console.error("WebSocket error:", error);
};

// Send audio data
function sendAudio(audioBase64) {
  ws.send(
    JSON.stringify({
      type: "input_audio_buffer.append",
      audio: audioBase64,
    })
  );
}
```

**Python Example:**

```python
import websockets
import asyncio
import json

async def transcribe():
    token = "YOUR_JWT_TOKEN"
    uri = f"ws://localhost:8000/api/transcribe/live?token={token}&voice=alloy"

    async with websockets.connect(uri) as websocket:
        # Wait for session creation
        response = await websocket.recv()
        print(f"Connected: {response}")

        # Send audio data
        await websocket.send(json.dumps({
            "type": "input_audio_buffer.append",
            "audio": "base64_audio_data_here"
        }))

        # Receive transcriptions
        while True:
            message = await websocket.recv()
            print(f"Received: {message}")

asyncio.run(transcribe())
```

### Active Sessions

**GET** `/api/transcribe/sessions`

**PowerShell:**

```powershell
Invoke-RestMethod http://localhost:8000/api/transcribe/sessions
```

---

## 7️⃣ Complete Testing Script

### Full End-to-End Test (PowerShell)

```powershell
# ================================
# SnapEye API Complete Test Script
# ================================

Write-Host "`n=== SnapEye API Testing ===" -ForegroundColor Cyan

# 1. Health Check
Write-Host "`n[1] Testing Health Endpoint..." -ForegroundColor Yellow
$health = Invoke-RestMethod http://localhost:8000/health
Write-Host "   Status: $($health.status)" -ForegroundColor Green

# 2. Root Endpoint
Write-Host "`n[2] Testing Root Endpoint..." -ForegroundColor Yellow
$root = Invoke-RestMethod http://localhost:8000/
Write-Host "   App: $($root.name) v$($root.version)" -ForegroundColor Green

# 3. Login
Write-Host "`n[3] Testing Login..." -ForegroundColor Yellow
$loginBody = @{
    username = "testuser"
    api_key = "sk-test-api-key-1234567890"
} | ConvertTo-Json

try {
    $loginResponse = Invoke-RestMethod -Uri http://localhost:8000/auth/login -Method Post -Body $loginBody -ContentType "application/json"
    $token = $loginResponse.access_token
    Write-Host "   ✓ Login successful!" -ForegroundColor Green
    Write-Host "   Token: $($token.Substring(0, 20))..." -ForegroundColor Gray
} catch {
    Write-Host "   ✗ Login failed: $_" -ForegroundColor Red
    exit
}

# 4. Verify Token
Write-Host "`n[4] Testing Token Verification..." -ForegroundColor Yellow
$headers = @{ Authorization = "Bearer $token" }
try {
    $verify = Invoke-RestMethod -Uri http://localhost:8000/auth/verify -Method Post -Headers $headers
    Write-Host "   ✓ Token valid!" -ForegroundColor Green
} catch {
    Write-Host "   ✗ Token invalid: $_" -ForegroundColor Red
}

# 5. Text Search
Write-Host "`n[5] Testing Text Search..." -ForegroundColor Yellow
$headers = @{
    Authorization = "Bearer $token"
    "Content-Type" = "application/json"
}

$searchBody = @{
    query = "What is machine learning?"
    temperature = 0.7
    max_results = 3
} | ConvertTo-Json

try {
    $searchResult = Invoke-RestMethod -Uri http://localhost:8000/api/search/text -Method Post -Headers $headers -Body $searchBody
    Write-Host "   ✓ Search successful!" -ForegroundColor Green
    Write-Host "   Model: $($searchResult.model)" -ForegroundColor Gray
    Write-Host "   Type: $($searchResult.search_type)" -ForegroundColor Gray
} catch {
    Write-Host "   ✗ Search failed: $_" -ForegroundColor Red
}

# 6. Display Result
if ($searchResult.result) {
    Write-Host "`n[6] Displaying Search Result..." -ForegroundColor Yellow
    Write-Host "   ✓ Result received!" -ForegroundColor Green
    $preview = $searchResult.result.Substring(0, [Math]::Min(100, $searchResult.result.Length))
    Write-Host "   Preview: $preview..." -ForegroundColor Gray
}

# 7. Transcription Sessions
Write-Host "`n[7] Testing Transcription Sessions..." -ForegroundColor Yellow
try {
    $sessions = Invoke-RestMethod http://localhost:8000/api/transcribe/sessions
    Write-Host "   ✓ Sessions endpoint working!" -ForegroundColor Green
    Write-Host "   Active sessions: $($sessions.active_sessions)" -ForegroundColor Gray
} catch {
    Write-Host "   ✗ Sessions check failed: $_" -ForegroundColor Red
}

Write-Host "`n=== Testing Complete ===" -ForegroundColor Cyan
Write-Host "`nAPI Documentation: http://localhost:8000/docs" -ForegroundColor Magenta
```

**Save as `test_api.ps1` and run:**

```powershell
.\test_api.ps1
```

---

## 8️⃣ Quick Test Commands

### One-Liner Tests

```powershell
# Health Check
Invoke-RestMethod http://localhost:8000/health

# Login & Get Token
$t = (Invoke-RestMethod -Uri http://localhost:8000/auth/login -Method Post -Body (@{username="user";api_key="sk-test-123456"} | ConvertTo-Json) -ContentType "application/json").access_token

# Text Search with Token
Invoke-RestMethod -Uri http://localhost:8000/api/search/text -Method Post -Headers @{Authorization="Bearer $t";"Content-Type"="application/json"} -Body (@{query="Hello"} | ConvertTo-Json)
```

---

## 9️⃣ Interactive API Documentation

### Swagger UI (Recommended)

**Open in browser:**

```
http://localhost:8000/docs
```

Features:

- ✅ Try all endpoints interactively
- ✅ See request/response schemas
- ✅ Test authentication
- ✅ Upload files
- ✅ View all parameters

### ReDoc Documentation

**Open in browser:**

```
http://localhost:8000/redoc
```

Features:

- ✅ Beautiful, clean interface
- ✅ Detailed endpoint descriptions
- ✅ Schema documentation
- ✅ Example responses

---

## 🔟 Troubleshooting

### Common Issues

**1. "Unauthorized" (401)**

- Token expired or invalid
- Solution: Login again to get new token

**2. "Forbidden" (403)**

- Missing Authorization header
- Solution: Include `Authorization: Bearer TOKEN`

**3. "Not Found" (404)**

- Wrong endpoint URL
- Check: http://localhost:8000/docs

**4. Connection Refused**

- Server not running
- Solution: `python run_server.py`

**5. "Invalid API Key" (OpenAI)**

- OpenAI API key not configured
- Add to `.env`: `OPENAI_API_KEY=sk-your-key`

---

## 📊 Expected Status Codes

| Code | Meaning          | When                  |
| ---- | ---------------- | --------------------- |
| 200  | OK               | Successful request    |
| 401  | Unauthorized     | Invalid/missing token |
| 403  | Forbidden        | Access denied         |
| 404  | Not Found        | Wrong endpoint        |
| 422  | Validation Error | Invalid request data  |
| 500  | Server Error     | Internal error        |

---

## 🎯 Next Steps

1. **Test with Swagger UI**: http://localhost:8000/docs
2. **Integrate with WPF app**: Use these endpoints
3. **Add OpenAI API Key**: For real AI responses
4. **Test WebSocket**: For real-time transcription

---

**Your API is ready for testing!** 🚀
