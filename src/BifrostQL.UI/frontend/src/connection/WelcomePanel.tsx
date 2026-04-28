import React, { useCallback, useEffect, useState } from 'react';
import { ConnectionInfo, PROVIDERS, VaultServer } from './types';
import { saveRecentConnections, loadRecentConnections } from './recent-connections';

const formatDate = (dateString: string): string => {
  const date = new Date(dateString);
  const now = new Date();
  const diffMs = now.getTime() - date.getTime();
  const diffMins = Math.floor(diffMs / 60000);
  const diffHours = Math.floor(diffMs / 3600000);
  const diffDays = Math.floor(diffMs / 86400000);

  if (diffMins < 1) return 'Just now';
  if (diffMins < 60) return `${diffMins}m ago`;
  if (diffHours < 24) return `${diffHours}h ago`;
  if (diffDays < 7) return `${diffDays}d ago`;

  return date.toLocaleDateString();
};

function getProviderIcon(provider: string): string {
  const found = PROVIDERS.find((p) => p.id === provider);
  return found ? found.icon : 'D';
}

function getProviderName(provider: string): string {
  const found = PROVIDERS.find((p) => p.id === provider);
  return found ? found.name : provider;
}

interface WelcomePanelProps {
  onConnectClick: () => void;
  onCreateTestDatabase: () => void;
  recentConnections?: ConnectionInfo[];
  onSelectRecentConnection?: (connection: ConnectionInfo) => void;
  onClearRecentConnections?: () => void;
  vaultServers?: VaultServer[];
  onConnectVaultServer?: (name: string) => void;
}

export const WelcomePanel: React.FC<WelcomePanelProps> = ({
  onConnectClick,
  onCreateTestDatabase,
  recentConnections: externalRecentConnections,
  onSelectRecentConnection,
  onClearRecentConnections,
  vaultServers,
  onConnectVaultServer,
}) => {
  const [internalRecentConnections, setInternalRecentConnections] = useState<ConnectionInfo[]>([]);
  const [hoveredId, setHoveredId] = useState<string | null>(null);

  const recentConnections = externalRecentConnections ?? internalRecentConnections;

  useEffect(() => {
    if (externalRecentConnections === undefined) {
      setInternalRecentConnections(loadRecentConnections());
    }
  }, [externalRecentConnections]);

  const handleSelectRecent = useCallback((connection: ConnectionInfo) => {
    onSelectRecentConnection?.(connection);
  }, [onSelectRecentConnection]);

  const handleDeleteRecent = useCallback((e: React.MouseEvent, connectionId: string) => {
    e.stopPropagation();
    if (externalRecentConnections !== undefined) {
      const updated = externalRecentConnections.filter((c) => c.id !== connectionId);
      saveRecentConnections(updated);
      onClearRecentConnections?.();
    } else {
      const updated = internalRecentConnections.filter((c) => c.id !== connectionId);
      setInternalRecentConnections(updated);
      saveRecentConnections(updated);
    }
  }, [externalRecentConnections, internalRecentConnections, onClearRecentConnections]);

  return (
    <div className="welcome-container">
      <div className="welcome-content">
        <div className="welcome-logo">
          <div className="welcome-logo-icon">BQ</div>
          <h1 className="welcome-title">BifrostQL</h1>
          <p className="welcome-subtitle">
            Explore your SQL databases with GraphQL
          </p>
        </div>

        <div className="welcome-cards">
          <button
            type="button"
            className={`welcome-card welcome-card--hero${hoveredId === 'try-it' ? ' welcome-card--hover' : ''}`}
            onClick={onCreateTestDatabase}
            onMouseEnter={() => setHoveredId('try-it')}
            onMouseLeave={() => setHoveredId(null)}
            data-testid="try-it-card"
          >
            <div className="welcome-card__icon welcome-card__icon--hero">&#9654;</div>
            <div className="welcome-card__body">
              <h3 className="welcome-card__title">Try It Now</h3>
              <p className="welcome-card__subtitle">Explore a ready-made database with GraphQL in seconds</p>
              <p className="welcome-card__description">
                Choose from 5 example databases - Blog, E-commerce, CRM, Classroom, or Project Tracker. No setup required.
              </p>
              <span className="welcome-card__button welcome-card__button--primary">Get Started</span>
            </div>
          </button>

          <button
            type="button"
            className={`welcome-card welcome-card--secondary${hoveredId === 'connect' ? ' welcome-card--hover' : ''}`}
            onClick={onConnectClick}
            onMouseEnter={() => setHoveredId('connect')}
            onMouseLeave={() => setHoveredId(null)}
            data-testid="connect-card"
          >
            <div className="welcome-card__icon">&#9881;</div>
            <div className="welcome-card__body">
              <h3 className="welcome-card__title">Connect to Database</h3>
              <p className="welcome-card__subtitle">SQL Server, PostgreSQL, MySQL, or SQLite</p>
              <span className="welcome-card__button">Connect</span>
            </div>
          </button>
        </div>

        {vaultServers && vaultServers.length > 0 && (
          <div className="welcome-vault">
            <h2 className="welcome-recent__header">Saved Servers</h2>
            <div className="welcome-recent__list">
              {vaultServers.map((server) => (
                <div
                  key={`vault-${server.name}`}
                  className={`welcome-recent__item${hoveredId === `vault-${server.name}` ? ' welcome-recent__item--hover' : ''}`}
                  onMouseEnter={() => setHoveredId(`vault-${server.name}`)}
                  onMouseLeave={() => setHoveredId(null)}
                >
                  <button
                    type="button"
                    className="welcome-recent__connect"
                    onClick={() => onConnectVaultServer?.(server.name)}
                    aria-label={`Connect to ${server.name}`}
                  >
                    <span className="welcome-recent__provider-badge">
                      {getProviderIcon(server.provider)}
                    </span>
                    <div className="welcome-recent__info">
                      <span className="welcome-recent__name">
                        {server.name}
                        {server.hasSsh && <span className="welcome-vault__badge" title="SSH tunnel">SSH</span>}
                        {server.source === 'env' && <span className="welcome-vault__badge welcome-vault__badge--env" title="From environment variable">ENV</span>}
                      </span>
                      <span className="welcome-recent__meta">
                        {getProviderName(server.provider)} &middot; {server.host}:{server.port}
                        {server.database ? `/${server.database}` : ''}
                      </span>
                    </div>
                    {server.tags.length > 0 && (
                      <div className="welcome-vault__tags">
                        {server.tags.map((tag) => (
                          <span key={tag} className="welcome-vault__tag">{tag}</span>
                        ))}
                      </div>
                    )}
                  </button>
                </div>
              ))}
            </div>
          </div>
        )}

        {recentConnections.length > 0 && (
          <div className="welcome-recent">
            <h2 className="welcome-recent__header">Recent Connections</h2>
            <div className="welcome-recent__list">
              {recentConnections.map((connection) => (
                <div
                  key={connection.id}
                  className={`welcome-recent__item${hoveredId === `recent-${connection.id}` ? ' welcome-recent__item--hover' : ''}`}
                  onMouseEnter={() => setHoveredId(`recent-${connection.id}`)}
                  onMouseLeave={() => setHoveredId(null)}
                >
                  <button
                    type="button"
                    className="welcome-recent__connect"
                    onClick={() => handleSelectRecent(connection)}
                    aria-label={`Connect to ${connection.name}`}
                  >
                    <span className="welcome-recent__provider-badge">
                      {getProviderIcon(connection.provider)}
                    </span>
                    <div className="welcome-recent__info">
                      <span className="welcome-recent__name">{connection.name}</span>
                      <span className="welcome-recent__meta">
                        {getProviderName(connection.provider)} &middot; {connection.server} &middot; {formatDate(connection.connectedAt)}
                      </span>
                    </div>
                  </button>
                  <button
                    type="button"
                    className="welcome-recent__delete"
                    onClick={(e) => handleDeleteRecent(e, connection.id)}
                    aria-label={`Remove ${connection.name}`}
                  >
                    &times;
                  </button>
                </div>
              ))}
            </div>
          </div>
        )}

        <footer className="welcome-footer">
          <p>
            BifrostQL v1.0.0 &middot;{' '}
            <a
              href="https://github.com/standardbeagle/bifrostql"
              target="_blank"
              rel="noopener noreferrer"
              className="welcome-footer__link"
            >
              Documentation
            </a>
          </p>
        </footer>
      </div>
    </div>
  );
};

export default WelcomePanel;
