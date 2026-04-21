"""
SQLite Persistence Service for SnapEye
Stores conversation history, sessions, and keywords for cross-session retrieval.
"""
import logging
import os
from datetime import datetime
from typing import Dict, List, Optional

import aiosqlite

from config import settings

logger = logging.getLogger(__name__)

CREATE_TABLES_SQL = """
CREATE TABLE IF NOT EXISTS sessions (
    session_id TEXT PRIMARY KEY,
    username TEXT NOT NULL,
    started_at TEXT NOT NULL,
    ended_at TEXT,
    total_messages INTEGER DEFAULT 0
);

CREATE TABLE IF NOT EXISTS messages (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    session_id TEXT NOT NULL,
    message_id TEXT NOT NULL,
    source TEXT NOT NULL DEFAULT 'microphone',
    transcript TEXT NOT NULL,
    is_final INTEGER NOT NULL DEFAULT 1,
    created_at TEXT NOT NULL,
    FOREIGN KEY (session_id) REFERENCES sessions(session_id)
);

CREATE TABLE IF NOT EXISTS documents (
    document_id TEXT PRIMARY KEY,
    filename TEXT NOT NULL,
    doc_type TEXT NOT NULL DEFAULT 'text',
    chunks INTEGER DEFAULT 0,
    total_chars INTEGER DEFAULT 0,
    added_at TEXT NOT NULL
);

CREATE TABLE IF NOT EXISTS keywords (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    keyword TEXT NOT NULL UNIQUE,
    added_at TEXT NOT NULL
);

CREATE INDEX IF NOT EXISTS idx_messages_session ON messages(session_id);
CREATE INDEX IF NOT EXISTS idx_messages_created ON messages(created_at);
"""


class StorageService:
    """
    Async SQLite storage for conversation history and configuration.

    Tables:
    - sessions: session metadata
    - messages: finalized transcript messages
    - documents: uploaded RAG documents metadata
    - keywords: custom keyword lists
    """

    def __init__(self):
        self.db_path = settings.SQLITE_DB_PATH
        self._initialized = False

    async def initialize(self):
        """Create tables if they don't exist."""
        if self._initialized:
            return

        os.makedirs(os.path.dirname(self.db_path) or ".", exist_ok=True)

        async with aiosqlite.connect(self.db_path) as db:
            await db.executescript(CREATE_TABLES_SQL)
            await db.commit()

        self._initialized = True
        logger.info(f"Storage initialized at {self.db_path}")

    # --- Sessions ---

    async def create_session(self, session_id: str, username: str):
        """Record a new session."""
        await self.initialize()
        async with aiosqlite.connect(self.db_path) as db:
            await db.execute(
                "INSERT OR REPLACE INTO sessions (session_id, username, started_at) VALUES (?, ?, ?)",
                (session_id, username, datetime.utcnow().isoformat()),
            )
            await db.commit()

    async def end_session(self, session_id: str):
        """Mark a session as ended."""
        await self.initialize()
        async with aiosqlite.connect(self.db_path) as db:
            await db.execute(
                "UPDATE sessions SET ended_at = ? WHERE session_id = ?",
                (datetime.utcnow().isoformat(), session_id),
            )
            await db.commit()

    async def get_sessions(self, username: Optional[str] = None, limit: int = 50) -> List[Dict]:
        """Get recent sessions."""
        await self.initialize()
        async with aiosqlite.connect(self.db_path) as db:
            db.row_factory = aiosqlite.Row
            if username:
                cursor = await db.execute(
                    "SELECT * FROM sessions WHERE username = ? ORDER BY started_at DESC LIMIT ?",
                    (username, limit),
                )
            else:
                cursor = await db.execute(
                    "SELECT * FROM sessions ORDER BY started_at DESC LIMIT ?",
                    (limit,),
                )
            rows = await cursor.fetchall()
            return [dict(row) for row in rows]

    # --- Messages ---

    async def save_message(
        self,
        session_id: str,
        message_id: str,
        source: str,
        transcript: str,
        is_final: bool = True,
    ):
        """Save a finalized transcript message."""
        await self.initialize()
        async with aiosqlite.connect(self.db_path) as db:
            await db.execute(
                "INSERT INTO messages (session_id, message_id, source, transcript, is_final, created_at) "
                "VALUES (?, ?, ?, ?, ?, ?)",
                (session_id, message_id, source, transcript, int(is_final), datetime.utcnow().isoformat()),
            )
            await db.execute(
                "UPDATE sessions SET total_messages = total_messages + 1 WHERE session_id = ?",
                (session_id,),
            )
            await db.commit()

    async def get_messages(
        self,
        session_id: str,
        limit: int = 100,
    ) -> List[Dict]:
        """Get messages for a session."""
        await self.initialize()
        async with aiosqlite.connect(self.db_path) as db:
            db.row_factory = aiosqlite.Row
            cursor = await db.execute(
                "SELECT * FROM messages WHERE session_id = ? ORDER BY created_at ASC LIMIT ?",
                (session_id, limit),
            )
            rows = await cursor.fetchall()
            return [dict(row) for row in rows]

    async def search_messages(
        self,
        query: str,
        username: Optional[str] = None,
        limit: int = 50,
    ) -> List[Dict]:
        """Full-text search over stored messages."""
        await self.initialize()
        async with aiosqlite.connect(self.db_path) as db:
            db.row_factory = aiosqlite.Row
            if username:
                cursor = await db.execute(
                    "SELECT m.*, s.username FROM messages m "
                    "JOIN sessions s ON m.session_id = s.session_id "
                    "WHERE m.transcript LIKE ? AND s.username = ? "
                    "ORDER BY m.created_at DESC LIMIT ?",
                    (f"%{query}%", username, limit),
                )
            else:
                cursor = await db.execute(
                    "SELECT * FROM messages WHERE transcript LIKE ? "
                    "ORDER BY created_at DESC LIMIT ?",
                    (f"%{query}%", limit),
                )
            rows = await cursor.fetchall()
            return [dict(row) for row in rows]

    # --- Documents ---

    async def save_document(self, doc_info: Dict):
        """Save document metadata."""
        await self.initialize()
        async with aiosqlite.connect(self.db_path) as db:
            await db.execute(
                "INSERT OR REPLACE INTO documents (document_id, filename, doc_type, chunks, total_chars, added_at) "
                "VALUES (?, ?, ?, ?, ?, ?)",
                (
                    doc_info["document_id"],
                    doc_info.get("filename", "unknown"),
                    doc_info.get("doc_type", "text"),
                    doc_info.get("chunks", 0),
                    doc_info.get("total_chars", 0),
                    doc_info.get("added_at", datetime.utcnow().isoformat()),
                ),
            )
            await db.commit()

    async def get_documents(self) -> List[Dict]:
        """List all stored document metadata."""
        await self.initialize()
        async with aiosqlite.connect(self.db_path) as db:
            db.row_factory = aiosqlite.Row
            cursor = await db.execute("SELECT * FROM documents ORDER BY added_at DESC")
            rows = await cursor.fetchall()
            return [dict(row) for row in rows]

    async def delete_document(self, document_id: str):
        """Delete document metadata."""
        await self.initialize()
        async with aiosqlite.connect(self.db_path) as db:
            await db.execute("DELETE FROM documents WHERE document_id = ?", (document_id,))
            await db.commit()

    # --- Keywords ---

    async def save_keywords(self, keywords: List[str]):
        """Save custom keywords."""
        await self.initialize()
        async with aiosqlite.connect(self.db_path) as db:
            now = datetime.utcnow().isoformat()
            for kw in keywords:
                kw = kw.strip()
                if kw:
                    await db.execute(
                        "INSERT OR IGNORE INTO keywords (keyword, added_at) VALUES (?, ?)",
                        (kw, now),
                    )
            await db.commit()

    async def get_keywords(self) -> List[str]:
        """Get all stored keywords."""
        await self.initialize()
        async with aiosqlite.connect(self.db_path) as db:
            cursor = await db.execute("SELECT keyword FROM keywords ORDER BY keyword")
            rows = await cursor.fetchall()
            return [row[0] for row in rows]

    async def delete_keyword(self, keyword: str):
        """Delete a keyword."""
        await self.initialize()
        async with aiosqlite.connect(self.db_path) as db:
            await db.execute("DELETE FROM keywords WHERE keyword = ?", (keyword.strip(),))
            await db.commit()


# Global service instance
storage_service = StorageService()
