import { useCallback, useEffect, useRef, useState } from 'react';
import { ChatPane, type ActivityItem, type ChatNotice } from './components/ChatPane';
import { ConversationList } from './components/ConversationList';
import {
  ChatHttpError,
  createConversation,
  resolveConfirmation,
  streamMessage,
  type ConfirmationRequest,
} from './lib/chat-api';
import { fetchConversations, fetchMessages, type ConversationRow, type MessageRow } from './lib/graphql';

export function App() {
  const [conversations, setConversations] = useState<ConversationRow[]>([]);
  const [activeId, setActiveId] = useState<number | null>(null);
  const [messages, setMessages] = useState<MessageRow[]>([]);
  const [draft, setDraft] = useState<string | null>(null);
  const [streaming, setStreaming] = useState(false);
  const [activity, setActivity] = useState<ActivityItem[]>([]);
  const [confirmation, setConfirmation] = useState<ConfirmationRequest | null>(null);
  const [notice, setNotice] = useState<ChatNotice | null>(null);
  const [loadError, setLoadError] = useState<string | null>(null);
  const abortRef = useRef<AbortController | null>(null);
  const activityKeyRef = useRef(0);

  const loadConversations = useCallback(async () => {
    try {
      setConversations(await fetchConversations());
      setLoadError(null);
    } catch (error) {
      setLoadError(error instanceof Error ? error.message : String(error));
    }
  }, []);

  const loadMessages = useCallback(async (conversationId: number) => {
    try {
      setMessages(await fetchMessages(conversationId));
      setLoadError(null);
    } catch (error) {
      setLoadError(error instanceof Error ? error.message : String(error));
    }
  }, []);

  useEffect(() => {
    void loadConversations();
  }, [loadConversations]);

  useEffect(() => {
    setMessages([]);
    setNotice(null);
    setActivity([]);
    setConfirmation(null);
    if (activeId !== null) void loadMessages(activeId);
  }, [activeId, loadMessages]);

  const handleCreate = async () => {
    try {
      const id = await createConversation(`Conversation ${new Date().toLocaleString()}`);
      await loadConversations();
      setActiveId(id);
    } catch (error) {
      setLoadError(error instanceof Error ? error.message : String(error));
    }
  };

  /**
   * Records one live `tool` event: a `result` completes the newest pending
   * `call` chip for that tool; anything else appends a chip.
   */
  const recordToolEvent = (name: string, phase: 'call' | 'result', summary?: string) => {
    setActivity((current) => {
      if (phase === 'result') {
        for (let i = current.length - 1; i >= 0; i -= 1) {
          const item = current[i];
          if (item.kind === 'tool' && item.name === name && item.phase === 'call') {
            const next = [...current];
            next[i] = { ...item, phase: 'result', summary };
            return next;
          }
        }
      }
      activityKeyRef.current += 1;
      return [...current, { kind: 'tool', key: activityKeyRef.current, name, phase, summary }];
    });
  };

  const handleSend = async (content: string) => {
    if (activeId === null || streaming) return;
    const conversationId = activeId;
    setNotice(null);
    setStreaming(true);
    // Tool chips and inline media are per-turn display state (the server
    // persists final answer text only) — a new turn starts clean.
    setActivity([]);
    setConfirmation(null);
    // Show the user's message immediately; the server assigns its real id and
    // history reloads with it after the stream ends.
    setMessages((current) => [
      ...current,
      { id: -Date.now(), role: 'user', content, created_at: null },
    ]);

    const controller = new AbortController();
    abortRef.current = controller;
    let text = '';

    try {
      for await (const event of streamMessage(conversationId, content, controller.signal)) {
        switch (event.type) {
          case 'message-accepted':
            break;
          case 'delta':
            text += event.text;
            setDraft(text);
            break;
          case 'tool':
            recordToolEvent(event.name, event.phase, event.summary);
            break;
          case 'media':
            activityKeyRef.current += 1;
            setActivity((current) => [
              ...current,
              { kind: 'media', key: activityKeyRef.current, toolName: event.toolName, items: event.items },
            ]);
            break;
          case 'confirmation':
            // A plan tool parked a write proposal; the stream is idle until
            // it resolves. Show the card and wait.
            setConfirmation({
              confirmationId: event.confirmationId,
              toolName: event.toolName,
              table: event.table,
              operation: event.operation,
              rows: event.rows,
              summary: event.summary,
            });
            break;
          case 'confirmation-resolved':
            // Authoritative resolution — ours, another tab's, or the server
            // timeout's deny. Clear the card either way.
            setConfirmation((current) =>
              current?.confirmationId === event.confirmationId ? null : current,
            );
            recordToolEvent(
              'proposal',
              'result',
              event.approved ? 'approved' : `denied${event.reason ? `: ${event.reason}` : ''}`,
            );
            break;
          case 'done':
            // Terminal success — the assistant row is persisted. Reload from
            // GraphQL so the UI shows exactly what the database holds.
            setDraft(null);
            if (event.stopReason === 'truncated') setNotice({ kind: 'truncated' });
            await loadMessages(conversationId);
            break;
          case 'error':
            // Terminal failure — nothing was persisted for the assistant, so
            // discard the partial deltas (the contract says clients must).
            setDraft(null);
            setConfirmation(null);
            setNotice(
              event.code === 'refusal'
                ? { kind: 'refusal', category: event.refusalCategory }
                : event.code === 'tool-loop-limit'
                  ? { kind: 'tool-loop-limit' }
                  : {
                      kind: 'stream-error',
                      code: event.code,
                      message: event.message,
                      retryable: event.retryable,
                    },
            );
            await loadMessages(conversationId);
            break;
        }
      }
    } catch (error) {
      setDraft(null);
      setConfirmation(null);
      if (controller.signal.aborted) {
        // User pressed Stop: the accepted user message stays, the partial
        // completion is never persisted server-side.
        setNotice({ kind: 'cancelled' });
        await loadMessages(conversationId);
      } else if (error instanceof ChatHttpError && error.status === 409) {
        setNotice({ kind: 'busy' });
      } else {
        // DEDUPE RISK — do NOT auto-retry here. A 500 (or a dropped
        // connection) after the POST left our hands may have landed AFTER the
        // user message was persisted; resending would store the question
        // twice and trigger a second completion. Surface the failure and let
        // the user reload history to see what was actually saved.
        const status = error instanceof ChatHttpError ? error.status : 0;
        const message = error instanceof Error ? error.message : String(error);
        setNotice({ kind: 'send-failed', status, message });
      }
    } finally {
      abortRef.current = null;
      setStreaming(false);
      setDraft(null);
    }
  };

  const handleCancel = () => abortRef.current?.abort();

  const handleDecideConfirmation = async (approve: boolean, reason?: string) => {
    if (activeId === null || confirmation === null) return;
    await resolveConfirmation(activeId, confirmation.confirmationId, approve, reason);
    // The card clears when the stream's confirmation-resolved event arrives —
    // that event, not this response, is the authoritative outcome.
  };

  return (
    <div className="app">
      <ConversationList
        conversations={conversations}
        activeId={activeId}
        busy={streaming}
        onSelect={setActiveId}
        onCreate={() => void handleCreate()}
      />
      <div className="content">
        {loadError && <div className="notice error top">{loadError}</div>}
        <ChatPane
          conversationId={activeId}
          messages={messages}
          draft={draft}
          streaming={streaming}
          activity={activity}
          confirmation={confirmation}
          notice={notice}
          onSend={(content) => void handleSend(content)}
          onCancel={handleCancel}
          onDecideConfirmation={handleDecideConfirmation}
          onReloadHistory={() => activeId !== null && void loadMessages(activeId)}
        />
      </div>
    </div>
  );
}
