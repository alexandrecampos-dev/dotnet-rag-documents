# dotnet-rag-documents

Pipeline **RAG (Retrieval-Augmented Generation)** completo em **.NET 9** — ingere documentos, armazena vetores no PostgreSQL com pgvector e responde perguntas com fontes citadas.

[![.NET](https://img.shields.io/badge/.NET-9.0-512BD4?style=flat-square&logo=dotnet)](https://dotnet.microsoft.com)
[![PostgreSQL](https://img.shields.io/badge/PostgreSQL-pgvector-316192?style=flat-square&logo=postgresql)](https://github.com/pgvector/pgvector)
[![Azure OpenAI](https://img.shields.io/badge/Azure_OpenAI-compatível-0078D4?style=flat-square&logo=microsoftazure)](https://azure.microsoft.com/products/ai-services/openai-service)
[![Ollama](https://img.shields.io/badge/Ollama-compatível-000000?style=flat-square)](https://ollama.com)
[![Licença](https://img.shields.io/badge/Licença-MIT-green?style=flat-square)](LICENSE)

---

## O que este projeto demonstra

A maioria dos exemplos de RAG está em Python. Este projeto implementa o mesmo pipeline em **.NET idiomático** — os mesmos padrões usados em sistemas de produção, sem frameworks de orquestração de IA como LangChain.

| Característica | Detalhe |
|---------------|---------|
| Chunking | Janela deslizante com sobreposição configurável |
| Embeddings | Azure OpenAI (`text-embedding-ada-002`) ou Ollama (`nomic-embed-text`) — troca por configuração, sem alterar código |
| Armazenamento vetorial | PostgreSQL + pgvector — sem infraestrutura adicional se você já usa Postgres |
| Geração de resposta | Azure OpenAI GPT-4o com fontes citadas |
| Arquitetura | Clean Architecture + CQRS com MediatR |

---

## Arquitetura

```
dotnet-rag-documents/
└── src/
    ├── RagDocuments.Domain/          # Entidades: Document, DocumentChunk
    ├── RagDocuments.Application/     # Handlers CQRS, interfaces de serviço
    ├── RagDocuments.Infrastructure/  # EF Core, pgvector, Azure OpenAI, Ollama
    └── RagDocuments.API/             # ASP.NET Core minimal API — 3 endpoints
```

Regra de dependência: `API -> Application -> Domain <- Infrastructure`

### Fluxo do pipeline

```
POST /documentos          — Ingestão
  1. Recebe o conteúdo do documento
  2. Divide em chunks com sobreposição (sliding window)
  3. Gera embeddings em lote
  4. Persiste documento + chunks + vetores no pgvector

POST /consulta            — Consulta RAG
  1. Gera embedding da pergunta
  2. Busca por similaridade de cosseno nos chunks (top-K)
  3. Monta janela de contexto com os resultados
  4. Envia contexto + pergunta para o LLM
  5. Retorna resposta + fontes citadas
```

### Provedores de embedding

| Provedor | Modelo | Dimensões | Valor de config |
|----------|--------|-----------|----------------|
| Azure OpenAI | text-embedding-ada-002 | 1536 | `AzureOpenAI` |
| Ollama (local) | nomic-embed-text | 768 | `Ollama` |

---

## Pré-requisitos

- [.NET 9 SDK](https://dotnet.microsoft.com/download)
- PostgreSQL com a extensão pgvector instalada

```sql
CREATE EXTENSION IF NOT EXISTS vector;
```

---

## Início rápido

```bash
# 1. Clone o repositório
git clone https://github.com/alexandrecampos-dev/dotnet-rag-documents.git
cd dotnet-rag-documents

# 2. Configure a connection string e o provedor de IA em:
#    src/RagDocuments.API/appsettings.json

# 3. Execute — as migrations são aplicadas automaticamente na inicialização
cd src/RagDocuments.API && dotnet run
```

API disponível em `http://localhost:5000`  
OpenAPI em `http://localhost:5000/openapi/v1.json`

---

## Referência da API

### Ingerir um documento

```http
POST /documentos
Content-Type: application/json

{
  "nome": "Clean Architecture",
  "fonte": "Robert C. Martin, 2017",
  "tipoDe": "text/plain",
  "conteudo": "O objetivo da arquitetura de software é minimizar os recursos humanos necessários para construir e manter o sistema..."
}
```

Resposta `201 Created`:

```json
{
  "documentoId": "3fa85f64-5717-4562-b3fc-2c963f66afa6",
  "chunksGerados": 12
}
```

### Consultar os documentos

```http
POST /consulta
Content-Type: application/json

{
  "pergunta": "Qual é o principal objetivo da arquitetura de software?",
  "topK": 5
}
```

Resposta `200 OK`:

```json
{
  "resposta": "De acordo com o contexto fornecido, o principal objetivo da arquitetura de software é minimizar os recursos humanos necessários para construir e manter o sistema.",
  "fontes": [
    {
      "documentoId": "3fa85f64-5717-4562-b3fc-2c963f66afa6",
      "nomeDocumento": "Clean Architecture",
      "trecho": "O objetivo da arquitetura de software é minimizar os recursos humanos...",
      "score": 0.97
    }
  ]
}
```

### Listar documentos ingeridos

```http
GET /documentos
```

---

## Configuração

| Chave | Padrão | Descrição |
|-------|--------|-----------|
| `AI:EmbeddingProvider` | `AzureOpenAI` | `AzureOpenAI` ou `Ollama` |
| `AzureOpenAI:EmbeddingDeployment` | `text-embedding-ada-002` | Nome do deployment de embeddings no Azure |
| `AzureOpenAI:ChatDeployment` | `gpt-4o` | Nome do deployment de chat no Azure |
| `AzureOpenAI:EmbeddingDimensions` | `1536` | Deve corresponder ao modelo utilizado |
| `Ollama:BaseUrl` | `http://localhost:11434` | Instância local do Ollama |
| `Ollama:EmbeddingModel` | `nomic-embed-text` | Modelo de embeddings no Ollama |
| `Ollama:EmbeddingDimensions` | `768` | Deve corresponder ao modelo utilizado |
| `Chunking:ChunkSize` | `300` | Palavras por chunk |
| `Chunking:OverlapSize` | `50` | Palavras compartilhadas entre chunks consecutivos |

### Executando com Ollama (local, sem chave de API)

```bash
ollama pull nomic-embed-text
ollama pull llama3
```

Em `appsettings.json`, defina:

```json
{
  "AI": { "EmbeddingProvider": "Ollama" }
}
```

> Ao trocar de provedor de embeddings, re-ingira os documentos. Vetores de modelos diferentes não são compatíveis entre si.

---

## Stack

| Tecnologia | Finalidade |
|-----------|-----------|
| .NET 9 | Runtime |
| ASP.NET Core | Host HTTP, minimal API |
| MediatR | Pipeline CQRS |
| Entity Framework Core | ORM e migrations |
| Npgsql + pgvector | Operações vetoriais no PostgreSQL |
| Azure.AI.OpenAI | Embeddings e completions |

---

## Autor

**Alexandre Campos** — Software Engineer & AI Engineer  
[LinkedIn](https://www.linkedin.com/in/alexandre-campos-3725b18b/) · [GitHub](https://github.com/alexandrecampos-dev)

---

MIT License
