"""
Authentication Tests
Test authentication and security features
"""
import sys
import os
sys.path.insert(0, os.path.dirname(os.path.dirname(os.path.abspath(__file__))))

import pytest
from fastapi.testclient import TestClient

from main import app
from security import security_manager

client = TestClient(app)


class TestAuthentication:
    """Test authentication endpoints"""
    
    def test_login_success(self):
        """Test successful login"""
        response = client.post(
            "/auth/login",
            json={
                "username": "test_user",
                "api_key": "sk-test-api-key-12345678"
            }
        )
        
        assert response.status_code == 200
        data = response.json()
        assert "access_token" in data
        assert "token_type" in data
        assert data["token_type"] == "bearer"
        assert "expires_in" in data
    
    def test_login_invalid_api_key(self):
        """Test login with invalid API key"""
        response = client.post(
            "/auth/login",
            json={
                "username": "test_user",
                "api_key": "short"  # Too short
            }
        )
        
        assert response.status_code == 401
    
    def test_protected_endpoint_without_token(self):
        """Test accessing protected endpoint without token"""
        response = client.post(
            "/api/search/text",
            json={
                "query": "Test query"
            }
        )
        
        assert response.status_code == 403  # Forbidden
    
    def test_protected_endpoint_with_valid_token(self):
        """Test accessing protected endpoint with valid token"""
        # First login
        login_response = client.post(
            "/auth/login",
            json={
                "username": "test_user",
                "api_key": "sk-test-api-key-12345678"
            }
        )
        token = login_response.json()["access_token"]
        
        # Then access protected endpoint
        response = client.post(
            "/api/search/text",
            json={
                "query": "Test query"
            },
            headers={"Authorization": f"Bearer {token}"}
        )
        
        # Should not be 403 Forbidden
        assert response.status_code != 403


class TestSecurityManager:
    """Test SecurityManager class"""
    
    def test_encryption_decryption(self):
        """Test data encryption and decryption"""
        original = "sensitive data"
        
        # Encrypt
        encrypted = security_manager.encrypt_data(original)
        assert encrypted != original
        
        # Decrypt
        decrypted = security_manager.decrypt_data(encrypted)
        assert decrypted == original
    
    def test_api_key_hashing(self):
        """Test API key hashing"""
        api_key = "sk-test-key"
        
        hash1 = security_manager.hash_api_key(api_key)
        hash2 = security_manager.hash_api_key(api_key)
        
        # Same input should produce same hash
        assert hash1 == hash2
        
        # Hash should be different from original
        assert hash1 != api_key
    
    def test_jwt_token_creation_and_verification(self):
        """Test JWT token creation and verification"""
        # Create token
        data = {"sub": "test_user", "role": "admin"}
        token = security_manager.create_access_token(data)
        
        # Verify token
        payload = security_manager.verify_token(token)
        
        assert payload["sub"] == "test_user"
        assert "exp" in payload
        assert "iat" in payload


class TestHealthCheck:
    """Test health check endpoint"""
    
    def test_health_endpoint(self):
        """Test health check endpoint"""
        response = client.get("/health")
        
        assert response.status_code == 200
        data = response.json()
        assert data["status"] == "healthy"
        assert "version" in data
        assert "timestamp" in data
        assert "services" in data


if __name__ == "__main__":
    pytest.main([__file__, "-v"])

