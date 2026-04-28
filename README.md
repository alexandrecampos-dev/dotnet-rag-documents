# dotnet-rag-documents

A production-ready **Retrieval-Augmented Generation (RAG)** pipeline built with **.NET 9** — document ingestion, vector storage in PostgreSQL with pgvector, and LLM-generated answers with cited sources.

[![.NET](https://img.shields.io/badge/.NET-9.0-512BD4?style=flat-square&logo=dotnet)](https://dotnet.microsoft.com)
[![PostgreSQL](https://img.shields.io/badge/PostgreSQL-pgvector-316192?style=flat-square&logo=postgresql)](https://github.com/pgvector/pgvector)
[![Azure OpenAI](https://img.shields.io/badge/Azure_OpenAI-compatible-0078D4?style=flat-square&logo=microsoftazure)](https://azure.microsoft.com/products/ai-services/openai-service)
[![Ollama](https://img.shields.io/badge/Ollama-compatible-000000?style=flat-square)](https://ollama.com)
[![Architecture](https://img.shields.io/badge/Architecture-Clean_Architecture-00C7B7?style=flat-square)]()
[![License](https://img.shields.io/badge/License-MIT-green?style=flat-square)](LICENSE)

---

## What This Project Demonstrates

Most RAG examples are written in Python. This project shows the same pipeline implemented in idiomatic .NET 9:

- Document ingestion with sliding-window chunking and configurable overlap
- Embedding generation via Azure OpenAI (`text-embedding-ada-002`) or Ollama (`nomic-embed-text`) — switched by config, no code change
- Vector storage and cosine similarity search using PostgreSQL + pgvector — no extra infrastructure required if you already run Postgres
- Answer generation with source attribution via Azure OpenAI GPT-4o
- Clean Architecture with CQRS and MediatR — the same patterns used in production .NET systems

---

## Architecture

```
dotnet-rag-documents/
└── src/
    ├── RagDocuments.Domain/          # Entities: Document, DocumentChunk
    ├── RagDocuments.Application/     # CQRS handlers, service interfaces
    ├── RagDocuments.Infrastructure/  # EF Core, pgvector, Azure OpenAI, Ollama
    └── RagDocuments.API/             # ASP.NET Core minimal API, 3 endpoints
```

Dependency rule: `API -> Application -> Domain <- Infrastructure`

### RAG Pipeline

```
POST /documents
  1. Receive document content
  2. Split into overlapping chunks (sliding window)
  3. Generate embeddings for each chunk (batch)
  4. Persist document + chunks with embeddings in pgvector

POST /query
  1. Embed the question
  2. Run cosine similarity search against all chunks
  3. Build a context window from the top-K results
  4. Send context + question to the LLM
  5. Return answer + cited sources
```

### Embedding Providers

| Provider | Model | Dimensions | Config value |
|----------|-------|-----------|--------------|
| Azure OpenAI | text-embedding-ada-002 | 1536 | `AzureOpenAI` |
| Ollama (local) | nomic-embed-text | 768 | `Ollama` |

Switch providers by changing `AI:EmbeddingProvider` in `appsettings.json`. No code change required.

---

## Prerequisites

- [.NET 9 SDK](https://dotnet.microsoft.com/download)
- PostgreSQL with pgvector extension installed

Enable pgvector in your database:

```sql
CREATE EXTENSION IF NOT EXISTS vector;
```

---

## Getting Started

```bash
git clone https://github.com/alexandrecampos-dev/dotnet-rag-documents.git
cd dotnet-rag-documents
```

Configure your connection string and AI provider in `src/RagDocuments.API/appsettings.json`:

```json
{
  "ConnectionStrings": {
    "DefaultConnection": "Host=localhost;Database=rag_documents;Username=postgres;Password=your_password"
  },
  "AI": {
    "EmbeddingProvider": "AzureOpenAI"
  },
  "AzureOpenAI": {
    "Endpoint": "https://your-resource.openai.azure.com/",
    "ApiKey": "your-api-key",
    "EmbeddingDeployment": "text-embedding-ada-002",
    "ChatDeployment": "gpt-4o"
  }
}
```

Run:

```bash
dotnet restore
cd src/RagDocuments.API
dotnet run
```

The API runs migrations automatically on startup. OpenAPI available at `http://localhost:5000/openapi/v1.json`.

---

## API Reference

### Ingest a document

```http
POST /documents
Content-Type: application/json

{
  "name": "Clean Architecture",
  "source": "Robert C. Martin, 2017",
  "contentType": "text/plain",
  "content": "The goal of software architecture is to minimize the human resources required to build and maintain the required system..."
}
```

Response `201 Created`:

```json
{
  "documentId": "3fa85f64-5717-4562-b3fc-2c963f66afa6",
  "chunksCreated": 12
}
```

### Query your documents

```http
POST /query
Content-Type: application/json

{
  "question": "What is the main goal of software architecture?",
  "topK": 5
}
```

Response `200 OK`:

```json
{
  "answer": "According to the provided context, the main goal of software architecture is to minimize the human resources required to build and maintain the required system.",
  "sources": [
    {
      "documentId": "3fa85f64-5717-4562-b3fc-2c963f66afa6",
      "documentName": "Clean Architecture",
      "excerpt": "The goal of software architecture is to minimize the human resources required...",
      "score": 0.97
    }
  ]
}
```

### List ingested documents

```http
GET /documents
```

---

## Configuration Reference

| Key | Default | Description |
|-----|---------|-------------|
| `AI:EmbeddingProvider` | `AzureOpenAI` | `AzureOpenAI` or `Ollama` |
| `AzureOpenAI:EmbeddingDeployment` | `text-embedding-ada-002` | Azure OpenAI embedding model deployment name |
| `AzureOpenAI:ChatDeployment` | `gpt-4o` | Azure OpenAI chat model deployment name |
| `AzureOpenAI:EmbeddingDimensions` | `1536` | Must match the model output |
| `Ollama:BaseUrl` | `http://localhost:11434` | Local Ollama instance |
| `Ollama:EmbeddingModel` | `nomic-embed-text` | Ollama model for embeddings |
| `Ollama:EmbeddingDimensions` | `768` | Must match the model output |
| `Chunking:ChunkSize` | `300` | Words per chunk |
| `Chunking:OverlapSize` | `50` | Words shared between consecutive chunks |

---

## Running with Ollama (local, no API key required)

1. Install [Ollama](https://ollama.com) and pull the required models:

```bash
ollama pull nomic-embed-text
ollama pull llama3
```

2. Update `appsettings.json`:

```json
{
  "AI": { "EmbeddingProvider": "Ollama" },
  "AzureOpenAI": { "EmbeddingDimensions": 768 }
}
```

Note: when switching embedding providers, re-ingest your documents — embeddings are not interchangeable across models.

---

## Tech Stack

| Technology | Purpose |
|-----------|---------|
| .NET 9 | Runtime |
| ASP.NET Core | HTTP host, minimal API |
| MediatR | CQRS mediator pipeline |
| Entity Framework Core | ORM, migrations |
| Npgsql + pgvector | PostgreSQL vector operations |
| Azure.AI.OpenAI | Embeddings and chat completions |
| FluentValidation | Input validation |

---

## References

- [pgvector](https://github.com/pgvector/pgvector)
- [Pgvector.EntityFrameworkCore](https://github.com/pgvector/pgvector-dotnet)
- [Azure OpenAI Service](https://learn.microsoft.com/azure/ai-services/openai/)
- [Ollama](https://ollama.com)
- [Clean Architecture](https://blog.cleancoder.com/uncle-bob/2012/08/13/the-Clean-Architecture.html)

---

## Author

**Alexandre Campos** — Software Engineer & AI Engineer  
[LinkedIn](https://www.linkedin.com/in/alexandre-campos-3725b18b/) · [GitHub](https://github.com/alexandrecampos-dev)

---

MIT License
