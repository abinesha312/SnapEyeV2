# 🔒 SnapEye Security Guide

## Credentials & Secrets Management

### Files to Never Commit

The following files contain sensitive information and are excluded via `.gitignore`:

#### Configuration Files
- `config.yaml` - May contain API endpoints or keys
- `.env` - Environment variables with API keys
- `*.local.json`, `*.local.yaml` - Local configuration overrides

#### Authentication & Sessions
- `session.json` - Contains JWT tokens and refresh tokens
- `ai_models.json` - Contains encrypted API keys for AI providers

#### User Data
- `prompts.json` - User's custom prompts
- `conversations/` - Conversation history
- `logs/` - May contain API responses or sensitive data

### Setup Instructions

#### 1. Backend Configuration

```bash
cd backend

# Copy example files
cp config/config.example.yaml config/config.yaml
cp .env.example .env

# Edit .env with your actual credentials
nano .env
```

Required environment variables:
```bash
OPENAI_API_KEY=sk-your-actual-key
ANTHROPIC_API_KEY=sk-ant-your-actual-key
DEEPGRAM_API_KEY=your-deepgram-key
SECRET_KEY=generate-with-openssl-rand-hex-32
ENCRYPTION_KEY=generate-with-python-fernet
```

#### 2. Application Configuration

```bash
cd Application

# Copy example config
cp config/config.example.yaml config/config.yaml

# Edit with your backend URL and preferences
```

### Generating Secure Keys

#### Secret Key (for JWT signing)
```bash
# Using OpenSSL
openssl rand -hex 32

# Using Python
python -c "import secrets; print(secrets.token_hex(32))"
```

#### Encryption Key (for Fernet)
```python
from cryptography.fernet import Fernet
print(Fernet.generate_key().decode())
```

### API Keys

1. **OpenAI**: Get from [platform.openai.com/api-keys](https://platform.openai.com/api-keys)
2. **Anthropic**: Get from [console.anthropic.com](https://console.anthropic.com/)
3. **Deepgram**: Get from [console.deepgram.com](https://console.deepgram.com/)

### Best Practices

✅ **DO:**
- Keep `.env` and `config.yaml` in `.gitignore`
- Use environment variables for sensitive data
- Rotate API keys regularly
- Use different keys for development and production
- Store production secrets in secure vaults (Azure Key Vault, AWS Secrets Manager)

❌ **DON'T:**
- Commit API keys or secrets to git
- Share `.env` files via email or chat
- Use default/example keys in production
- Hard-code credentials in source files

### Emergency: If You Accidentally Committed Secrets

```bash
# 1. Remove the file from git history
git filter-branch --force --index-filter \
  "git rm --cached --ignore-unmatch path/to/secret/file" \
  --prune-empty --tag-name-filter cat -- --all

# 2. Force push (WARNING: This rewrites history)
git push origin --force --all

# 3. IMMEDIATELY rotate the exposed credentials
# - Generate new API keys
# - Update your .env file
# - Notify your team if applicable
```

### Data Storage Locations

#### Windows (AppData)
- Session: `%APPDATA%\SnapEye\session.json`
- AI Models: `%APPDATA%\SnapEye\ai_models.json`
- Prompts: `%APPDATA%\SnapEye\prompts.json`
- Conversations: `%APPDATA%\SnapEye\conversations\`

#### Backend
- Database: `backend/data/snapeye.db`
- ChromaDB: `backend/data/chromadb/`
- Logs: `backend/logs/snapeye.log`

### Encryption Details

- **API Keys**: Encrypted using Fernet (symmetric encryption with ENCRYPTION_KEY)
- **JWT Tokens**: Signed using HS256 algorithm with SECRET_KEY
- **Passwords**: Hashed using SHA-256 (consider upgrading to bcrypt/argon2)
- **Session Data**: Stored encrypted locally in %APPDATA%

### Reporting Security Issues

If you discover a security vulnerability, please email: security@snapeye.ai

**Do not** open public GitHub issues for security vulnerabilities.

---

**Last Updated**: June 2026
**Version**: 2.0.0
