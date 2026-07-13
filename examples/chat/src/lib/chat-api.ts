/**
 * Client for the BifrostQL chat endpoints (docs/guides/llm-chat):
 *   POST {path}/conversations                                    -> { id }
 *   POST {path}/conversations/{id}/messages                      -> text/event-stream
 *   POST {path}/conversations/{id}/confirmations/{confirmationId} -> { confirmationId, approved }
 *   GET  {path}/media/{table}/{id}                               -> image bytes (auth-gated)
 *
 * Stream events: message-accepted, then any interleaving of delta / tool /
 * media / confirmation / confirmation-resolved, then exactly one terminal
 * event — done or error.
 */
import { parseSseStream } from './sse';

const CHAT_PATH = '/_chat';

/** The confirmation endpoint rejects longer reasons with a 400. */
export const MAX_CONFIRMATION_REASON_LENGTH = 500;

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

export interface MediaItem {
  id: number | string;
  /** A stored URL (URL-mode tables) or an opaque bifrost-media://table/id reference. */
  mediaReference: string;
  contentType?: string;
  caption?: string;
}

export interface ConfirmationRequest {
  confirmationId: string;
  toolName: string;
  table: string;
  operation: string;
  rows: Record<string, unknown>[];
  summary: string;
}

export type ChatStreamEvent =
  | { type: 'message-accepted'; userMessageId: number; conversationId: number }
  | { type: 'delta'; text: string }
  | { type: 'tool'; name: string; phase: 'call' | 'result'; summary?: string }
  | { type: 'media'; toolName: string; items: MediaItem[] }
  | ({ type: 'confirmation' } & ConfirmationRequest)
  | { type: 'confirmation-resolved'; confirmationId: string; approved: boolean; reason?: string }
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
 * Resolves a parked plan proposal. A 404 means the proposal is no longer
 * resolvable — already resolved (possibly from another tab), timed out, or
 * never the caller's — and the stream's own `confirmation-resolved` event is
 * the authoritative outcome.
 */
export async function resolveConfirmation(
  conversationId: number,
  confirmationId: string,
  approve: boolean,
  reason?: string,
): Promise<void> {
  const response = await fetch(
    `${CHAT_PATH}/conversations/${encodeURIComponent(conversationId)}/confirmations/${encodeURIComponent(confirmationId)}`,
    {
      method: 'POST',
      headers: { 'content-type': 'application/json' },
      credentials: 'include',
      body: JSON.stringify(reason ? { approve, reason } : { approve }),
    },
  );
  if (!response.ok) throw await toChatHttpError(response);
}

/**
 * The auth-gated fetch path for a binary-mode media reference
 * (`bifrost-media://table/id` -> `{CHAT_PATH}/media/table/id`), or null when
 * the reference is a plain stored URL the client should use directly.
 */
export function mediaFetchPath(mediaReference: string): string | null {
  const match = /^bifrost-media:\/\/([^/]+)\/([^/]+)$/.exec(mediaReference);
  if (!match) return null;
  return `${CHAT_PATH}/media/${encodeURIComponent(match[1])}/${encodeURIComponent(match[2])}`;
}

/**
 * Resolves a media reference to a URL an <img> can render: stored URLs pass
 * through; bifrost-media:// references are fetched with credentials (the
 * media route re-authorizes the row on every request) and returned as an
 * object URL the caller must revoke when done with it.
 */
export async function resolveMediaUrl(mediaReference: string): Promise<string> {
  const path = mediaFetchPath(mediaReference);
  if (path === null) return mediaReference;
  const response = await fetch(path, { credentials: 'include' });
  if (!response.ok) throw await toChatHttpError(response);
  return URL.createObjectURL(await response.blob());
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
  let payload: Record<string, unknown>;
  try {
    payload = JSON.parse(data) as Record<string, unknown>;
  } catch {
    // A frame whose data is not JSON is not one of ours (e.g. a proxy's
    // keep-alive) — skip it rather than tearing the stream down.
    return null;
  }
  switch (event) {
    case 'message-accepted':
      return {
        type: 'message-accepted',
        userMessageId: payload.userMessageId as number,
        conversationId: payload.conversationId as number,
      };
    case 'delta':
      return { type: 'delta', text: payload.text as string };
    case 'tool':
      return {
        type: 'tool',
        name: payload.name as string,
        phase: payload.phase as 'call' | 'result',
        summary: payload.summary as string | undefined,
      };
    case 'media':
      return {
        type: 'media',
        toolName: payload.toolName as string,
        items: (payload.items ?? []) as MediaItem[],
      };
    case 'confirmation':
      return {
        type: 'confirmation',
        confirmationId: payload.confirmationId as string,
        toolName: payload.toolName as string,
        table: payload.table as string,
        operation: payload.operation as string,
        rows: (payload.rows ?? []) as Record<string, unknown>[],
        summary: payload.summary as string,
      };
    case 'confirmation-resolved':
      return {
        type: 'confirmation-resolved',
        confirmationId: payload.confirmationId as string,
        approved: payload.approved as boolean,
        reason: payload.reason as string | undefined,
      };
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
