import React, { useEffect, useState } from 'react';

interface Diagnostics {
  hostVersion?: string | null;
  serverVersion?: string | null;
  runtime?: string | null;
  os?: string | null;
  connected?: boolean;
  provider?: string | null;
}

interface AboutPanelProps {
  onBack: () => void;
}

const row: React.CSSProperties = {
  display: 'flex',
  justifyContent: 'space-between',
  gap: 24,
  padding: '8px 0',
  borderBottom: '1px solid #e5e7eb',
  fontSize: 14,
};
const label: React.CSSProperties = { color: '#6b7280' };
const value: React.CSSProperties = { fontFamily: 'monospace', textAlign: 'right' };

/**
 * About / diagnostics view. Shows the frontend (SPA), host (.NET desktop
 * shell) and server (BifrostQL GraphQL engine) versions side by side. These
 * are built and versioned separately, so a mismatch is flagged loudly — a
 * stale build on any side is the usual cause of subtle runtime bugs.
 */
export const AboutPanel: React.FC<AboutPanelProps> = ({ onBack }) => {
  const uiVersion = __APP_VERSION__;
  const [diag, setDiag] = useState<Diagnostics | null>(null);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    let cancelled = false;
    fetch('/api/diagnostics')
      .then((r) => {
        if (!r.ok) throw new Error(`Server returned ${r.status}`);
        return r.json();
      })
      .then((d) => { if (!cancelled) setDiag(d as Diagnostics); })
      .catch((e) => { if (!cancelled) setError(String(e?.message ?? e)); });
    return () => { cancelled = true; };
  }, []);

  const versions = [
    ['UI (frontend)', uiVersion],
    ['Host (.NET shell)', diag?.hostVersion ?? null],
    ['Backend (server)', diag?.serverVersion ?? null],
  ] as const;

  const known = versions.map(([, v]) => v).filter((v): v is string => !!v);
  const mismatch = new Set(known).size > 1;

  return (
    <div className="bifrost-connection-container">
      <div style={{ maxWidth: 560, margin: '48px auto', padding: 24 }}>
        <div style={{ display: 'flex', alignItems: 'center', gap: 12, marginBottom: 24 }}>
          <div className="bifrost-header__logo">B</div>
          <h1 style={{ margin: 0, fontSize: 22 }}>About BifrostQL</h1>
        </div>

        {mismatch && (
          <div
            role="alert"
            style={{
              background: '#fef3c7',
              border: '1px solid #f59e0b',
              color: '#92400e',
              borderRadius: 6,
              padding: '10px 14px',
              marginBottom: 16,
              fontSize: 13,
            }}
          >
            ⚠ Version mismatch — the frontend, host and server are not all on the
            same version. Rebuild the stale component to avoid runtime drift.
          </div>
        )}

        <section style={{ marginBottom: 24 }}>
          <h2 style={{ fontSize: 13, textTransform: 'uppercase', color: '#9ca3af', letterSpacing: 0.5 }}>
            Versions
          </h2>
          {versions.map(([name, v]) => (
            <div style={row} key={name}>
              <span style={label}>{name}</span>
              <span style={{ ...value, color: !v ? '#ef4444' : mismatch ? '#b45309' : '#111827' }}>
                {v ? `v${v}` : 'unknown'}
              </span>
            </div>
          ))}
        </section>

        <section style={{ marginBottom: 24 }}>
          <h2 style={{ fontSize: 13, textTransform: 'uppercase', color: '#9ca3af', letterSpacing: 0.5 }}>
            Diagnostics
          </h2>
          {error && (
            <div style={{ ...row, color: '#ef4444' }}>
              <span style={label}>Diagnostics</span>
              <span style={value}>unavailable: {error}</span>
            </div>
          )}
          {[
            ['.NET runtime', diag?.runtime],
            ['OS', diag?.os],
            ['Database', diag?.connected ? (diag?.provider ?? 'connected') : 'not connected'],
          ].map(([name, v]) => (
            <div style={row} key={String(name)}>
              <span style={label}>{name}</span>
              <span style={value}>{v ? String(v) : '—'}</span>
            </div>
          ))}
        </section>

        <div style={{ display: 'flex', gap: 12 }}>
          <button className="bifrost-disconnect-button" onClick={onBack}>Back</button>
          <a
            href="https://github.com/standardbeagle/bifrostql"
            target="_blank"
            rel="noopener noreferrer"
            className="welcome-footer__link"
            style={{ alignSelf: 'center' }}
          >
            Documentation
          </a>
        </div>
      </div>
    </div>
  );
};
