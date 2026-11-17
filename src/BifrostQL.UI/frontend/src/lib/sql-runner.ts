/**
 * Executes a parsed batch of SQL statements sequentially and collects a per-statement
 * outcome (result set, or error text pinned to the statement's source offset). Kept
 * separate from the React console so the batch semantics are unit-testable against a
 * stub executor without a DOM or the Photino bridge:
 *
 * - run order is preserved, one outcome per executed statement;
 * - the batch stops at the first failing statement. Later statements are reported as
 *   skipped, never run: they were written assuming the earlier ones succeeded, and a
 *   mis-split block body (or a failed CREATE) must not leave its tail running on its own.
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

export interface SqlBatchResult {
  /** One outcome per statement that ran; the last one carries the error when the batch stopped. */
  outcomes: StatementOutcome[];
  /** Statements not run because an earlier statement failed. */
  skipped: SqlStatement[];
}

export async function runSqlStatements(
  statements: SqlStatement[],
  exec: (sql: string) => Promise<SqlResult>,
  clock: () => number = () => performance.now(),
): Promise<SqlBatchResult> {
  const outcomes: StatementOutcome[] = [];
  for (let i = 0; i < statements.length; i++) {
    const statement = statements[i];
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
      return { outcomes, skipped: statements.slice(i + 1) };
    }
  }
  return { outcomes, skipped: [] };
}
