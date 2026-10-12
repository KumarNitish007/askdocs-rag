import { SseParser, splitCitations } from './sse';

describe('SseParser', () => {
  it('parses complete events and buffers partial ones across chunks', () => {
    const p = new SseParser();

    expect(p.push('event: token\ndata: {"text":"Hel')).toEqual([]);
    expect(p.push('lo"}\n\nevent: done\ndata: {}\n\nevent: tok')).toEqual([
      { event: 'token', data: '{"text":"Hello"}' },
      { event: 'done', data: '{}' }
    ]);
    expect(p.push('en\ndata: {"text":"!"}\n\n')).toEqual([{ event: 'token', data: '{"text":"!"}' }]);
  });

  it('handles CRLF line endings', () => {
    expect(new SseParser().push('event: done\r\ndata: {}\r\n\r\n')).toEqual([{ event: 'done', data: '{}' }]);
  });
});

describe('splitCitations', () => {
  it('turns [n] markers into citation parts', () => {
    expect(splitCitations('Leave is 24 days [1][3].')).toEqual([
      { kind: 'text', text: 'Leave is 24 days ' },
      { kind: 'cite', n: 1 },
      { kind: 'cite', n: 3 },
      { kind: 'text', text: '.' }
    ]);
  });

  it('leaves text without citations alone', () => {
    expect(splitCitations('No sources here')).toEqual([{ kind: 'text', text: 'No sources here' }]);
  });
});
