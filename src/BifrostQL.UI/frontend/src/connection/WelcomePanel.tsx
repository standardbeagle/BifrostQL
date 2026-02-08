import React, { useCallback, useEffect } from 'react';
import { ConnectionInfo } from './types';

const RECENT_CONNECTIONS_KEY = 'bifrostql_recent_connections';
const MAX_RECENT_CONNECTIONS = 5;

const styles = {
  container: {
    display: 'flex',
    flexDirection: 'column' as const,
    alignItems: 'center',
    justifyContent: 'center',
    minHeight: '100vh',
    padding: '2rem',
    gap: '2rem',
  } as React.CSSProperties,
  content: {
    display: 'flex',
    flexDirection: 'column' as const,
    alignItems: 'center',
    gap: '1.5rem',
    maxWidth: '600px',
    width: '100%',
  } as React.CSSProperties,
  logo: {
    display: 'flex',
    flexDirection: 'column' as const,
    alignItems: 'center',
    gap: '0.5rem',
  } as React.CSSProperties,
  logoIcon: {
    width: '4rem',
    height: '4rem',
    background: 'linear-gradient(135deg, #3b82f6 0%, #8b5cf6 100%)',
    borderRadius: '1rem',
    display: 'flex',
    alignItems: 'center',
    justifyContent: 'center',
    fontSize: '2rem',
    fontWeight: '700',
    color: 'white',
    marginBottom: '0.5rem',
  } as React.CSSProperties,
  title: {
    fontSize: '2rem',
    fontWeight: '700',
    margin: 0,
    background: 'linear-gradient(135deg, #3b82f6 0%, #8b5cf6 100%)',
    WebkitBackgroundClip: 'text' as const,
    WebkitTextFillColor: 'transparent',
    backgroundClip: 'text' as const,
  } as React.CSSProperties,
  subtitle: {
    fontSize: '1rem',
    color: 'var(--color-text-secondary, #94a3b8)',
    margin: 0,
    textAlign: 'center' as const,
  } as React.CSSProperties,
  cardContainer: {
    display: 'grid',
    gridTemplateColumns: 'repeat(auto-fit, minmax(240px, 1fr))',
    gap: '1rem',
    width: '100%',
  } as React.CSSProperties,
  card: {
    padding: '1.5rem',
    border: '1px solid var(--color-border, #334155)',
    borderRadius: '0.75rem',
    backgroundColor: 'var(--color-bg-secondary, #1e293b)',
    cursor: 'pointer',
    transition: 'all 0.2s',
    display: 'flex',
    flexDirection: 'column' as const,
    gap: '0.75rem',
  } as React.CSSProperties,
  cardHover: {
    borderColor: 'var(--color-primary, #3b82f6)',
    transform: 'translateY(-2px)',
    boxShadow: '0 4px 20px rgba(59, 130, 246, 0.15)',
  } as React.CSSProperties,
  cardIcon: {
    fontSize: '2rem',
    marginBottom: '0.25rem',
  } as React.CSSProperties,
  cardTitle: {
    fontSize: '1rem',
    fontWeight: '600',
    margin: 0,
    color: 'var(--color-text-primary, #e2e8f0)',
  } as React.CSSProperties,
  cardDescription: {
    fontSize: '0.875rem',
    margin: 0,
    color: 'var(--color-text-secondary, #94a3b8)',
    lineHeight: '1.5',
  } as React.CSSProperties,
  recentSection: {
    width: '100%',
    maxWidth: '600px',
  } as React.CSSProperties,
  recentHeader: {
    display: 'flex',
    justifyContent: 'space-between',
    alignItems: 'center',
    marginBottom: '1rem',
  } as React.CSSProperties,
  recentTitle: {
    fontSize: '0.875rem',
    fontWeight: '600',
    margin: 0,
    color: 'var(--color-text-secondary, #94a3b8)',
    textTransform: 'uppercase',
    letterSpacing: '0.05em',
  } as React.CSSProperties,
  clearButton: {
    background: 'none',
    border: 'none',
    color: 'var(--color-text-secondary, #94a3b8)',
    fontSize: '0.75rem',
    cursor: 'pointer',
    padding: '0.25rem 0.5rem',
    borderRadius: '0.25rem',
    transition: 'all 0.2s',
  } as React.CSSProperties,
  clearButtonHover: {
    color: 'var(--color-danger, #ef4444)',
    backgroundColor: 'rgba(239, 68, 68, 0.1)',
  } as React.CSSProperties,
  recentList: {
    display: 'flex',
    flexDirection: 'column' as const,
    gap: '0.5rem',
  } as React.CSSProperties,
  recentItem: {
    display: 'flex',
    alignItems: 'center',
    justifyContent: 'space-between',
    padding: '0.75rem 1rem',
    border: '1px solid var(--color-border, #334155)',
    borderRadius: '0.5rem',
    backgroundColor: 'var(--color-bg-secondary, #1e293b)',
    cursor: 'pointer',
    transition: 'all 0.2s',
  } as React.CSSProperties,
  recentItemHover: {
    borderColor: 'var(--color-primary, #3b82f6)',
    backgroundColor: 'rgba(59, 130, 246, 0.05)',
  } as React.CSSProperties,
  recentItemInfo: {
    display: 'flex',
    flexDirection: 'column' as const,
    gap: '0.125rem',
  } as React.CSSProperties,
  recentItemName: {
    fontSize: '0.875rem',
    fontWeight: '500',
    color: 'var(--color-text-primary, #e2e8f0)',
    margin: 0,
  } as React.CSSProperties,
  recentItemMeta: {
    fontSize: '0.75rem',
    color: 'var(--color-text-secondary, #94a3b8)',
    margin: 0,
  } as React.CSSProperties,
  recentItemArrow: {
    fontSize: '1rem',
    color: 'var(--color-text-secondary, #94a3b8)',
  } as React.CSSProperties,
  divider: {
    width: '100%',
    height: '1px',
    backgroundColor: 'var(--color-border, #334155)',
    margin: '1rem 0',
  } as React.CSSProperties,
  footer: {
    marginTop: 'auto',
    textAlign: 'center' as const,
    fontSize: '0.75rem',
    color: 'var(--color-text-secondary, #94a3b8)',
  } as React.CSSProperties,
  footerLink: {
    color: 'var(--color-primary, #3b82f6)',
    textDecoration: 'none',
  } as React.CSSProperties,
};

interface WelcomePanelProps {
  onConnectClick: () => void;
  onCreateTestDatabase: () => void;
  recentConnections?: ConnectionInfo[];
  onSelectRecentConnection?: (connection: ConnectionInfo) => void;
  onClearRecentConnections?: () => void;
}

const saveRecentConnections = (connections: ConnectionInfo[]): void => {
  if (typeof window === 'undefined') return;
  try {
    localStorage.setItem(RECENT_CONNECTIONS_KEY, JSON.stringify(connections));
  } catch (error) {
    console.warn('Failed to save recent connections:', error);
  }
};

const loadRecentConnections = (): ConnectionInfo[] => {
  if (typeof window === 'undefined') return [];
  try {
    const stored = localStorage.getItem(RECENT_CONNECTIONS_KEY);
    if (stored) {
      const connections = JSON.parse(stored) as ConnectionInfo[];
      return connections.slice(0, MAX_RECENT_CONNECTIONS);
    }
  } catch (error) {
    console.warn('Failed to load recent connections:', error);
  }
  return [];
};

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

export const WelcomePanel: React.FC<WelcomePanelProps> = ({
  onConnectClick,
  onCreateTestDatabase,
  recentConnections: externalRecentConnections,
  onSelectRecentConnection,
  onClearRecentConnections,
}) => {
  const [internalRecentConnections, setInternalRecentConnections] = React.useState<ConnectionInfo[]>([]);
  const [isHovered, setIsHovered] = React.useState<string | null>(null);
  const [isClearHovered, setIsClearHovered] = React.useState(false);

  const recentConnections = externalRecentConnections ?? internalRecentConnections;

  useEffect(() => {
    if (externalRecentConnections === undefined) {
      setInternalRecentConnections(loadRecentConnections());
    }
  }, [externalRecentConnections]);

  const handleSelectRecentConnection = useCallback((connection: ConnectionInfo) => {
    if (onSelectRecentConnection) {
      onSelectRecentConnection(connection);
    }
  }, [onSelectRecentConnection]);

  const handleClearRecentConnections = useCallback(() => {
    setInternalRecentConnections([]);
    saveRecentConnections([]);
    if (onClearRecentConnections) {
      onClearRecentConnections();
    }
  }, [onClearRecentConnections]);

  const CardButton: React.FC<{
    icon: string;
    title: string;
    description: string;
    onClick: () => void;
    testId: string;
  }> = ({ icon, title, description, onClick, testId }) => (
    <button
      type="button"
      onClick={onClick}
      data-testid={testId}
      style={{
        ...styles.card,
        ...(isHovered === testId ? styles.cardHover : {}),
      }}
      onMouseEnter={() => setIsHovered(testId)}
      onMouseLeave={() => setIsHovered(null)}
    >
      <div style={styles.cardIcon}>{icon}</div>
      <h3 style={styles.cardTitle}>{title}</h3>
      <p style={styles.cardDescription}>{description}</p>
    </button>
  );

  const RecentConnectionItem: React.FC<{ connection: ConnectionInfo; index: number }> = ({ connection, index }) => (
    <button
      type="button"
      onClick={() => handleSelectRecentConnection(connection)}
      style={{
        ...styles.recentItem,
        ...(isHovered === `recent-${index}` ? styles.recentItemHover : {}),
      }}
      onMouseEnter={() => setIsHovered(`recent-${index}`)}
      onMouseLeave={() => setIsHovered(null)}
      aria-label={`Connect to ${connection.name}`}
    >
      <div style={styles.recentItemInfo}>
        <p style={styles.recentItemName}>{connection.name}</p>
        <p style={styles.recentItemMeta}>
          {connection.server} · {formatDate(connection.connectedAt)}
        </p>
      </div>
      <span style={styles.recentItemArrow}>→</span>
    </button>
  );

  return (
    <div style={styles.container}>
      <div style={styles.content}>
        <div style={styles.logo}>
          <div style={styles.logoIcon}>BQ</div>
          <h1 style={styles.title}>BifrostQL</h1>
          <p style={styles.subtitle}>
            Explore your SQL Server databases with GraphQL
          </p>
        </div>

        <div style={styles.cardContainer}>
          <CardButton
            icon="🔗"
            title="Connect to Database"
            description="Connect to an existing SQL Server database with your connection string"
            onClick={onConnectClick}
            testId="connect-card"
          />
          <CardButton
            icon="🧪"
            title="Create Test Database"
            description="Spin up a sample database to explore BifrostQL features"
            onClick={onCreateTestDatabase}
            testId="test-db-card"
          />
        </div>

        {recentConnections.length > 0 && (
          <>
            <div style={styles.divider} />
            <div style={styles.recentSection}>
              <div style={styles.recentHeader}>
                <h2 style={styles.recentTitle}>Recent Connections</h2>
                <button
                  type="button"
                  onClick={handleClearRecentConnections}
                  style={{
                    ...styles.clearButton,
                    ...(isClearHovered ? styles.clearButtonHover : {}),
                  }}
                  onMouseEnter={() => setIsClearHovered(true)}
                  onMouseLeave={() => setIsClearHovered(false)}
                  aria-label="Clear recent connections"
                >
                  Clear
                </button>
              </div>
              <div style={styles.recentList}>
                {recentConnections.map((connection, index) => (
                  <RecentConnectionItem key={connection.id} connection={connection} index={index} />
                ))}
              </div>
            </div>
          </>
        )}

        <footer style={styles.footer}>
          <p>
            BifrostQL v1.0.0 ·{' '}
            <a
              href="https://github.com/standardbeagle/bifrostql"
              target="_blank"
              rel="noopener noreferrer"
              style={styles.footerLink}
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

export { saveRecentConnections, loadRecentConnections, MAX_RECENT_CONNECTIONS };
