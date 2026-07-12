/**
 * Client for the BifrostQL chat endpoints (docs/guides/llm-chat):
 *   POST {path}/conversations              -> { id }
 *   POST {path}/conversations/{id}/messages -> text/event-stream
 *
 * Stream events: message-accepted, delta (repeated), then exactly one
 * terminal event — done or error.
 */
import { parseSseStream } from './sse';

const CHAT_PATH = '/_chat';

/** Non-200 response before the stream starts (401/404/409/500...). */
export class ChatHttpError extends Error {
  constructor(
    readonly status: number,
    readonly code: string,
    message: string,
  ) {
    super(message);
    this.name = 'ChatHttpError';
  }
}

export type ChatStreamEvent =
  | { type: 'message-accepted'; userMessageId: number; conversationId: number }
  | { type: 'delta'; text: string }
  | { type: 'done'; assistantMessageId: number; stopReason: 'complete' | 'truncated' }
  | {
      type: 'error';
      code: string;
      message: string;
      refusalCategory?: string;
      retryable?: boolean;
    };

async function toChatHttpError(response: Response): Promise<ChatHttpError> {
  let code = 'unknown';
  let message = `HTTP ${response.status}`;
  try {
    const body = (await response.json()) as { code?: string; message?: string };
    if (body.code) code = body.code;
    if (body.message) message = body.message;
  } catch {
    // Non-JSON error body: keep the status-derived message.
  }
  return new ChatHttpError(response.status, code, message);
}

export async function createConversation(title?: string): Promise<number> {
  const response = await fetch(`${CHAT_PATH}/conversations`, {
    method: 'POST',
    headers: { 'content-type': 'application/json' },
    credentials: 'include',
    body: JSON.stringify(title ? { title } : {}),
  });
  if (!response.ok) throw await toChatHttpError(response);
  const body = (await response.json()) as { id: number };
  return body.id;
}

/**
 * Posts a user message and yields the streamed completion events.
 *
 * Throws ChatHttpError when the request is rejected before the stream starts
 * (notably 409 stream-in-progress). Once streaming, failures arrive as a
 * terminal `error` event instead. Aborting `signal` cancels the fetch — the
 * server discards the partial completion but keeps the accepted user message.
 */
export async function* streamMessage(
  conversationId: number,
  content: string,
  signal: AbortSignal,
): AsyncGenerator<ChatStreamEvent, void, undefined> {
  const response = await fetch(
    `${CHAT_PATH}/conversations/${encodeURIComponent(conversationId)}/messages`,
    {
      method: 'POST',
      headers: { 'content-type': 'application/json' },
      credentials: 'include',
      body: JSON.stringify({ content }),
      signal,
    },
  );
  if (!response.ok) throw await toChatHttpError(response);
  if (!response.body) throw new Error('Chat stream response carried no body.');

  for await (const frame of parseSseStream(response.body)) {
    const event = mapFrame(frame.event, frame.data);
    if (event) yield event;
    if (event?.type === 'done' || event?.type === 'error') return;
  }
}

export function mapFrame(event: string, data: string): ChatStreamEvent | null {
  const payload = JSON.parse(data) as Record<string, unknown>;
  switch (event) {
    case 'message-accepted':
      return {
        type: 'message-accepted',
        userMessageId: payload.userMessageId as number,
        conversationId: payload.conversationId as number,
      };
    case 'delta':
      return { type: 'delta', text: payload.text as string };
    case 'done':
      return {
        type: 'done',
        assistantMessageId: payload.assistantMessageId as number,
        stopReason: payload.stopReason as 'complete' | 'truncated',
      };
    case 'error':
      return {
        type: 'error',
        code: payload.code as string,
        message: payload.message as string,
        refusalCategory: payload.refusalCategory as string | undefined,
        retryable: payload.retryable as boolean | undefined,
      };
    default:
      // Unknown event names are ignored so the server can add events later.
      return null;
  }
}
