/**
 * Minimal server-sent-events parser over a fetch ReadableStream.
 *
 * Implements the parts of the SSE wire format the BifrostQL chat endpoints
 * use (see docs/guides/llm-chat): named events (`event:`), JSON payloads in
 * one or more `data:` lines (multi-line data joins with "\n" per spec),
 * comment lines (`:`), and CRLF, LF, or lone-CR line endings (the SSE spec
 * allows all three). `id:`/`retry:` fields are accepted and ignored. A frame
 * is dispatched on the blank line that ends it; a trailing partial frame
 * without its terminating blank line is discarded, as the SSE spec requires.
 */
export interface SseFrame {
  /** Event name; "message" when the frame carried no `event:` field. */
  event: string;
  /** Joined data payload. */
  data: string;
}

export async function* parseSseStream(
  stream: ReadableStream<Uint8Array>,
): AsyncGenerator<SseFrame, void, undefined> {
  const decoder = new TextDecoder();
  const reader = stream.getReader();
  let buffer = '';
  let eventName = '';
  let dataLines: string[] = [];

  try {
    for (;;) {
      const { done, value } = await reader.read();
      buffer += value !== undefined ? decoder.decode(value, { stream: !done }) : '';
      if (done) buffer += decoder.decode();

      for (;;) {
        // A line ends at LF, CRLF, or a lone CR. A CR as the buffer's last byte
        // is ambiguous until the next chunk shows whether an LF follows — wait,
        // unless the stream is done and no more bytes can arrive.
        const cr = buffer.indexOf('\r');
        const lf = buffer.indexOf('\n');
        let end: number;
        let skip: number;
        if (cr >= 0 && (lf < 0 || cr < lf)) {
          if (cr === buffer.length - 1 && !done) break;
          end = cr;
          skip = buffer[cr + 1] === '\n' ? 2 : 1;
        } else if (lf >= 0) {
          end = lf;
          skip = 1;
        } else {
          break;
        }
        const line = buffer.slice(0, end);
        buffer = buffer.slice(end + skip);

        if (line === '') {
          // Blank line: dispatch the accumulated frame, if it has data.
          if (dataLines.length > 0) {
            yield { event: eventName === '' ? 'message' : eventName, data: dataLines.join('\n') };
          }
          eventName = '';
          dataLines = [];
          continue;
        }
        if (line.startsWith(':')) continue; // comment / keep-alive

        const colon = line.indexOf(':');
        const field = colon < 0 ? line : line.slice(0, colon);
        let fieldValue = colon < 0 ? '' : line.slice(colon + 1);
        if (fieldValue.startsWith(' ')) fieldValue = fieldValue.slice(1);

        if (field === 'event') eventName = fieldValue;
        else if (field === 'data') dataLines.push(fieldValue);
        // id / retry / unknown fields: ignored.
      }

      if (done) return;
    }
  } finally {
    reader.releaseLock();
  }
}
