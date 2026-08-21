"""
RAG (Retrieval-Augmented Generation) Service for SnapEye
ChromaDB vector database with OpenAI embeddings for document search.
"""
import hashlib
import logging
import os
import uuid
from datetime import datetime
from typing import Dict, List, Optional

from config import settings

logger = logging.getLogger(__name__)


class RAGChunk:
    """A single chunk of text with metadata."""

    def __init__(
        self,
        text: str,
        document_id: str,
        chunk_index: int,
        metadata: Optional[Dict] = None,
        score: float = 0.0,
    ):
        self.text = text
        self.document_id = document_id
        self.chunk_index = chunk_index
        self.metadata = metadata or {}
        self.score = score

    def to_dict(self) -> Dict:
        return {
            "text": self.text,
            "document_id": self.document_id,
            "chunk_index": self.chunk_index,
            "metadata": self.metadata,
            "score": self.score,
        }


class RAGService:
    """
    Retrieval-Augmented Generation service using ChromaDB and OpenAI embeddings.

    Features:
    - Document upload, chunking, and embedding
    - Semantic search over knowledge base
    - Transcript segment indexing for cross-session context
    - Document management (list, delete)
    """

    def __init__(self):
        self._client = None
        self._collection = None
        self._transcript_collection = None
        self._experience_collection = None
        self._user_memory_collection = None
        self._initialized = False
        self._documents: Dict[str, Dict] = {}  # track uploaded docs
        # In-memory cache of per-user experience JSON (canonical store).
        self._experience_cache: Dict[str, List[Dict]] = {}

    def _ensure_initialized(self):
        """Lazy initialization of ChromaDB."""
        if self._initialized:
            return

        try:
            import chromadb

            if settings.CHROMA_HOST:
                # Standalone ChromaDB server (e.g. the "chromadb" container in
                # podman-compose.yml) - required for multi-process/multi-user
                # deployments since an embedded PersistentClient can only be opened
                # by one process at a time.
                self._client = chromadb.HttpClient(
                    host=settings.CHROMA_HOST, port=settings.CHROMA_PORT
                )
                logger.info(f"Connecting to ChromaDB server at {settings.CHROMA_HOST}:{settings.CHROMA_PORT}")
            else:
                # Local dev fallback: no Podman/ChromaDB server required.
                data_dir = settings.RAG_DATA_DIR
                os.makedirs(data_dir, exist_ok=True)
                self._client = chromadb.PersistentClient(path=data_dir)

            self._collection = self._client.get_or_create_collection(
                name="knowledge_base",
                metadata={"hnsw:space": "cosine"},
            )
            self._transcript_collection = self._client.get_or_create_collection(
                name="transcripts",
                metadata={"hnsw:space": "cosine"},
            )
            # Dedicated collection for the user's professional experience and education.
            # Each entry (a company/role or a school) is a single embedded document so
            # spoken/typed questions retrieve the most relevant experience in real time.
            self._experience_collection = self._client.get_or_create_collection(
                name="experience",
                metadata={"hnsw:space": "cosine"},
            )
            # Per-user conversation / personal Q&A memory for cross-session retrieval.
            self._user_memory_collection = self._client.get_or_create_collection(
                name="user_memory",
                metadata={"hnsw:space": "cosine"},
            )
            self._initialized = True
            logger.info(
                f"RAG service initialized (knowledge_base: {self._collection.count()} docs, "
                f"transcripts: {self._transcript_collection.count()} segments, "
                f"experience: {self._experience_collection.count()} entries, "
                f"user_memory: {self._user_memory_collection.count()} segments)"
            )
        except Exception as e:
            logger.error(f"RAG initialization error: {e}")
            raise

    async def add_document(
        self,
        text: str,
        filename: str = "unknown",
        doc_type: str = "text",
        metadata: Optional[Dict] = None,
        username: Optional[str] = None,
    ) -> Dict:
        """
        Add a document to the knowledge base.
        Chunks the text, generates embeddings, and stores in ChromaDB.

        Returns document info dict.
        """
        self._ensure_initialized()

        doc_id = str(uuid.uuid4())
        chunks = self._chunk_text(text)

        if not chunks:
            return {"error": "Document is empty or could not be chunked"}

        # Generate embeddings
        embeddings = await self._get_embeddings([c for c in chunks])

        # Store in ChromaDB
        ids = [f"{doc_id}_chunk_{i}" for i in range(len(chunks))]
        metadatas = [
            {
                "document_id": doc_id,
                "filename": filename,
                "doc_type": doc_type,
                "chunk_index": i,
                "total_chunks": len(chunks),
                "added_at": datetime.utcnow().isoformat(),
                "username": username or "",
                **(metadata or {}),
            }
            for i in range(len(chunks))
        ]

        self._collection.add(
            ids=ids,
            documents=chunks,
            embeddings=embeddings,
            metadatas=metadatas,
        )

        doc_info = {
            "document_id": doc_id,
            "filename": filename,
            "doc_type": doc_type,
            "chunks": len(chunks),
            "total_chars": len(text),
            "added_at": datetime.utcnow().isoformat(),
        }
        self._documents[doc_id] = doc_info

        logger.info(f"Added document '{filename}' ({len(chunks)} chunks, {len(text)} chars)")
        return doc_info

    async def search(
        self,
        query: str,
        top_k: int = 0,
        filter_doc_id: Optional[str] = None,
        username: Optional[str] = None,
    ) -> List[RAGChunk]:
        """
        Semantic search over the knowledge base.

        Args:
            query: Search query text
            top_k: Number of results to return
            filter_doc_id: Optional document ID to filter by
            username: Optional username to scope results to that user's own uploads

        Returns list of RAGChunk objects sorted by relevance.
        """
        self._ensure_initialized()
        top_k = top_k or settings.RAG_TOP_K

        query_embedding = await self._get_embeddings([query])

        where_filter = self._combine_where(
            {"document_id": filter_doc_id} if filter_doc_id else None,
            {"username": username} if username else None,
        )

        results = self._collection.query(
            query_embeddings=query_embedding,
            n_results=top_k,
            where=where_filter,
            include=["documents", "metadatas", "distances"],
        )

        chunks = []
        if results["documents"] and results["documents"][0]:
            for i, doc in enumerate(results["documents"][0]):
                meta = results["metadatas"][0][i] if results["metadatas"] else {}
                distance = results["distances"][0][i] if results["distances"] else 0.0
                score = 1.0 - distance  # cosine distance -> similarity

                chunks.append(RAGChunk(
                    text=doc,
                    document_id=meta.get("document_id", ""),
                    chunk_index=meta.get("chunk_index", 0),
                    metadata=meta,
                    score=score,
                ))

        logger.debug(f"RAG search for '{query[:50]}...' returned {len(chunks)} results")
        return chunks

    @staticmethod
    def _combine_where(*conditions: Optional[Dict]) -> Optional[Dict]:
        """
        Combine zero or more single-key filters into one Chroma `where` clause.
        Chroma requires an explicit `$and` when more than one condition applies -
        passing multiple top-level keys directly raises "expected exactly one operator".
        """
        active = [c for c in conditions if c]
        if not active:
            return None
        if len(active) == 1:
            return active[0]
        return {"$and": active}

    async def add_transcript_segment(
        self,
        text: str,
        session_id: str,
        source: str = "microphone",
        message_id: str = "",
        username: Optional[str] = None,
    ):
        """Index a finalized transcript segment for cross-session retrieval."""
        self._ensure_initialized()

        if not text or len(text.strip()) < 10:
            return

        segment_id = f"transcript_{session_id}_{message_id or uuid.uuid4().hex[:8]}"
        embedding = await self._get_embeddings([text])

        self._transcript_collection.add(
            ids=[segment_id],
            documents=[text],
            embeddings=embedding,
            metadatas=[{
                "session_id": session_id,
                "source": source,
                "message_id": message_id,
                "username": username or "",
                "indexed_at": datetime.utcnow().isoformat(),
            }],
        )

    async def search_transcripts(
        self,
        query: str,
        top_k: int = 5,
        session_id: Optional[str] = None,
        username: Optional[str] = None,
    ) -> List[RAGChunk]:
        """Search over indexed transcript segments."""
        self._ensure_initialized()

        query_embedding = await self._get_embeddings([query])

        where_filter = self._combine_where(
            {"session_id": session_id} if session_id else None,
            {"username": username} if username else None,
        )

        results = self._transcript_collection.query(
            query_embeddings=query_embedding,
            n_results=top_k,
            where=where_filter,
            include=["documents", "metadatas", "distances"],
        )

        chunks = []
        if results["documents"] and results["documents"][0]:
            for i, doc in enumerate(results["documents"][0]):
                meta = results["metadatas"][0][i] if results["metadatas"] else {}
                distance = results["distances"][0][i] if results["distances"] else 0.0

                chunks.append(RAGChunk(
                    text=doc,
                    document_id=meta.get("session_id", ""),
                    chunk_index=0,
                    metadata=meta,
                    score=1.0 - distance,
                ))

        return chunks

    def list_documents(self) -> List[Dict]:
        """List all uploaded documents."""
        return list(self._documents.values())

    async def delete_document(self, document_id: str) -> bool:
        """Delete a document and all its chunks from the knowledge base."""
        self._ensure_initialized()

        try:
            # Find all chunk IDs for this document
            results = self._collection.get(
                where={"document_id": document_id},
                include=[],
            )

            if results["ids"]:
                self._collection.delete(ids=results["ids"])

            self._documents.pop(document_id, None)
            logger.info(f"Deleted document {document_id}")
            return True
        except Exception as e:
            logger.error(f"Delete document error: {e}")
            return False

    def _chunk_text(self, text: str) -> List[str]:
        """
        Split text into chunks of approximately chunk_size tokens
        with overlap for context continuity.
        """
        chunk_size = settings.RAG_CHUNK_SIZE
        overlap = settings.RAG_CHUNK_OVERLAP

        # Approximate: 1 token ~ 4 chars
        char_chunk_size = chunk_size * 4
        char_overlap = overlap * 4

        # Split by paragraphs first, then consolidate into chunks
        paragraphs = [p.strip() for p in text.split("\n\n") if p.strip()]
        if not paragraphs:
            paragraphs = [p.strip() for p in text.split("\n") if p.strip()]
        if not paragraphs:
            paragraphs = [text]

        chunks = []
        current_chunk = ""

        for para in paragraphs:
            if len(current_chunk) + len(para) + 1 <= char_chunk_size:
                current_chunk = f"{current_chunk}\n{para}" if current_chunk else para
            else:
                if current_chunk:
                    chunks.append(current_chunk.strip())
                # Start new chunk with overlap from previous
                if chunks and char_overlap > 0:
                    prev = chunks[-1]
                    overlap_text = prev[-char_overlap:] if len(prev) > char_overlap else prev
                    current_chunk = f"{overlap_text}\n{para}"
                else:
                    current_chunk = para

        if current_chunk.strip():
            chunks.append(current_chunk.strip())

        return chunks

    async def _get_embeddings(self, texts: List[str]) -> List[List[float]]:
        """Generate embeddings using the LLM router."""
        from services.llm_router import llm_router
        return await llm_router.generate_embeddings(texts)

    # === Experience / Education (JSON canonical store + ChromaDB vector index) ===
    #
    # JSON is the source of truth (fast CRUD, no dependency on embeddings). Each write
    # also upserts into the "experience" collection for semantic matching at query time.
    # If ChromaDB/embeddings are unavailable, keyword search over JSON still works.

    @staticmethod
    def _safe_username(username: Optional[str]) -> str:
        u = (username or "_default").strip() or "_default"
        return u.replace("/", "_").replace("\\", "_")

    def _experience_json_path(self, username: Optional[str] = None) -> str:
        base = os.path.join(os.path.dirname(settings.RAG_DATA_DIR) or ".", "experience")
        os.makedirs(base, exist_ok=True)
        return os.path.join(base, f"{self._safe_username(username)}.json")

    def _load_experience_entries(self, username: Optional[str] = None) -> List[Dict]:
        key = self._safe_username(username)
        if key in self._experience_cache:
            return list(self._experience_cache[key])
        entries: List[Dict] = []
        try:
            path = self._experience_json_path(username)
            if os.path.exists(path):
                import json
                with open(path, "r", encoding="utf-8") as f:
                    data = json.load(f)
                if isinstance(data, list):
                    entries = [e for e in data if isinstance(e, dict)]
        except Exception as e:
            logger.error(f"Load experience JSON error: {e}")
        self._experience_cache[key] = entries
        return list(entries)

    def _save_experience_entries(self, username: Optional[str], entries: List[Dict]) -> None:
        key = self._safe_username(username)
        self._experience_cache[key] = list(entries)
        import json
        path = self._experience_json_path(username)
        tmp = path + ".tmp"
        with open(tmp, "w", encoding="utf-8") as f:
            json.dump(entries, f, ensure_ascii=False, indent=2)
        os.replace(tmp, path)

    async def _embed_experience_entry(self, metadata: Dict, text: str) -> None:
        """Best-effort vector upsert; JSON save already succeeded."""
        try:
            self._ensure_initialized()
            embedding = await self._get_embeddings([text])
            self._experience_collection.upsert(
                ids=[metadata["entry_id"]],
                documents=[text],
                embeddings=embedding,
                metadatas=[metadata],
            )
        except Exception as e:
            logger.warning(f"Experience vector upsert skipped (JSON saved): {e}")

    def _delete_experience_vector(self, entry_id: str, username: Optional[str] = None) -> None:
        try:
            self._ensure_initialized()
            where = self._combine_where({"username": username} if username else None)
            self._experience_collection.delete(ids=[entry_id], where=where)
        except Exception as e:
            logger.debug(f"Experience vector delete skipped: {e}")

    @staticmethod
    def _compose_experience_text(
        company: str, role: str, start_date: str, end_date: str,
        summary: str, entry_type: str,
    ) -> str:
        """Build the human-readable/embeddable text for an experience/education entry."""
        header_bits = [b for b in [role, company] if b]
        header = " at ".join(header_bits) if header_bits else (company or role or "Experience")
        period = ""
        if start_date or end_date:
            period = f" ({start_date or '?'} - {end_date or 'Present'})"
        label = "Education" if entry_type == "education" else "Experience"
        return f"{label}: {header}{period}.\n{summary}".strip()

    def _keyword_search_experience(
        self, query: str, top_k: int, username: Optional[str]
    ) -> List[RAGChunk]:
        """Fallback when vector search is unavailable — keyword overlap over JSON entries."""
        entries = self._load_experience_entries(username)
        if not query or not entries:
            return []

        import re
        stop = {
            "the", "a", "an", "and", "or", "of", "to", "in", "on", "for", "with", "your",
            "you", "me", "my", "i", "about", "can", "explain", "tell", "what", "how",
        }
        terms = [t for t in re.findall(r"[a-z0-9]+", query.lower()) if t not in stop and len(t) > 1]
        scored = []
        for idx, e in enumerate(entries):
            hay = " ".join([
                str(e.get("company", "")), str(e.get("role", "")),
                str(e.get("summary", "")), str(e.get("entry_type", "")),
            ]).lower()
            overlap = sum(1 for t in set(terms) if t in hay)
            scored.append((overlap, e.get("updated_at", ""), e))

        any_match = any(s[0] > 0 for s in scored)
        scored.sort(key=lambda s: (s[0], s[1]), reverse=True)

        chunks: List[RAGChunk] = []
        for overlap, _rec, e in scored[:top_k]:
            if any_match and overlap == 0:
                continue
            text = self._compose_experience_text(
                e.get("company", ""), e.get("role", ""), e.get("start_date", ""),
                e.get("end_date", ""), e.get("summary", ""), e.get("entry_type", "work"),
            )
            score = min(1.0, overlap / max(1, len(set(terms)))) if any_match else 0.35
            chunks.append(RAGChunk(
                text=text,
                document_id=e.get("entry_id", ""),
                chunk_index=0,
                metadata=e,
                score=score,
            ))
        return chunks

    async def add_experience(
        self,
        company: str = "",
        role: str = "",
        start_date: str = "",
        end_date: str = "",
        summary: str = "",
        entry_type: str = "work",
        entry_id: Optional[str] = None,
        username: Optional[str] = None,
    ) -> Dict:
        """Add (or replace) a single experience entry — JSON first, then vector index."""
        if not (summary.strip() or company.strip() or role.strip()):
            return {"error": "Experience needs at least a company, role, or summary"}

        entry_id = entry_id or str(uuid.uuid4())
        metadata = {
            "entry_id": entry_id,
            "company": company,
            "role": role,
            "start_date": start_date,
            "end_date": end_date,
            "summary": summary,
            "entry_type": entry_type,
            "username": username or "",
            "updated_at": datetime.utcnow().isoformat(),
        }

        entries = self._load_experience_entries(username)
        entries = [e for e in entries if e.get("entry_id") != entry_id]
        entries.append(metadata)
        self._save_experience_entries(username, entries)

        text = self._compose_experience_text(company, role, start_date, end_date, summary, entry_type)
        await self._embed_experience_entry(metadata, text)
        logger.info(f"Saved experience entry {entry_id} ({role} @ {company})")
        return metadata

    async def update_experience(self, entry_id: str, username: Optional[str] = None, **fields) -> Dict:
        return await self.add_experience(entry_id=entry_id, username=username, **fields)

    def delete_experience(self, entry_id: str, username: Optional[str] = None) -> bool:
        entries = self._load_experience_entries(username)
        new_entries = [e for e in entries if e.get("entry_id") != entry_id]
        if len(new_entries) == len(entries):
            return False
        self._save_experience_entries(username, new_entries)
        self._delete_experience_vector(entry_id, username)
        logger.info(f"Deleted experience entry {entry_id}")
        return True

    def list_experiences(self, username: Optional[str] = None) -> List[Dict]:
        try:
            entries = self._load_experience_entries(username)
            entries.sort(
                key=lambda m: (m.get("entry_type") == "education", m.get("updated_at", "")),
                reverse=False,
            )
            return entries
        except Exception as e:
            logger.error(f"List experiences error: {e}")
            return []

    async def search_experience(
        self, query: str, top_k: int = 4, username: Optional[str] = None
    ) -> List[RAGChunk]:
        """Semantic search with JSON keyword fallback — scoped per user."""
        if not query or not query.strip():
            return []

        try:
            self._ensure_initialized()
            query_embedding = await self._get_embeddings([query])
            where_filter = {"username": username} if username else None
            results = self._experience_collection.query(
                query_embeddings=query_embedding,
                n_results=top_k,
                where=where_filter,
                include=["documents", "metadatas", "distances"],
            )
            chunks: List[RAGChunk] = []
            if results["documents"] and results["documents"][0]:
                for i, doc in enumerate(results["documents"][0]):
                    meta = results["metadatas"][0][i] if results["metadatas"] else {}
                    distance = results["distances"][0][i] if results["distances"] else 0.0
                    chunks.append(RAGChunk(
                        text=doc,
                        document_id=meta.get("entry_id", ""),
                        chunk_index=0,
                        metadata=meta,
                        score=1.0 - distance,
                    ))
            if chunks:
                return chunks
        except Exception as e:
            logger.debug(f"Experience vector search failed, using JSON fallback: {e}")

        return self._keyword_search_experience(query, top_k, username)

    # === User conversation / personal memory (vector index, scoped by username) ===

    async def index_user_memory(
        self,
        username: str,
        conversation_id: str,
        message_id: str,
        kind: str,
        text: str,
    ) -> None:
        """Embed a finalized conversation message for later retrieval by user id."""
        if not text or len(text.strip()) < 4 or not username:
            return
        try:
            self._ensure_initialized()
            mem_id = f"{username}_{conversation_id}_{message_id or uuid.uuid4().hex[:8]}"
            embedding = await self._get_embeddings([text.strip()])
            self._user_memory_collection.upsert(
                ids=[mem_id],
                documents=[text.strip()],
                embeddings=embedding,
                metadatas=[{
                    "username": username,
                    "conversation_id": conversation_id,
                    "message_id": message_id or "",
                    "kind": kind,
                    "indexed_at": datetime.utcnow().isoformat(),
                }],
            )
        except Exception as e:
            logger.debug(f"User memory index skipped: {e}")

    async def search_user_memory(
        self, query: str, username: str, top_k: int = 4
    ) -> List[RAGChunk]:
        """Retrieve past conversation snippets relevant to a query for this user."""
        if not query or not username:
            return []
        try:
            self._ensure_initialized()
            query_embedding = await self._get_embeddings([query])
            results = self._user_memory_collection.query(
                query_embeddings=query_embedding,
                n_results=top_k,
                where={"username": username},
                include=["documents", "metadatas", "distances"],
            )
            chunks: List[RAGChunk] = []
            if results["documents"] and results["documents"][0]:
                for i, doc in enumerate(results["documents"][0]):
                    meta = results["metadatas"][0][i] if results["metadatas"] else {}
                    distance = results["distances"][0][i] if results["distances"] else 0.0
                    chunks.append(RAGChunk(
                        text=doc,
                        document_id=meta.get("message_id", ""),
                        chunk_index=0,
                        metadata=meta,
                        score=1.0 - distance,
                    ))
            return chunks
        except Exception as e:
            logger.debug(f"User memory search skipped: {e}")
            return []


def confidence_from_chunks(chunks: Optional[List] = None) -> float:
    """
    Derive a per-answer confidence score. Answers grounded in strongly matching
    experience/knowledge score highest; ungrounded answers still report a solid
    baseline (the assistant answers decisively from general knowledge).
    """
    base = 0.72
    if not chunks:
        return base
    try:
        top = max((c.score if hasattr(c, "score") else 0.0) for c in chunks)
    except ValueError:
        return base
    top = max(0.0, min(1.0, top))
    conf = 0.6 + 0.39 * top
    return round(min(0.99, max(base, conf)), 2)


# Global service instance
rag_service = RAGService()
