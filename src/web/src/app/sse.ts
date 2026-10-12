/** One parsed Server-Sent Event. */
export interface SseMessage {
  event: string;
  data: string;
}

/**
 * Incremental SSE parser for fetch() streams (EventSource can't POST a body).
 * Feed it decoded text chunks as they arrive; it returns the complete events seen so far
 * and keeps any partial event buffered until the rest arrives.
 */
export class SseParser {
  private buffer = '';

  push(chunk: string): SseMessage[] {
    this.buffer += chunk.replace(/\r\n/g, '\n');
    const messages: SseMessage[] = [];
    let boundary: number;
    while ((boundary = this.buffer.indexOf('\n\n')) >= 0) {
      const block = this.buffer.slice(0, boundary);
      this.buffer = this.buffer.slice(boundary + 2);
      let event = 'message';
      const data: string[] = [];
      for (const line of block.split('\n')) {
        if (line.startsWith('event:')) event = line.slice(6).trim();
        else if (line.startsWith('data:')) data.push(line.slice(5).replace(/^ /, ''));
      }
      if (data.length) messages.push({ event, data: data.join('\n') });
    }
    return messages;
  }
}

export type AnswerPart = { kind: 'text'; text: string } | { kind: 'cite'; n: number };

/** Splits "Leave is 24 days [1][3]." into text and citation parts so citations render as buttons (no innerHTML). */
export function splitCitations(text: string): AnswerPart[] {
  const parts: AnswerPart[] = [];
  const re = /\[(\d{1,2})\]/g;
  let last = 0;
  let m: RegExpExecArray | null;
  while ((m = re.exec(text))) {
    if (m.index > last) parts.push({ kind: 'text', text: text.slice(last, m.index) });
    parts.push({ kind: 'cite', n: Number(m[1]) });
    last = m.index + m[0].length;
  }
  if (last < text.length) parts.push({ kind: 'text', text: text.slice(last) });
  return parts;
}
