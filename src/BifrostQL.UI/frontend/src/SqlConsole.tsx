import { useCallback, useState } from 'react';
import { BridgeError } from './lib/native-bridge';
import {
  execSql,
  isSqlBridgeAvailable,
  type SqlResult,
} from './lib/sql-bridge';
import { ResultGrid } from './ResultGrid';

/**
 * Raw SQL console backed by the Photino `exec-sql` bridge. Runs arbitrary SQL
 * against the host's active connection (no GraphQL, no HTTP) and renders the
 * columnar result in a simple grid. Full DML/DDL is allowed — non-SELECT
 * statements report an affected-row count instead of a grid.
 *
 * Only functional inside the desktop shell; in a plain browser the bridge is
 * absent and the console shows an explanatory notice instead of the editor.
 */
export function SqlConsole() {
  const [sql, setSql] = useState('');
  const [result, setResult] = useState<SqlResult | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [running, setRunning] = useState(false);
  const [elapsedMs, setElapsedMs] = useState<number | null>(null);

  const bridgeAvailable = isSqlBridgeAvailable();

  const run = useCallback(async () => {
    const trimmed = sql.trim();
    if (!trimmed || running) return;

    setRunning(true);
    setError(null);
    const started = performance.now();
    try {
      const res = await execSql(trimmed);
      setResult(res);
      setElapsedMs(Math.round(performance.now() - started));
    } catch (err) {
      setResult(null);
      setElapsedMs(null);
      setError(
        err instanceof BridgeError || err instanceof Error
          ? err.message
          : String(err)
      );
    } finally {
      setRunning(false);
    }
  }, [sql, running]);

  const onKeyDown = useCallback(
    (e: React.KeyboardEvent<HTMLTextAreaElement>) => {
      // Ctrl/Cmd+Enter runs the query — standard SQL-console muscle memory.
      if ((e.ctrlKey || e.metaKey) && e.key === 'Enter') {
        e.preventDefault();
        void run();
      }
    },
    [run]
  );

  if (!bridgeAvailable) {
    return (
      <div className="sql-console sql-console--unavailable" style={styles.unavailable}>
        The raw SQL console is only available in the BifrostQL desktop app.
      </div>
    );
  }

  return (
    <div className="sql-console" style={styles.container}>
      <textarea
        className="sql-console__editor"
        style={styles.editor}
        value={sql}
        onChange={(e) => setSql(e.target.value)}
        onKeyDown={onKeyDown}
        placeholder="SELECT * FROM ...   (Ctrl+Enter to run)"
        spellCheck={false}
        aria-label="SQL query"
      />
      <div className="sql-console__toolbar" style={styles.toolbar}>
        <button
          type="button"
          onClick={() => void run()}
          disabled={running || !sql.trim()}
          style={styles.runButton}
        >
          {running ? 'Running…' : 'Run (Ctrl+Enter)'}
        </button>
        <span style={styles.status}>
          {error
            ? ''
            : result
              ? resultSummary(result, elapsedMs)
              : ''}
        </span>
      </div>

      {error && (
        <div className="sql-console__error" role="alert" style={styles.error}>
          {error}
        </div>
      )}

      {!error && result && result.columns.length > 0 && (
        <ResultGrid columns={result.columns} rows={result.rows} />
      )}
    </div>
  );
}

function resultSummary(result: SqlResult, elapsedMs: number | null): string {
  const time = elapsedMs == null ? '' : ` in ${elapsedMs} ms`;
  if (result.columns.length === 0) {
    return `${result.rowsAffected} row(s) affected${time}`;
  }
  const truncated = result.truncated ? ' (truncated)' : '';
  return `${result.rows.length} row(s)${truncated}${time}`;
}

// Inline styles mirror the inline-style convention already used in App.tsx for
// the transport toggle; promote to app.css if this console grows.
const styles: Record<string, React.CSSProperties> = {
  container: { display: 'flex', flexDirection: 'column', height: '100%', minHeight: 0 },
  editor: {
    width: '100%',
    minHeight: 120,
    resize: 'vertical',
    fontFamily: 'var(--font-mono, monospace)',
    fontSize: 13,
    padding: 12,
    border: '1px solid var(--border, #d1d5db)',
    borderRadius: 6,
    boxSizing: 'border-box',
  },
  toolbar: { display: 'flex', alignItems: 'center', gap: 12, padding: '8px 0' },
  runButton: {
    background: '#2563eb',
    color: '#fff',
    border: 'none',
    borderRadius: 6,
    padding: '6px 14px',
    cursor: 'pointer',
    font: 'inherit',
  },
  status: { fontSize: 12, color: '#6b7280' },
  error: {
    color: '#b91c1c',
    background: '#fef2f2',
    border: '1px solid #fecaca',
    borderRadius: 6,
    padding: 12,
    fontFamily: 'var(--font-mono, monospace)',
    fontSize: 12,
    whiteSpace: 'pre-wrap',
  },
  unavailable: { padding: 24, color: '#6b7280' },
};
