/**
 * Read the Server-Sent-Events stream returned by
 * /api/database/create-quickstart, forwarding each progress `message` to
 * `onProgress` and returning the connection string carried by the final
 * `Complete!` event (or '' if none was seen).
 *
 * The caller guards on `response.body` before invoking this.
 */
export async function parseQuickstartStream(
  response: Response,
  onProgress: (message: string) => void,
): Promise<string> {
  const reader = response.body!.getReader();
  const decoder = new TextDecoder();
  let buffer = '';
  let connectionString = '';

  const processLine = (line: string) => {
    if (!line.startsWith('data: ')) return;
    try {
      const event = JSON.parse(line.slice(6));
      if (event.message) onProgress(event.message);
      if (event.connectionString) connectionString = event.connectionString;
    } catch {
      // skip malformed SSE data lines
    }
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
