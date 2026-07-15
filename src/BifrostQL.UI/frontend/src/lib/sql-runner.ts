/**
 * Executes a parsed batch of SQL statements sequentially and collects a per-statement
 * outcome (result set, or error text pinned to the statement's source offset). Kept
 * separate from the React console so the batch semantics — one row per statement, run
 * order preserved, an error on one statement captured rather than aborting the batch —
 * are unit-testable against a stub executor without a DOM or the Photino bridge.
 *
 * The executor is injected (the console passes `execSql`), so nothing here knows about
 * the bridge transport; the console remains the only place that talks to the host.
 */

import type { SqlResult } from './sql-bridge';
import type { SqlStatement } from './sql-statements';

export interface StatementOutcome {
  statement: SqlStatement;
  /** Present when the statement succeeded. */
  result?: SqlResult;
  /** Present when the statement failed; the message to surface at its offset. */
  error?: string;
  elapsedMs: number;
}

export async function runSqlStatements(
  statements: SqlStatement[],
  exec: (sql: string) => Promise<SqlResult>,
  clock: () => number = () => performance.now(),
): Promise<StatementOutcome[]> {
  const outcomes: StatementOutcome[] = [];
  for (const statement of statements) {
    const started = clock();
    try {
      const result = await exec(statement.text);
      outcomes.push({ statement, result, elapsedMs: Math.round(clock() - started) });
    } catch (err) {
      outcomes.push({
        statement,
        error: err instanceof Error ? err.message : String(err),
        elapsedMs: Math.round(clock() - started),
      });
    }
  }
  return outcomes;
}
