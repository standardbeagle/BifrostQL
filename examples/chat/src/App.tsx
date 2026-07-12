import { useCallback, useEffect, useRef, useState } from 'react';
import { ChatPane, type ChatNotice } from './components/ChatPane';
import { ConversationList } from './components/ConversationList';
import { ChatHttpError, createConversation, streamMessage } from './lib/chat-api';
import { fetchConversations, fetchMessages, type ConversationRow, type MessageRow } from './lib/graphql';

export function App() {
  const [conversations, setConversations] = useState<ConversationRow[]>([]);
  const [activeId, setActiveId] = useState<number | null>(null);
  const [messages, setMessages] = useState<MessageRow[]>([]);
  const [draft, setDraft] = useState<string | null>(null);
  const [streaming, setStreaming] = useState(false);
  const [notice, setNotice] = useState<ChatNotice | null>(null);
  const [loadError, setLoadError] = useState<string | null>(null);
  const abortRef = useRef<AbortController | null>(null);

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

  const handleSend = async (content: string) => {
    if (activeId === null || streaming) return;
    const conversationId = activeId;
    setNotice(null);
    setStreaming(true);
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
            setNotice(
              event.code === 'refusal'
                ? { kind: 'refusal', category: event.refusalCategory }
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
          notice={notice}
          onSend={(content) => void handleSend(content)}
          onCancel={handleCancel}
          onReloadHistory={() => activeId !== null && void loadMessages(activeId)}
        />
      </div>
    </div>
  );
}
