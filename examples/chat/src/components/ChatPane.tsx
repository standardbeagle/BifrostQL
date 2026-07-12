import { useEffect, useRef, useState } from 'react';
import type { MessageRow } from '../lib/graphql';

/** Typed UI states for the non-happy paths the chat contract defines. */
export type ChatNotice =
  | { kind: 'refusal'; category?: string }
  | { kind: 'stream-error'; code: string; message: string; retryable?: boolean }
  | { kind: 'busy' } // 409 stream-in-progress
  | { kind: 'send-failed'; status: number; message: string } // message may already be saved
  | { kind: 'cancelled' }
  | { kind: 'truncated' };

export interface ChatPaneProps {
  conversationId: number | null;
  messages: MessageRow[];
  draft: string | null; // streaming assistant text, null when not streaming
  streaming: boolean;
  notice: ChatNotice | null;
  onSend: (content: string) => void;
  onCancel: () => void;
  onReloadHistory: () => void;
}

function NoticeBanner({ notice, onReloadHistory }: { notice: ChatNotice; onReloadHistory: () => void }) {
  switch (notice.kind) {
    case 'refusal':
      return (
        <div className="notice refusal">
          The model declined to answer{notice.category ? ` (${notice.category})` : ''}. The
          partial response was discarded and nothing was saved; your message was kept.
        </div>
      );
    case 'stream-error':
      return (
        <div className="notice error">
          The response stream failed ({notice.code}): {notice.message}
          {notice.retryable === true ? ' You can try sending again.' : ''}
        </div>
      );
    case 'busy':
      return (
        <div className="notice busy">
          A response is already streaming for this conversation. Wait for it to finish before
          sending again.
        </div>
      );
    case 'send-failed':
      return (
        <div className="notice error">
          Sending failed (HTTP {notice.status}): {notice.message} Your message{' '}
          <strong>may already be saved</strong> — reload the history before sending again so it
          is not duplicated.{' '}
          <button className="inline-action" onClick={onReloadHistory}>
            Reload history
          </button>
        </div>
      );
    case 'cancelled':
      return (
        <div className="notice busy">
          Response cancelled. Your message was saved; the partial answer was discarded.
        </div>
      );
    case 'truncated':
      return (
        <div className="notice busy">
          The response hit the token limit and was truncated. The truncated text was saved.
        </div>
      );
  }
}

export function ChatPane({
  conversationId,
  messages,
  draft,
  streaming,
  notice,
  onSend,
  onCancel,
  onReloadHistory,
}: ChatPaneProps) {
  const [input, setInput] = useState('');
  const scrollRef = useRef<HTMLDivElement>(null);

  useEffect(() => {
    scrollRef.current?.scrollTo({ top: scrollRef.current.scrollHeight });
  }, [messages, draft]);

  if (conversationId === null) {
    return <main className="chat-pane empty-state">Select or create a conversation.</main>;
  }

  const submit = () => {
    const content = input.trim();
    if (content === '' || streaming) return;
    setInput('');
    onSend(content);
  };

  return (
    <main className="chat-pane">
      <div className="messages" ref={scrollRef}>
        {messages.map((message) => (
          <div key={message.id} className={`bubble ${message.role}`}>
            {message.content}
          </div>
        ))}
        {draft !== null && (
          <div className="bubble assistant streaming">
            {draft}
            <span className="cursor">▍</span>
          </div>
        )}
      </div>
      {notice && <NoticeBanner notice={notice} onReloadHistory={onReloadHistory} />}
      <form
        className="send-box"
        onSubmit={(event) => {
          event.preventDefault();
          submit();
        }}
      >
        <input
          value={input}
          onChange={(event) => setInput(event.target.value)}
          placeholder="Ask something…"
          disabled={streaming}
          autoFocus
        />
        {streaming ? (
          <button type="button" className="cancel" onClick={onCancel}>
            Stop
          </button>
        ) : (
          <button type="submit" disabled={input.trim() === ''}>
            Send
          </button>
        )}
      </form>
    </main>
  );
}
