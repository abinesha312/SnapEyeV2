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
        self._initialized = False
        self._documents: Dict[str, Dict] = {}  # track uploaded docs

    def _ensure_initialized(self):
        """Lazy initialization of ChromaDB."""
        if self._initialized:
            return

        try:
            import chromadb

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
            self._initialized = True
            logger.info(
                f"RAG service initialized (knowledge_base: {self._collection.count()} docs, "
                f"transcripts: {self._transcript_collection.count()} segments)"
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
    ) -> List[RAGChunk]:
        """
        Semantic search over the knowledge base.

        Args:
            query: Search query text
            top_k: Number of results to return
            filter_doc_id: Optional document ID to filter by

        Returns list of RAGChunk objects sorted by relevance.
        """
        self._ensure_initialized()
        top_k = top_k or settings.RAG_TOP_K

        query_embedding = await self._get_embeddings([query])

        where_filter = None
        if filter_doc_id:
            where_filter = {"document_id": filter_doc_id}

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

    async def add_transcript_segment(
        self,
        text: str,
        session_id: str,
        source: str = "microphone",
        message_id: str = "",
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
                "indexed_at": datetime.utcnow().isoformat(),
            }],
        )

    async def search_transcripts(
        self,
        query: str,
        top_k: int = 5,
        session_id: Optional[str] = None,
    ) -> List[RAGChunk]:
        """Search over indexed transcript segments."""
        self._ensure_initialized()

        query_embedding = await self._get_embeddings([query])

        where_filter = None
        if session_id:
            where_filter = {"session_id": session_id}

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


# Global service instance
rag_service = RAGService()
