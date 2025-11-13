# 🚀 SnapEye API - Quick Reference Card

## 📡 Base URL
```
http://localhost:8000
```

---

## 🔥 Quick Start (Copy & Paste)

### 1. Health Check
```powershell
Invoke-RestMethod http://localhost:8000/health
```

### 2. Login & Get Token
```powershell
$token = (Invoke-RestMethod -Uri http://localhost:8000/auth/login -Method Post -Body (@{username="user";api_key="sk-test-key-1234567890"} | ConvertTo-Json) -ContentType "application/json").access_token
```

### 3. Text Search
```powershell
$h = @{Authorization="Bearer $token";"Content-Type"="application/json"}
Invoke-RestMethod -Uri http://localhost:8000/api/search/text -Method Post -Headers $h -Body (@{query="What is AI?"} | ConvertTo-Json)
```

---

## 📋 All Endpoints

| Endpoint | Method | Auth | Description |
|----------|--------|------|-------------|
| `/health` | GET | ❌ | Health check |
| `/` | GET | ❌ | API info |
| `/auth/login` | POST | ❌ | Get JWT token |
| `/auth/verify` | POST | ✅ | Verify token |
| `/auth/refresh` | POST | ✅ | Refresh token |
| `/auth/logout` | POST | ✅ | Logout |
| `/api/search/text` | POST | ✅ | Text search |
| `/api/search/image` | POST | ✅ | Image analysis |
| `/api/decrypt` | POST | ✅ | Decrypt result |
| `/api/transcribe/live` | WS | ✅ | Real-time audio |
| `/api/transcribe/sessions` | GET | ❌ | Active sessions |

---

## 🎯 Test All Endpoints

Run the test script:
```powershell
cd backend
.\test_api.ps1
```

---

## 📚 Documentation

- **Swagger UI**: http://localhost:8000/docs
- **ReDoc**: http://localhost:8000/redoc
- **Full Guide**: `API_TESTING.md`

---

## 💡 Common Commands

### Get Token
```powershell
$t = (Invoke-RestMethod -Uri http://localhost:8000/auth/login -Method Post -Body (@{username="user";api_key="sk-key-123"} | ConvertTo-Json) -ContentType "application/json").access_token
```

### Use Token
```powershell
$h = @{Authorization="Bearer $t"}
Invoke-RestMethod -Uri http://localhost:8000/api/search/text -Method Post -Headers $h -Body (@{query="test"} | ConvertTo-Json) -ContentType "application/json"
```

### Upload Image
```powershell
$h = @{Authorization="Bearer $t"}
$f = @{file=Get-Item "image.jpg"; query="What's this?"}
Invoke-RestMethod -Uri http://localhost:8000/api/search/image -Method Post -Headers $h -Form $f
```

---

## 🔧 Troubleshooting

**Server not running?**
```powershell
python run_server.py
```

**Need token?**
```powershell
.\test_api.ps1  # Will show you the token
```

**Test everything:**
```powershell
.\test_api.ps1
```

---

✅ **All Endpoints Tested & Working!**

