import { SseParser } from './sse';

export interface DocumentInfo {
  id: string;
  fileName: string;
  contentType: string;
  sizeBytes: number;
  status: 'Queued' | 'Processing' | 'Ready' | 'Failed';
  error: string | null;
  pageCount: number;
  chunkCount: number;
  uploadedAtUtc: string;
}

export interface Citation {
  number: number;
  documentId: string;
  documentName: string;
  pageNumber: number;
  snippet: string;
  score: number;
}

export interface ChatTurn {
  role: 'user' | 'assistant';
  content: string;
}

export interface AskHandlers {
  onSources(citations: Citation[], provider: string): void;
  onToken(text: string): void;
  onDone(answered: boolean): void;
  onError(message: string): void;
}

async function problem(res: Response): Promise<string> {
  try {
    const body = await res.json();
    return body.detail ?? body.title ?? `Request failed (${res.status})`;
  } catch {
    return `Request failed (${res.status})`;
  }
}

export const api = {
  async info(): Promise<{ provider: string; embeddings: string; indexedChunks: number }> {
    return (await fetch('/api/info')).json();
  },

  async documents(): Promise<DocumentInfo[]> {
    return (await fetch('/api/documents')).json();
  },

  async upload(file: File): Promise<DocumentInfo> {
    const form = new FormData();
    form.append('file', file);
    const res = await fetch('/api/documents', { method: 'POST', body: form });
    if (!res.ok) throw new Error(await problem(res));
    return res.json();
  },

  async remove(id: string): Promise<void> {
    await fetch(`/api/documents/${id}`, { method: 'DELETE' });
  },

  /** POSTs the question and consumes the Server-Sent Events stream as it arrives. */
  async ask(question: string, documentIds: string[], history: ChatTurn[], h: AskHandlers, signal: AbortSignal): Promise<void> {
    const res = await fetch('/api/ask', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ question, documentIds, history }),
      signal
    });
    if (!res.ok || !res.body) {
      h.onError(res.status === 429 ? 'Too many questions in a minute - please wait a moment.' : await problem(res));
      return;
    }

    const reader = res.body.pipeThrough(new TextDecoderStream()).getReader();
    const parser = new SseParser();
    for (;;) {
      const { value, done } = await reader.read();
      if (done) break;
      for (const msg of parser.push(value)) {
        const data = JSON.parse(msg.data);
        if (msg.event === 'sources') h.onSources(data.citations, data.provider);
        else if (msg.event === 'token') h.onToken(data.text);
        else if (msg.event === 'done') h.onDone(data.answered);
        else if (msg.event === 'error') h.onError(data.message);
      }
    }
  }
};
