/**
 * Read the Server-Sent-Events stream returned by
 * /api/database/create-quickstart, forwarding each progress `message` to
 * `onProgress` and returning the connection string carried by the final
 * `Complete!` event (or '' if none was seen).
 *
 * A server event flagged `error: true` rejects with that event's message: it is the
 * real failure ("Failed to insert seed data: …"), and the stream ends right after it.
 *
 * The caller guards on `response.body` before invoking this.
 */
/** One `data:` frame written by the host's `SseWriter.Event`. */
interface QuickStartEvent {
  stage?: string;
  percent?: number;
  message?: string;
  error?: boolean;
  connectionString?: string;
  provider?: string;
}

export async function parseQuickStartStream(
  response: Response,
  onProgress: (message: string) => void,
): Promise<string> {
  const reader = response.body!.getReader();
  const decoder = new TextDecoder();
  let buffer = '';
  let connectionString = '';

  const processLine = (line: string) => {
    if (!line.startsWith('data: ')) return;
    const event = JSON.parse(line.slice(6)) as QuickStartEvent;
    if (event.error) throw new Error(event.message || 'Quickstart database creation failed');
    if (event.message) onProgress(event.message);
    if (event.connectionString) connectionString = event.connectionString;
  };

  for (;;) {
    const { done, value } = await reader.read();
    if (done) break;
    buffer += decoder.decode(value, { stream: true });

    const lines = buffer.split('\n');
    buffer = lines.pop() ?? '';
    for (const line of lines) processLine(line);
  }

  // Flush the trailing line: the final `Complete!` event (which carries the
  // connection string) often arrives without a trailing newline, so it sits
  // in the buffer after the loop ends and would otherwise be dropped.
  buffer += decoder.decode();
  if (buffer.length > 0) processLine(buffer);

  return connectionString;
}
