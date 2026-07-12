import { describe, expect, it } from 'vitest';
import { parseSseStream, type SseFrame } from './sse';

function streamOf(chunks: string[]): ReadableStream<Uint8Array> {
  const encoder = new TextEncoder();
  return new ReadableStream({
    start(controller) {
      for (const chunk of chunks) controller.enqueue(encoder.encode(chunk));
      controller.close();
    },
  });
}

async function collect(chunks: string[]): Promise<SseFrame[]> {
  const frames: SseFrame[] = [];
  for await (const frame of parseSseStream(streamOf(chunks))) frames.push(frame);
  return frames;
}

describe('parseSseStream', () => {
  it('parses the chat endpoint event sequence', async () => {
    const frames = await collect([
      'event: message-accepted\ndata: {"userMessageId":7,"conversationId":42}\n\n',
      'event: delta\ndata: {"text":"Hel"}\n\n',
      'event: delta\ndata: {"text":"lo"}\n\n',
      'event: done\ndata: {"assistantMessageId":8,"stopReason":"complete"}\n\n',
    ]);
    expect(frames).toEqual([
      { event: 'message-accepted', data: '{"userMessageId":7,"conversationId":42}' },
      { event: 'delta', data: '{"text":"Hel"}' },
      { event: 'delta', data: '{"text":"lo"}' },
      { event: 'done', data: '{"assistantMessageId":8,"stopReason":"complete"}' },
    ]);
  });

  it('joins multi-line data with newlines', async () => {
    const frames = await collect(['event: delta\ndata: line one\ndata: line two\n\n']);
    expect(frames).toEqual([{ event: 'delta', data: 'line one\nline two' }]);
  });

  it('reassembles frames split across arbitrary chunk boundaries', async () => {
    const wire = 'event: delta\ndata: {"text":"chunked"}\n\nevent: done\ndata: {"x":1}\n\n';
    // Split at every third byte to force partial lines and partial fields.
    const chunks: string[] = [];
    for (let i = 0; i < wire.length; i += 3) chunks.push(wire.slice(i, i + 3));
    const frames = await collect(chunks);
    expect(frames).toEqual([
      { event: 'delta', data: '{"text":"chunked"}' },
      { event: 'done', data: '{"x":1}' },
    ]);
  });

  it('handles CRLF line endings', async () => {
    const frames = await collect(['event: delta\r\ndata: {"text":"crlf"}\r\n\r\n']);
    expect(frames).toEqual([{ event: 'delta', data: '{"text":"crlf"}' }]);
  });

  it('ignores comment lines and id/retry fields', async () => {
    const frames = await collect([
      ': keep-alive\nid: 3\nretry: 1000\nevent: delta\ndata: {"text":"x"}\n\n: trailing comment\n\n',
    ]);
    expect(frames).toEqual([{ event: 'delta', data: '{"text":"x"}' }]);
  });

  it('defaults the event name to "message" when no event field is sent', async () => {
    const frames = await collect(['data: plain\n\n']);
    expect(frames).toEqual([{ event: 'message', data: 'plain' }]);
  });

  it('discards a trailing frame that never got its terminating blank line', async () => {
    const frames = await collect(['event: delta\ndata: {"text":"done"}\n\nevent: delta\ndata: partial']);
    expect(frames).toEqual([{ event: 'delta', data: '{"text":"done"}' }]);
  });

  it('handles multi-byte characters split across chunk boundaries', async () => {
    const encoder = new TextEncoder();
    const bytes = encoder.encode('event: delta\ndata: {"text":"héllo→"}\n\n');
    const mid = 22; // splits inside a multi-byte sequence
    const stream = new ReadableStream<Uint8Array>({
      start(controller) {
        controller.enqueue(bytes.slice(0, mid));
        controller.enqueue(bytes.slice(mid));
        controller.close();
      },
    });
    const frames: SseFrame[] = [];
    for await (const frame of parseSseStream(stream)) frames.push(frame);
    expect(frames).toEqual([{ event: 'delta', data: '{"text":"héllo→"}' }]);
  });
});
