# Design decisions

## 1. Retrieval-augmented generation, with a relevance gate

The model only sees passages retrieved from the user's own files, numbered so it can cite them. Before any model call,
a **relevance gate** checks that at least one passage shares a keyword with the question or clears a similarity threshold.
If none does, the answer is "not found" and no model is called. That one rule removes the most common failure of document
chatbots: confidently answering from general knowledge when the documents say nothing.

## 2. Hybrid search: BM25 + vectors, fused with RRF

- **BM25** is excellent at exact terms: policy names, product codes, figures like "INR 40,000".
- **Vector similarity** handles paraphrase ("time off" vs "leave").
- **Reciprocal Rank Fusion** (`score = Σ 1/(60 + rank)`) combines the two rankings using ranks only, so there's no need to
  calibrate BM25 scores against cosine similarities, which live on different scales.

Each list contributes its top 50 candidates; the fused top-k (default 6) goes to the model.

## 3. Page-aware, sentence-aligned chunking

PDFs are extracted page by page (PdfPig's content-order extractor keeps reading order), and chunks never cross a page boundary,
so every citation maps to exactly one page. Chunks target ~900 characters, end on sentence boundaries, and carry ~150 characters of
overlap so an answer straddling a cut is still retrievable. Markdown headings become their own sentences, so a heading doesn't get
glued onto the first sentence of the section.

## 4. An in-memory index (for now)

Chunks and vectors are persisted in SQLite, and the searchable index lives in memory as an **immutable snapshot**. Writers build a new
snapshot and swap the reference, so searches never lock and never see a half-built index. This is simple and fast up to a few hundred
thousand chunks on one node. Past that, or with multiple app instances, the `HybridIndex` would be replaced by a vector database
(pgvector, Azure AI Search, Qdrant). The retrieval contract (`Search(query, vector, topK, scope)`) stays the same.

## 5. Three answer providers behind one interface

`IAnswerGenerator.StreamAsync(...)` returns text deltas, so every provider streams the same way to the UI.

- **Extractive** (default) quotes the highest-overlap sentences across sources with citations. It needs no model, works offline,
  and makes the end-to-end tests deterministic.
- **Ollama** keeps everything local and private, which suits sensitive documents.
- **Claude** gives the best answers. It uses the official Anthropic C# SDK with streaming, explicit effort, and server-side refusal
  fallback, and it surfaces `refusal` / `max_tokens` stop reasons to the user instead of silently truncating.

## 6. Embedding model changes are detected

Every document records which embedder produced its vectors (`hashing-512`, `ollama-nomic-embed-text`...). Vectors from different
models aren't comparable, so on startup any document embedded with a different model is re-queued and re-indexed automatically.

## 7. Server-Sent Events over a POST

The answer streams as SSE events: `sources` first (so citations render immediately), then `token`s, then `done` or `error`.
The browser's `EventSource` can't POST a body, so the UI reads the `fetch` response stream with a small incremental SSE parser
(unit-tested for events split across network chunks). A provider failure mid-answer becomes an `error` event rather than a broken stream.

## 8. Uploads are treated as untrusted

Allowed extensions only, a PDF magic-byte check, size limits enforced by Kestrel and form options, generated file names on disk,
SHA-256 de-duplication, and a system prompt that tells the model to ignore instructions embedded in document text.
`/api/ask` is rate-limited per IP to protect a paid model API.
