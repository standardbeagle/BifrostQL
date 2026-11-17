import { describe, expect, it } from 'vitest';
import { parseQuickStartStream } from './quickstart-stream';

function sseResponse(frames: string[]): Response {
  const body = frames.map((f) => `data: ${f}\n\n`).join('');
  return new Response(body, { headers: { 'Content-Type': 'text/event-stream' } });
}

describe('parseQuickStartStream', () => {
  it('returns the connection string from the Complete! event and forwards progress', async () => {
    const progress: string[] = [];
    const cs = await parseQuickStartStream(
      sseResponse([
        JSON.stringify({ stage: 'Creating database', percent: 10, message: 'Creating' }),
        JSON.stringify({ stage: 'Complete!', percent: 100, message: 'Done', connectionString: 'Data Source=/tmp/x.db' }),
      ]),
      (m) => progress.push(m),
    );
    expect(cs).toBe('Data Source=/tmp/x.db');
    expect(progress).toEqual(['Creating', 'Done']);
  });

  it("rejects with the server's error event message instead of returning no connection string", async () => {
    // Arrange — the host's SseWriter.Event(..., error: true) frame after a seed failure.
    const response = sseResponse([
      JSON.stringify({ stage: 'Loading seed data', percent: 75, message: 'Loading sample dataset' }),
      JSON.stringify({
        stage: 'Error',
        percent: 0,
        message: 'Failed to insert seed data: unrecognized token',
        error: true,
      }),
    ]);
    // Act + Assert
    await expect(parseQuickStartStream(response, () => {})).rejects.toThrow(
      'Failed to insert seed data: unrecognized token',
    );
  });
});
