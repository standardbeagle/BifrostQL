import { ProfileDropdown } from './profiles/ProfileDropdown';
import type { ApiProfile } from './profiles/types';
import type { ConnectionInfo } from './connection';
import type { TransportMode } from './lib/transport';

export type EditorPane = 'graphql' | 'sql' | 'builder' | 'forms';

interface EditorHeaderProps {
  connectionInfo: ConnectionInfo | null;
  apiProfiles: ApiProfile[];
  activeProfileId: string;
  onSelectProfile: (id: string) => void;
  transportMode: TransportMode;
  transportConnected: boolean;
  onToggleTransport: () => void;
  sqlBridgeAvailable: boolean;
  editorPane: EditorPane;
  onSelectPane: (pane: EditorPane) => void;
  onShowAbout: () => void;
  onDisconnect: () => void;
}

/**
 * The editor view's top bar: brand, active connection name, API-profile picker,
 * GraphQL transport toggle (HTTP/JSON vs binary WebSocket, with a live
 * connection badge), the optional editor-pane switch (desktop-only), and the
 * About/Disconnect actions.
 */
export function EditorHeader({
  connectionInfo,
  apiProfiles,
  activeProfileId,
  onSelectProfile,
  transportMode,
  transportConnected,
  onToggleTransport,
  sqlBridgeAvailable,
  editorPane,
  onSelectPane,
  onShowAbout,
  onDisconnect,
}: EditorHeaderProps) {
  return (
    <div className="bifrost-header">
      <div className="bifrost-header__brand">
        <div className="bifrost-header__logo">B</div>
        <h1>BifrostQL</h1>
      </div>
      {connectionInfo && <>
        <div className="bifrost-header__separator" />
        <span className="bifrost-database-info">{connectionInfo.name}</span>
      </>}
      <div className="bifrost-header__spacer" />
      <ProfileDropdown
        profiles={apiProfiles}
        activeId={activeProfileId}
        onSelect={onSelectProfile}
      />
      <div
        className="bifrost-transport-toggle"
        role="group"
        aria-label="GraphQL transport"
        style={{
          display: 'flex',
          alignItems: 'center',
          gap: 8,
          marginRight: 12,
          fontSize: 12,
        }}
      >
        <span
          aria-label={
            transportMode === 'binary'
              ? transportConnected
                ? 'Binary transport connected'
                : 'Binary transport disconnected'
              : 'HTTP transport'
          }
          title={
            transportMode === 'binary'
              ? transportConnected
                ? 'Editor queries route over the binary WebSocket transport (connected)'
                : 'Binary WebSocket transport disconnected; reconnecting'
              : 'Editor queries route over the HTTP/JSON transport'
          }
          style={{
            width: 8,
            height: 8,
            borderRadius: '50%',
            background:
              transportMode === 'http'
                ? '#9ca3af'
                : transportConnected
                  ? '#22c55e'
                  : '#ef4444',
            display: 'inline-block',
          }}
        />
        <button
          type="button"
          onClick={onToggleTransport}
          aria-pressed={transportMode === 'binary'}
          title="Route editor queries over the HTTP/JSON or WebSocket binary transport."
          style={{
            background: 'transparent',
            border: '1px solid currentColor',
            borderRadius: 4,
            padding: '2px 8px',
            cursor: 'pointer',
            font: 'inherit',
            color: 'inherit',
          }}
        >
          {transportMode === 'binary' ? 'Binary' : 'GraphQL'}
        </button>
      </div>
      {sqlBridgeAvailable && (
        <div role="group" aria-label="Editor pane" style={{ display: 'flex', gap: 4, marginRight: 12 }}>
          {([
            ['graphql', 'GraphQL'],
            ['sql', 'SQL'],
            ['builder', 'Query builder'],
            ['forms', 'Form builder'],
          ] as const).map(([pane, label]) => (
            <button
              key={pane}
              type="button"
              onClick={() => onSelectPane(pane)}
              aria-pressed={editorPane === pane}
              style={{
                background: editorPane === pane ? '#2563eb' : 'transparent',
                color: editorPane === pane ? '#fff' : 'inherit',
                border: '1px solid currentColor',
                borderRadius: 4,
                padding: '2px 8px',
                cursor: 'pointer',
                font: 'inherit',
                fontSize: 12,
              }}
            >
              {label}
            </button>
          ))}
        </div>
      )}
      <button
        className="bifrost-disconnect-button"
        onClick={onShowAbout}
        style={{ marginRight: 8 }}
      >
        About
      </button>
      <button
        className="bifrost-disconnect-button"
        onClick={onDisconnect}
      >
        Disconnect
      </button>
    </div>
  );
}
