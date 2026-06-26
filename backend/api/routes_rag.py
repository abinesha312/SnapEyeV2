"""
RAG (Retrieval-Augmented Generation) API Routes
Document upload, search, and knowledge base management.
"""
import logging
from typing import Optional

from fastapi import APIRouter, Depends, UploadFile, File, Form
from fastapi.responses import JSONResponse
from pydantic import BaseModel, Field

from security import auth_handler
from services.rag_service import rag_service

logger = logging.getLogger(__name__)

router = APIRouter(
    prefix="/api/rag",
    tags=["RAG Knowledge Base"],
)


class SearchRequest(BaseModel):
    """Request body for semantic search."""
    query: str = Field(..., description="Search query")
    top_k: int = Field(default=5, ge=1, le=20, description="Number of results")
    document_id: Optional[str] = Field(default=None, description="Filter by document ID")


class TextUploadRequest(BaseModel):
    """Request body for uploading plain text."""
    text: str = Field(..., description="Document text content")
    filename: str = Field(default="untitled.txt", description="Document name")
    doc_type: str = Field(default="text", description="Document type")


@router.post(
    "/upload",
    summary="Upload document to knowledge base",
    description="Upload a PDF, DOCX, or TXT file to the knowledge base. "
                "The document is chunked, embedded, and stored for semantic search.",
)
async def upload_document(
    file: UploadFile = File(...),
    user_data=Depends(auth_handler.verify_auth),
):
    """Upload and index a document file."""
    try:
        content = await file.read()
        filename = file.filename or "unknown"
        content_type = file.content_type or ""

        # Extract text based on file type
        text = ""
        doc_type = "text"

        if content_type == "application/pdf" or filename.endswith(".pdf"):
            text = _extract_pdf_text(content)
            doc_type = "pdf"
        elif (content_type == "application/vnd.openxmlformats-officedocument.wordprocessingml.document"
              or filename.endswith(".docx")):
            text = _extract_docx_text(content)
            doc_type = "docx"
        elif content_type.startswith("text/") or filename.endswith((".txt", ".md", ".csv")):
            text = content.decode("utf-8", errors="ignore")
            doc_type = "text"
        else:
            return JSONResponse(
                status_code=400,
                content={"error": f"Unsupported file type: {content_type}"},
            )

        if not text.strip():
            return JSONResponse(
                status_code=400,
                content={"error": "No text could be extracted from the document"},
            )

        result = await rag_service.add_document(
            text=text,
            filename=filename,
            doc_type=doc_type,
        )

        return JSONResponse(content=result)

    except Exception as e:
        logger.exception(f"Document upload error: {e}")
        return JSONResponse(
            status_code=500,
            content={"error": str(e)},
        )


@router.post(
    "/upload-text",
    summary="Upload plain text to knowledge base",
    description="Upload plain text directly (no file) to the knowledge base.",
)
async def upload_text(
    request: TextUploadRequest,
    user_data=Depends(auth_handler.verify_auth),
):
    """Upload plain text and index it."""
    try:
        result = await rag_service.add_document(
            text=request.text,
            filename=request.filename,
            doc_type=request.doc_type,
        )
        return JSONResponse(content=result)
    except Exception as e:
        logger.exception(f"Text upload error: {e}")
        return JSONResponse(status_code=500, content={"error": str(e)})


@router.post(
    "/search",
    summary="Semantic search over knowledge base",
    description="Search the knowledge base using semantic similarity.",
)
async def search_knowledge_base(
    request: SearchRequest,
    user_data=Depends(auth_handler.verify_auth),
):
    """Perform semantic search over uploaded documents."""
    try:
        chunks = await rag_service.search(
            query=request.query,
            top_k=request.top_k,
            filter_doc_id=request.document_id,
        )

        return JSONResponse(content={
            "query": request.query,
            "results": [c.to_dict() for c in chunks],
            "total": len(chunks),
        })

    except Exception as e:
        logger.exception(f"RAG search error: {e}")
        return JSONResponse(status_code=500, content={"error": str(e)})


@router.get(
    "/documents",
    summary="List uploaded documents",
    description="Get a list of all documents in the knowledge base.",
)
async def list_documents(user_data=Depends(auth_handler.verify_auth)):
    """List all uploaded documents."""
    docs = rag_service.list_documents()
    return JSONResponse(content={"documents": docs, "total": len(docs)})


@router.delete(
    "/documents/{document_id}",
    summary="Delete a document",
    description="Remove a document and all its chunks from the knowledge base.",
)
async def delete_document(
    document_id: str,
    user_data=Depends(auth_handler.verify_auth),
):
    """Delete a document from the knowledge base."""
    try:
        success = await rag_service.delete_document(document_id)
        if success:
            return JSONResponse(content={"deleted": True, "document_id": document_id})
        return JSONResponse(
            status_code=404,
            content={"error": "Document not found"},
        )
    except Exception as e:
        logger.exception(f"Delete document error: {e}")
        return JSONResponse(status_code=500, content={"error": str(e)})


def _extract_pdf_text(content: bytes) -> str:
    """Extract text from a PDF file."""
    try:
        from PyPDF2 import PdfReader
        import io

        reader = PdfReader(io.BytesIO(content))
        text_parts = []
        for page in reader.pages:
            text_parts.append(page.extract_text() or "")
        return "\n\n".join(text_parts)
    except Exception as e:
        logger.error(f"PDF extraction error: {e}")
        return ""


def _extract_docx_text(content: bytes) -> str:
    """Extract text from a DOCX file."""
    try:
        from docx import Document
        import io

        doc = Document(io.BytesIO(content))
        text_parts = []
        for paragraph in doc.paragraphs:
            if paragraph.text.strip():
                text_parts.append(paragraph.text)
        return "\n\n".join(text_parts)
    except Exception as e:
        logger.error(f"DOCX extraction error: {e}")
        return ""
