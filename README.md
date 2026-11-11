# dotnet-rag-documents

Pipeline **RAG (Retrieval-Augmented Generation)** em **.NET 10**: ingere documentos, busca de forma híbrida no PostgreSQL (pgvector + busca textual) e responde com **citações verificáveis**. Os provedores de modelo são trocados por configuração (Ollama, OpenAI, Azure OpenAI, Groq), e tudo roda 100% local com um `docker compose up`.

[![CI](https://github.com/alexandrecampos-dev/dotnet-rag-documents/actions/workflows/ci.yml/badge.svg)](https://github.com/alexandrecampos-dev/dotnet-rag-documents/actions/workflows/ci.yml)
[![.NET](https://img.shields.io/badge/.NET-10-512BD4?style=flat-square&logo=dotnet)](https://dotnet.microsoft.com)
[![PostgreSQL](https://img.shields.io/badge/PostgreSQL-pgvector-316192?style=flat-square&logo=postgresql)](https://github.com/pgvector/pgvector)
[![Microsoft.Extensions.AI](https://img.shields.io/badge/Microsoft.Extensions.AI-10-512BD4?style=flat-square)](https://learn.microsoft.com/dotnet/ai/microsoft-extensions-ai)
[![Licença](https://img.shields.io/badge/Licença-MIT-green?style=flat-square)](LICENSE)

---

## O que este projeto demonstra

| Decisão | Como está aqui | Onde no código |
|---|---|---|
| **Um contrato, vários provedores** | A aplicação só conhece duas operações: gerar (`IChatClient`) e embutir (`IEmbeddingGenerator`), do Microsoft.Extensions.AI. Qual provedor entra é configuração. Chat e embedding são independentes: dá para gerar com Groq e embutir com Ollama. | [`Provedores.cs`](src/RagDocuments.Api/Provedores.cs) |
| **Fatiamento que respeita o texto** | Corta por seção, depois por parágrafo, e só quebra parágrafo longo em fim de frase, com sobreposição de frases inteiras. O título da seção entra no embedding: "o prazo é de 30 dias" sozinho não diz prazo de quê. | [`Fatiador.cs`](src/RagDocuments.Core/Ingestao/Fatiador.cs) |
| **Busca híbrida** | Vetorial por cosseno (índice HNSW) e textual do PostgreSQL (índice GIN), fundidas por Reciprocal Rank Fusion numa consulta só. O vetor acha pelo significado; o texto acha sigla, código e nome próprio que o embedding borra. | [`BancoVetorial.cs`](src/RagDocuments.Core/Armazenamento/BancoVetorial.cs) |
| **Saber dizer "não sei", em camadas** | Sem trecho relevante (similaridade abaixo do limiar e cobertura textual baixa), a resposta é "não encontrei" **sem chamar o modelo**: não custa token nem convida a inventar. O que passa do limiar ainda encontra a regra do prompt, que manda responder "não encontrei" quando os documentos não respondem. | [`RespostaService.cs`](src/RagDocuments.Core/Respostas/RespostaService.cs) |
| **Citação verificável** | O modelo precisa citar `[n]`. Cada número é conferido contra as fontes que foram de fato enviadas; `[9]` com 5 fontes é descartado. Resposta sem citação válida volta marcada como `sem_citacao`. | [`RespostaService.cs`](src/RagDocuments.Core/Respostas/RespostaService.cs) |
| **Documento é dado, não instrução** | As regras ficam na mensagem de sistema e os documentos vão delimitados na do usuário. Um documento que tente fechar o delimitador ou mandar "ignore as instruções" é neutralizado antes de chegar ao modelo. | [`Prompt.cs`](src/RagDocuments.Core/Respostas/Prompt.cs) |
| **Trava de modelo de embedding** | O modelo usado na indexação fica gravado no banco. Subir com outro modelo é recusado com mensagem clara, em vez de produzir buscas silenciosamente erradas. | [`BancoVetorial.cs`](src/RagDocuments.Core/Armazenamento/BancoVetorial.cs) |
| **Ingestão idempotente** | Mesmo nome e mesmo conteúdo: nada acontece e nenhum embedding é gerado. Conteúdo novo: o documento inteiro é substituído numa transação. | [`IngestaoService.cs`](src/RagDocuments.Core/Ingestao/IngestaoService.cs) |
| **Observabilidade desde o dia um** | Spans OpenTelemetry por etapa (ingestão, busca, geração) e, das chamadas ao modelo, tokens e latência nas convenções de GenAI. Cada resposta também devolve tokens e tempo de busca e de geração. | [`Program.cs`](src/RagDocuments.Api/Program.cs) |
| **Medir antes de confiar** | Um conjunto de perguntas com o documento esperado (e perguntas fora do escopo). Roda no CI com embedding determinístico e, contra o modelo real, com `dotnet run -- avaliar`. | [`evals/perguntas.json`](evals/perguntas.json) |

---

## Como usar

Pré-requisitos: [.NET 10 SDK](https://dotnet.microsoft.com/download) e Docker.

```bash
git clone https://github.com/alexandrecampos-dev/dotnet-rag-documents.git
cd dotnet-rag-documents

docker compose up -d                                         # PostgreSQL + pgvector e Ollama (baixa ~2 GB de modelos na primeira vez)
dotnet run --project src/RagDocuments.Api -- ingerir exemplos   # indexa os documentos de exemplo
dotnet run --project src/RagDocuments.Api                       # API em http://localhost:5200
```

```bash
curl -s http://localhost:5200/perguntas -H "Content-Type: application/json" \
  -d '{"pergunta":"Em quantas partes posso dividir minhas férias?"}'
```

Resposta real, com `qwen2.5:3b` gerando e `nomic-embed-text` embutindo, rodando em CPU:

```json
{
  "texto": "Você pode dividir suas férias em até três períodos. [1]",
  "situacao": "respondida",
  "fontes": [
    {
      "numero": 1,
      "documento": "politica-de-ferias.md",
      "secao": "Fracionamento",
      "trecho": "As férias podem ser divididas em até três períodos. Um deles precisa ter pelo menos 14 dia…",
      "similaridade": 0.735
    }
  ],
  "trechosConsultados": 5,
  "uso": { "tokensDeEntrada": 646, "tokensDeSaida": 18, "msBusca": 162, "msGeracao": 1474 }
}
```

E uma pergunta fora do escopo, que não chega ao modelo (zero token):

```json
{ "texto": "Não encontrei essa informação nos documentos.", "situacao": "nao_encontrada", "fontes": [],
  "trechosConsultados": 0, "uso": { "tokensDeEntrada": 0, "tokensDeSaida": 0, "msBusca": 147, "msGeracao": 0 } }
```

### API

| Método | Rota | O que faz |
|---|---|---|
| `POST` | `/perguntas` | Responde com texto, situação (`respondida`, `nao_encontrada`, `sem_citacao`), fontes citadas e uso (tokens e tempos). |
| `POST` | `/documentos` | Ingere `{ "nome", "conteudo", "origem" }`. |
| `POST` | `/documentos/arquivo` | Ingere um `.md` ou `.txt` enviado como formulário. |
| `GET` | `/documentos` | Lista os documentos indexados. |
| `DELETE` | `/documentos/{id}` | Remove um documento e seus trechos. |

---

## Trocar de provedor

Chat e embedding têm seções próprias (`Ia:Chat` e `Ia:Embedding`). Os tipos são `Ollama` e `OpenAICompativel`, que serve para OpenAI, Azure OpenAI e Groq, porque os três falam a mesma API. A chave vai por variável de ambiente ou user-secrets, nunca no `appsettings.json`.

```bash
# Gerar com OpenAI, embutir local com Ollama
export Ia__Chat__Tipo=OpenAICompativel
export Ia__Chat__Endpoint=https://api.openai.com/v1
export Ia__Chat__Modelo=gpt-4.1-mini
export Ia__Chat__ChaveDeApi=sk-...

# Azure OpenAI: o mesmo tipo, com o endpoint v1 do recurso
export Ia__Chat__Endpoint=https://SEU-RECURSO.openai.azure.com/openai/v1/

# Groq (só chat; o Groq não oferece embeddings)
export Ia__Chat__Endpoint=https://api.groq.com/openai/v1
```

Trocar o **modelo de embedding** muda o espaço vetorial: ajuste `Rag:ModeloDeEmbedding`, `Rag:DimensoesDoEmbedding` e os prefixos de tarefa (`Rag:PrefixoDaPergunta` e `Rag:PrefixoDoDocumento`, que o `nomic-embed-text` exige e os modelos da OpenAI não usam), e reindexe num banco novo. Se esquecer, a aplicação recusa a subida e explica por quê.

---

## Avaliação

```bash
dotnet run --project src/RagDocuments.Api -- avaliar evals/perguntas.json
```

Para cada pergunta, o comando mostra a melhor similaridade e a melhor cobertura textual encontradas (é com esses números que se calibra `Rag:SimilaridadeMinima` para o modelo em uso), a situação e a resposta. No fim, três indicadores: documento certo citado, respostas com citação válida e recusa correta das perguntas fora do escopo.

Resultado medido com a configuração padrão (`qwen2.5:3b` + `nomic-embed-text`, limiar 0,62), sobre os documentos de `exemplos/`:

| Indicador | Resultado |
|---|---|
| Documento certo citado | 11 de 11 |
| Respostas com citação válida | 11 de 11 |
| Recusa das perguntas fora do escopo | 3 de 3 |

O que os números mostraram, e que vale para qualquer RAG:

- **O limiar sozinho não separa o escopo.** Com o `nomic-embed-text` em português, a menor similaridade de uma pergunta do escopo foi 0,66 e a maior de uma pergunta fora dele foi 0,69 ("qual a cotação do dólar hoje?"). Os prefixos de tarefa que o modelo pede (`search_query:` e `search_document:`) não mudaram isso. Por isso a recusa é em camadas: o limiar de 0,62 corta o que está claramente longe sem chamar o modelo ("Copa do Mundo" ficou em 0,57; "bolo de cenoura", 0,60), e o que passa é recusado pela regra do prompt. Para português, vale testar um embedding multilíngue e recalibrar com o mesmo comando.
- **Modelo pequeno varia entre execuções.** Com o mesmo prompt, uma de quatro rodadas teve 10 de 11 citações válidas; a resposta sem citação voltou marcada como `sem_citacao`, que é para isso que a marcação existe.
- **Nem toda mudança de prompt ajuda.** Repetir a regra de citação no fim da mensagem, para lembrar o modelo, piorou o resultado: ele passou a recusar uma pergunta que sabia responder. A mudança foi descartada, e só se sabe disso porque havia um eval para medir.

---

## Testes

```bash
dotnet test
```

Os testes de integração sobem um **PostgreSQL com pgvector de verdade** (Testcontainers) e usam um embedding determinístico no lugar do modelo, para rodar no CI sem chave e sem rede:

- **Ingestão:** reingerir o mesmo conteúdo não gasta embedding; conteúdo novo substitui; trocar de modelo de embedding é recusado.
- **Resposta:** pergunta fora do escopo não chama o modelo; busca textual acha a sigla `CONF-3`; citação inexistente é descartada; resposta sem citação é marcada; documento malicioso chega neutralizado.
- **Eval de recuperação:** todas as perguntas de `evals/perguntas.json` trazem o documento certo, e as fora do escopo não trazem nada.
- **API:** ingerir e perguntar pela API; corpo inválido responde 400, não 500; pergunta vazia não chama o modelo.
- **Unidade:** fatiamento por seção e frase com sobreposição, delimitação do prompt e leitura de citações.

---

## Estrutura

```
src/
  RagDocuments.Core/   fatiamento, ingestão, busca híbrida (SQL explícito), prompt e citações
  RagDocuments.Api/    minimal API, provedores por configuração, OpenTelemetry e comandos ingerir/avaliar
tests/
  RagDocuments.Tests/  unidade e integração com PostgreSQL + pgvector em container
exemplos/              documentos fictícios para testar
evals/                 perguntas com documento esperado
```

## Para levar a produção

- Reranking com cross-encoder entre a busca e a geração, quando o acervo crescer.
- Fatiamento por tokens do modelo de embedding, em vez de caracteres.
- Autenticação nos endpoints, controle de acesso por documento e fila para ingestão de arquivos grandes.
- Calibrar `SimilaridadeMinima` e `CoberturaTextualMinima` com perguntas reais do seu domínio, usando o comando `avaliar`.

## Histórico

- **2.0.0:** busca híbrida, limiar de relevância, citações verificáveis, defesa contra instrução em documento, provedores via Microsoft.Extensions.AI (Ollama, OpenAI, Azure OpenAI, Groq), trava de modelo de embedding, ingestão idempotente, OpenTelemetry, evals, testes com PostgreSQL real e `docker compose`. Sai o EF Core e o MediatR.
- **1.0.0** ([tag](https://github.com/alexandrecampos-dev/dotnet-rag-documents/tree/v1.0.0)): não subia do zero (chamava `Migrate()` sem migrações no repositório), a troca para Ollama quebrava (coluna fixa em 1.536 dimensões) e a geração só existia para Azure OpenAI.

## Licença

[MIT](LICENSE)
