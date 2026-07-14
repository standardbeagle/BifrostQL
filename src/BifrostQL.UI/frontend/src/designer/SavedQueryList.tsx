import { useCallback, useEffect, useState } from "react";
import type { SavedObject } from "@standardbeagle/edit-db";
import { savedQueryStore, SAVED_QUERY_TYPE } from "./saved-query-store";

interface SavedQueryListProps {
  /** Id of the query currently open in the designer, highlighted in the list. */
  activeId: string | null;
  /** Bumped by the designer after a save/rename/delete to refetch the list. */
  reloadToken: number;
  onOpen: (query: SavedObject) => void;
}

/**
 * The shell's saved-query nav rail: every `type: 'query'` saved object, click to
 * open it in the designer. Read-only — creating, renaming, and deleting live in
 * the designer pane (which owns the design being saved); this list just refetches
 * when the pane says the store changed.
 */
export function SavedQueryList({ activeId, reloadToken, onOpen }: SavedQueryListProps) {
  const [queries, setQueries] = useState<SavedObject[]>([]);
  const [error, setError] = useState<string | null>(null);
  const [loading, setLoading] = useState(true);

  useEffect(() => {
    const controller = new AbortController();
    setLoading(true);
    savedQueryStore
      .list(SAVED_QUERY_TYPE, controller.signal)
      .then((found) => {
        setQueries([...found].sort((a, b) => a.name.localeCompare(b.name)));
        setError(null);
      })
      .catch((e) => {
        if (controller.signal.aborted) return;
        setError(e instanceof Error ? e.message : String(e));
      })
      .finally(() => !controller.signal.aborted && setLoading(false));
    return () => controller.abort();
  }, [reloadToken]);

  const open = useCallback((q: SavedObject) => onOpen(q), [onOpen]);

  return (
    <nav style={styles.root} aria-label="Saved queries">
      <h2 style={styles.heading}>Saved queries</h2>
      {error && <div role="alert" style={styles.error}>{error}</div>}
      {!error && loading && <div style={styles.empty}>Loading…</div>}
      {!error && !loading && queries.length === 0 && (
        <div style={styles.empty}>No saved queries yet. Design one, then Save.</div>
      )}
      <ul style={styles.list}>
        {queries.map((q) => (
          <li key={q.id}>
            <button
              type="button"
              onClick={() => open(q)}
              aria-current={q.id === activeId ? "true" : undefined}
              style={{ ...styles.item, ...(q.id === activeId ? styles.itemActive : null) }}
            >
              {q.name || "(untitled)"}
            </button>
          </li>
        ))}
      </ul>
    </nav>
  );
}

const styles: Record<string, React.CSSProperties> = {
  root: { width: 200, flexShrink: 0, display: "flex", flexDirection: "column", gap: 4, padding: "8px 0", borderRight: "1px solid var(--border, #d1d5db)", overflow: "auto" },
  heading: { margin: 0, padding: "0 12px 6px", fontSize: 11, textTransform: "uppercase", letterSpacing: "0.05em", color: "#6b7280" },
  list: { listStyle: "none", margin: 0, padding: 0 },
  item: { display: "block", width: "100%", textAlign: "left", padding: "6px 12px", border: "none", background: "transparent", cursor: "pointer", font: "inherit", fontSize: 13, color: "inherit" },
  itemActive: { background: "#eff6ff", color: "#1d4ed8", fontWeight: 600 },
  empty: { padding: "4px 12px", fontSize: 12, color: "#6b7280" },
  error: { padding: "4px 12px", fontSize: 12, color: "#b91c1c" },
};
