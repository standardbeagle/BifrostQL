import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import CodeMirror, { type ReactCodeMirrorRef } from '@uiw/react-codemirror';
import { EditorView, keymap } from '@codemirror/view';
import { Prec } from '@codemirror/state';
import { sql } from '@codemirror/lang-sql';
import { BridgeError } from './lib/native-bridge';
import { execSql, isSqlBridgeAvailable, type SqlResult } from './lib/sql-bridge';
import { getBuilderSchema, type BuilderSchema } from './lib/builder-bridge';
import { createSqlLanguageSupport } from './lib/sql-completion';
import { dialectForProvider } from './lib/sql-dialect';
import { splitSqlStatements } from './lib/sql-statements';
import { runSqlStatements, type StatementOutcome } from './lib/sql-runner';
import { recordSqlStatement } from './lib/sql-history';
import { ResultGrid } from './ResultGrid';
import type { Provider } from './connection/types';

/**
 * Raw SQL console backed by the Photino `exec-sql` bridge. Runs SQL against the host's
 * active connection (no GraphQL, no HTTP) with a CodeMirror 6 editor: dialect-correct
 * highlighting, schema-aware autocomplete (tables after FROM, columns after alias-dot),
 * and whole-buffer / selection execution split into per-statement result blocks.
 *
 * Full DML/DDL is intentionally allowed — this is a desktop-bridge-only, local-user
 * surface with no server-side arbitrary-SQL path. Non-query statements report an
 * affected-row count; errors are pinned to the failing statement.
 *
 * Only functional inside the desktop shell; in a plain browser the bridge is absent and
 * the console shows an explanatory notice instead of the editor.
 */

interface SqlConsoleProps {
  /**
   * Active connection provider, used to pick the SQL dialect for highlighting,
   * completion, and statement splitting. When the connection provider is not yet known
   * SQLite is used as a neutral parsing default — all four supported dialects delimit
   * statements with `;`, and completion still resolves against the fetched schema.
   */
  provider?: Provider;
}

// Dark theme mapped onto the shell's Norse-Industrial tokens so the editor matches the
// surrounding chrome rather than shipping CodeMirror's default light look.
const sqlEditorTheme = EditorView.theme(
  {
    '&': {
      color: 'var(--text-primary)',
      backgroundColor: 'var(--void-surface)',
      fontSize: '13px',
      height: '100%',
      borderRadius: '6px',
      border: '1px solid var(--void-border)',
    },
    '.cm-content': {
      fontFamily: 'var(--font-mono, ui-monospace, monospace)',
      caretColor: 'var(--text-bright)',
    },
    '.cm-cursor, .cm-dropCursor': { borderLeftColor: 'var(--text-bright)' },
    '.cm-gutters': {
      backgroundColor: 'var(--void)',
      color: 'var(--text-muted)',
      border: 'none',
    },
    '.cm-activeLine': { backgroundColor: 'var(--void-highlight)' },
    '.cm-activeLineGutter': { backgroundColor: 'var(--void-highlight)' },
    '&.cm-focused > .cm-scroller > .cm-selectionLayer .cm-selectionBackground, .cm-selectionBackground, .cm-content ::selection':
      { backgroundColor: 'var(--void-elevated)' },
    '.cm-tooltip': {
      backgroundColor: 'var(--void-elevated)',
      border: '1px solid var(--void-border)',
      color: 'var(--text-primary)',
    },
    '.cm-tooltip-autocomplete ul li[aria-selected]': {
      backgroundColor: 'var(--accent-action)',
      color: 'var(--text-bright)',
    },
  },
  { dark: true },
);

export function SqlConsole({ provider }: SqlConsoleProps) {
  // SQLite is the neutral parsing default until the connection provider is known.
  const activeProvider: Provider = provider ?? 'sqlite';
  const bridgeAvailable = isSqlBridgeAvailable();

  const cmRef = useRef<ReactCodeMirrorRef>(null);
  const [value, setValue] = useState('');
  const [schema, setSchema] = useState<BuilderSchema | null>(null);
  const [outcomes, setOutcomes] = useState<StatementOutcome[]>([]);
  const [runError, setRunError] = useState<string | null>(null);
  const [running, setRunning] = useState(false);

  // Fetch the active connection's schema once for completion. A failure here only
  // disables autocomplete — the console still runs SQL — so it is not surfaced as an
  // error; the editor simply falls back to keyword-only completion.
  useEffect(() => {
    if (!bridgeAvailable) return;
    let cancelled = false;
    getBuilderSchema()
      .then((s) => !cancelled && setSchema(s))
      .catch(() => {
        /* completion is best-effort; execution does not depend on the schema */
      });
    return () => {
      cancelled = true;
    };
  }, [bridgeAvailable]);

  // `run` is rebuilt on each relevant change; the editor keymap reaches it through a ref
  // so the Mod-Enter binding always calls the latest closure without reconfiguring the
  // editor's extensions on every keystroke.
  const runRef = useRef<() => void>(() => {});

  const run = useCallback(async () => {
    const view = cmRef.current?.view;
    if (!view || running) return;
    // Selection-execute: run only the selection when there is one, else the whole buffer.
    const sel = view.state.selection.main;
    const source = sel.empty
      ? view.state.doc.toString()
      : view.state.sliceDoc(sel.from, sel.to);

    const statements = splitSqlStatements(source, activeProvider);
    if (statements.length === 0) return;

    setRunning(true);
    setRunError(null);
    try {
      const next = await runSqlStatements(statements, (s) => execSql(s));
      setOutcomes(next);
      for (const outcome of next) recordSqlStatement(outcome.statement.text);
    } catch (err) {
      // A throw here is a bridge-level failure (transport/timeout), not a per-statement
      // SQL error — those are captured inside runSqlStatements as outcomes.
      setOutcomes([]);
      setRunError(err instanceof BridgeError || err instanceof Error ? err.message : String(err));
    } finally {
      setRunning(false);
    }
  }, [running, activeProvider]);
  runRef.current = run;

  const extensions = useMemo(() => {
    const language = schema
      ? createSqlLanguageSupport(activeProvider, schema)
      : sql({ dialect: dialectForProvider(activeProvider), upperCaseKeywords: true });
    const runKeymap = Prec.highest(
      keymap.of([
        {
          key: 'Mod-Enter',
          preventDefault: true,
          run: () => {
            runRef.current();
            return true;
          },
        },
      ]),
    );
    return [language, sqlEditorTheme, runKeymap, EditorView.lineWrapping];
  }, [activeProvider, schema]);

  if (!bridgeAvailable) {
    return (
      <div className="sql-console sql-console--unavailable">
        The raw SQL console is only available in the BifrostQL desktop app.
      </div>
    );
  }

  const canRun = !running && value.trim().length > 0;

  return (
    <div className="sql-console">
      <div className="sql-console__editor">
        <CodeMirror
          ref={cmRef}
          value={value}
          onChange={setValue}
          extensions={extensions}
          theme="none"
          height="100%"
          basicSetup={{ lineNumbers: true, autocompletion: true, highlightActiveLine: true }}
          placeholder="SELECT * FROM …   (Ctrl+Enter to run; select text to run only the selection)"
        />
      </div>

      <div className="sql-console__toolbar">
        <button
          type="button"
          className="sql-console__run"
          onClick={() => void run()}
          disabled={!canRun}
        >
          {running ? 'Running…' : 'Run (Ctrl+Enter)'}
        </button>
        <span className="sql-console__hint">
          Runs the selection if any, otherwise the whole buffer.
        </span>
      </div>

      {runError && (
        <div className="sql-console__error" role="alert">
          {runError}
        </div>
      )}

      <div className="sql-console__results">
        {outcomes.map((outcome, i) => (
          <StatementResult key={i} index={i} outcome={outcome} />
        ))}
      </div>
    </div>
  );
}

/** One result block per executed statement: a grid, an affected-row count, or an error. */
function StatementResult({ index, outcome }: { index: number; outcome: StatementOutcome }) {
  const label = `Statement ${index + 1}`;
  const time = ` · ${outcome.elapsedMs} ms`;

  if (outcome.error) {
    return (
      <div className="sql-console__statement sql-console__statement--error">
        <div className="sql-console__statement-head">
          {label} · error at offset {outcome.statement.from}
          {time}
        </div>
        <div className="sql-console__error" role="alert">
          {outcome.error}
        </div>
      </div>
    );
  }

  const result = outcome.result as SqlResult;
  const isQuery = result.columns.length > 0;
  return (
    <div className="sql-console__statement">
      <div className="sql-console__statement-head">
        {label} · {summarize(result)}
        {time}
      </div>
      {isQuery && <ResultGrid columns={result.columns} rows={result.rows} />}
    </div>
  );
}

function summarize(result: SqlResult): string {
  if (result.columns.length === 0) return `${result.rowsAffected} row(s) affected`;
  const truncated = result.truncated ? ' (truncated)' : '';
  return `${result.rows.length} row(s)${truncated}`;
}
