import type { ConversationRow } from '../lib/graphql';

export interface ConversationListProps {
  conversations: ConversationRow[];
  activeId: number | null;
  busy: boolean;
  onSelect: (id: number) => void;
  onCreate: () => void;
}

export function ConversationList({
  conversations,
  activeId,
  busy,
  onSelect,
  onCreate,
}: ConversationListProps) {
  return (
    <aside className="sidebar">
      <button className="new-conversation" onClick={onCreate} disabled={busy}>
        + New conversation
      </button>
      <ul className="conversation-list">
        {conversations.map((conversation) => (
          <li key={conversation.id}>
            <button
              className={conversation.id === activeId ? 'conversation active' : 'conversation'}
              onClick={() => onSelect(conversation.id)}
            >
              {conversation.title ?? `Conversation ${conversation.id}`}
            </button>
          </li>
        ))}
        {conversations.length === 0 && <li className="empty">No conversations yet.</li>}
      </ul>
    </aside>
  );
}
