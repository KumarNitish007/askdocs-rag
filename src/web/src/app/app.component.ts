import { Component, ElementRef, OnDestroy, OnInit, computed, signal, viewChild } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { api, ChatTurn, Citation, DocumentInfo } from './api';
import { AnswerPart, splitCitations } from './sse';

interface Message {
  role: 'user' | 'assistant';
  text: string;
  citations: Citation[];
  streaming: boolean;
  error?: string;
}

@Component({
  selector: 'app-root',
  imports: [FormsModule],
  templateUrl: './app.component.html',
  styleUrl: './app.component.scss'
})
export class AppComponent implements OnInit, OnDestroy {
  protected readonly provider = signal('');
  protected readonly documents = signal<DocumentInfo[]>([]);
  protected readonly selected = signal<Set<string>>(new Set());
  protected readonly messages = signal<Message[]>([]);
  protected readonly uploading = signal(false);
  protected readonly uploadError = signal('');
  protected readonly dragging = signal(false);
  protected readonly activeCitation = signal<Citation | null>(null);
  protected question = '';

  protected readonly busy = computed(() => this.messages().some(m => m.streaming));
  protected readonly readyCount = computed(() => this.documents().filter(d => d.status === 'Ready').length);
  protected readonly suggestions = [
    'How many days of annual leave do employees get?',
    'What is the notice period after probation?',
    'How much roof area does a 3 kW solar system need?'
  ];

  private readonly thread = viewChild<ElementRef<HTMLElement>>('thread');
  private poll?: ReturnType<typeof setInterval>;
  private abort?: AbortController;

  async ngOnInit(): Promise<void> {
    this.provider.set((await api.info()).provider);
    await this.refresh();
    this.poll = setInterval(() => {
      if (this.documents().some(d => d.status === 'Queued' || d.status === 'Processing')) this.refresh();
    }, 1500);
  }

  ngOnDestroy(): void {
    clearInterval(this.poll);
    this.abort?.abort();
  }

  protected parts(text: string): AnswerPart[] {
    return splitCitations(text);
  }

  protected citationFor(message: Message, n: number): Citation | undefined {
    return message.citations.find(c => c.number === n);
  }

  protected sizeLabel(bytes: number): string {
    return bytes > 1024 * 1024 ? `${(bytes / 1024 / 1024).toFixed(1)} MB` : `${Math.max(1, Math.round(bytes / 1024))} KB`;
  }

  protected toggle(id: string): void {
    const next = new Set(this.selected());
    if (next.has(id)) next.delete(id); else next.add(id);
    this.selected.set(next);
  }

  protected async onFiles(files: FileList | null): Promise<void> {
    if (!files?.length) return;
    this.uploading.set(true);
    this.uploadError.set('');
    try {
      for (const file of Array.from(files)) await api.upload(file);
    } catch (e) {
      this.uploadError.set((e as Error).message);
    } finally {
      this.uploading.set(false);
      await this.refresh();
    }
  }

  protected onDrop(event: DragEvent): void {
    event.preventDefault();
    this.dragging.set(false);
    this.onFiles(event.dataTransfer?.files ?? null);
  }

  protected async remove(doc: DocumentInfo): Promise<void> {
    if (!confirm(`Remove ${doc.fileName} from the library?`)) return;
    await api.remove(doc.id);
    const next = new Set(this.selected());
    next.delete(doc.id);
    this.selected.set(next);
    await this.refresh();
  }

  protected newChat(): void {
    this.abort?.abort();
    this.messages.set([]);
    this.activeCitation.set(null);
  }

  /** Enter sends, Shift+Enter inserts a new line. */
  protected onEnter(event: KeyboardEvent): void {
    if (event.shiftKey) return;
    event.preventDefault();
    this.send();
  }

  protected stop(): void {
    this.abort?.abort();
  }

  protected async send(text = this.question): Promise<void> {
    const question = text.trim();
    if (!question || this.busy()) return;
    this.question = '';

    const history: ChatTurn[] = this.messages()
      .filter(m => !m.error && !m.streaming)
      .map(m => ({ role: m.role, content: m.text }));

    const answer: Message = { role: 'assistant', text: '', citations: [], streaming: true };
    this.messages.update(m => [...m, { role: 'user', text: question, citations: [], streaming: false }, answer]);
    const update = (change: Partial<Message>) => {
      Object.assign(answer, change);
      this.messages.update(m => [...m]);
      queueMicrotask(() => this.thread()?.nativeElement.scrollTo({ top: 1e9, behavior: 'smooth' }));
    };

    this.abort = new AbortController();
    try {
      await api.ask(question, [...this.selected()], history, {
        onSources: citations => update({ citations }),
        onToken: t => update({ text: answer.text + t }),
        onDone: () => update({ streaming: false }),
        onError: message => update({ streaming: false, error: message })
      }, this.abort.signal);
    } catch (e) {
      update({ error: (e as Error).name === 'AbortError' ? 'Stopped.' : 'Connection lost. Is the API running?' });
    } finally {
      update({ streaming: false, text: answer.text.trim() });
    }
  }

  private async refresh(): Promise<void> {
    this.documents.set(await api.documents());
  }
}
