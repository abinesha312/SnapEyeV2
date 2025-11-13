# ================================
# SnapEye API Complete Test Script
# ================================

Write-Host "`n=== SnapEye API Testing ===" -ForegroundColor Cyan

# 1. Health Check
Write-Host "`n[1] Testing Health Endpoint..." -ForegroundColor Yellow
try {
    $health = Invoke-RestMethod http://localhost:8000/health
    Write-Host "   Status: $($health.status)" -ForegroundColor Green
    Write-Host "   Version: $($health.version)" -ForegroundColor Gray
} catch {
    Write-Host "   FAILED: Server not responding!" -ForegroundColor Red
    Write-Host "   Make sure server is running: python run_server.py" -ForegroundColor Yellow
    exit
}

# 2. Root Endpoint
Write-Host "`n[2] Testing Root Endpoint..." -ForegroundColor Yellow
$root = Invoke-RestMethod http://localhost:8000/
Write-Host "   App: $($root.name) v$($root.version)" -ForegroundColor Green
Write-Host "   Status: $($root.status)" -ForegroundColor Gray

# 3. Login
Write-Host "`n[3] Testing Login..." -ForegroundColor Yellow
$loginBody = @{
    username = "testuser"
    api_key = "sk-test-api-key-1234567890"
} | ConvertTo-Json

try {
    $loginResponse = Invoke-RestMethod -Uri http://localhost:8000/auth/login -Method Post -Body $loginBody -ContentType "application/json"
    $token = $loginResponse.access_token
    Write-Host "   Login successful!" -ForegroundColor Green
    Write-Host "   Token: $($token.Substring(0, 30))..." -ForegroundColor Gray
    Write-Host "   Expires in: $($loginResponse.expires_in) seconds" -ForegroundColor Gray
} catch {
    Write-Host "   Login failed: $_" -ForegroundColor Red
    exit
}

# 4. Verify Token
Write-Host "`n[4] Testing Token Verification..." -ForegroundColor Yellow
$headers = @{ Authorization = "Bearer $token" }
try {
    $verify = Invoke-RestMethod -Uri http://localhost:8000/auth/verify -Method Post -Headers $headers
    Write-Host "   Token valid!" -ForegroundColor Green
    Write-Host "   User: $($verify.user)" -ForegroundColor Gray
} catch {
    Write-Host "   Token verification failed: $_" -ForegroundColor Red
}

# 5. Text Search (if OpenAI key configured)
Write-Host "`n[5] Testing Text Search..." -ForegroundColor Yellow
$searchHeaders = @{
    Authorization = "Bearer $token"
    "Content-Type" = "application/json"
}

$searchBody = @{
    query = "What is 2+2?"
    temperature = 0.7
    max_results = 1
} | ConvertTo-Json

try {
    $searchResult = Invoke-RestMethod -Uri http://localhost:8000/api/search/text -Method Post -Headers $searchHeaders -Body $searchBody
    Write-Host "   Search successful!" -ForegroundColor Green
    Write-Host "   Model: $($searchResult.model)" -ForegroundColor Gray
    Write-Host "   Type: $($searchResult.search_type)" -ForegroundColor Gray
    
    if ($searchResult.result) {
        $preview = $searchResult.result
        if ($preview.Length -gt 100) {
            $preview = $preview.Substring(0, 100) + "..."
        }
        Write-Host "   Result: $preview" -ForegroundColor Gray
    }
} catch {
    Write-Host "   Search failed (expected if no OpenAI key): $($_.Exception.Response.StatusCode)" -ForegroundColor Yellow
}

# 6. Display Result (now plain text by default)
if ($searchResult -and $searchResult.result) {
    Write-Host "`n[6] Result Received..." -ForegroundColor Yellow
    Write-Host "   Plain text result (no decryption needed)!" -ForegroundColor Green
    $fullPreview = $searchResult.result
    if ($fullPreview.Length -gt 200) {
        $fullPreview = $fullPreview.Substring(0, 200) + "..."
    }
    Write-Host "   Full preview: $fullPreview" -ForegroundColor Cyan
}

# 7. Transcription Sessions
Write-Host "`n[7] Testing Transcription Sessions..." -ForegroundColor Yellow
try {
    $sessions = Invoke-RestMethod http://localhost:8000/api/transcribe/sessions
    Write-Host "   Sessions endpoint working!" -ForegroundColor Green
    Write-Host "   Active sessions: $($sessions.active_sessions)" -ForegroundColor Gray
    Write-Host "   Status: $($sessions.status)" -ForegroundColor Gray
} catch {
    Write-Host "   Sessions check failed: $_" -ForegroundColor Red
}

# 8. Logout
Write-Host "`n[8] Testing Logout..." -ForegroundColor Yellow
try {
    $logout = Invoke-RestMethod -Uri http://localhost:8000/auth/logout -Method Post -Headers $headers
    Write-Host "   Logout successful!" -ForegroundColor Green
} catch {
    Write-Host "   Logout failed: $_" -ForegroundColor Red
}

# Summary
Write-Host "`n=== Testing Complete ===" -ForegroundColor Cyan
Write-Host "`nEndpoints Tested:" -ForegroundColor White
Write-Host "  - Health Check" -ForegroundColor Gray
Write-Host "  - Root Endpoint" -ForegroundColor Gray
Write-Host "  - Authentication (Login/Verify/Logout)" -ForegroundColor Gray
Write-Host "  - Text Search" -ForegroundColor Gray
Write-Host "  - Decryption" -ForegroundColor Gray
Write-Host "  - Transcription Sessions" -ForegroundColor Gray

Write-Host "`nInteractive Documentation:" -ForegroundColor White
Write-Host "  Swagger UI: http://localhost:8000/docs" -ForegroundColor Cyan
Write-Host "  ReDoc:      http://localhost:8000/redoc" -ForegroundColor Cyan

Write-Host "`nNOTE:" -ForegroundColor Yellow
Write-Host "  Add OPENAI_API_KEY to .env for full AI functionality" -ForegroundColor Gray
Write-Host ""

