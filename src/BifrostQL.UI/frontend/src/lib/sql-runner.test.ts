import { describe, expect, it, vi } from 'vitest';
import { runSqlStatements } from './sql-runner';
import type { SqlResult } from './sql-bridge';
import type { SqlStatement } from './sql-statements';

const stmt = (text: string, from = 0): SqlStatement => ({ text, from, to: from + text.length });

const emptyResult: SqlResult = { columns: [], rows: [], rowsAffected: 1, truncated: false };

describe('runSqlStatements', () => {
  it('runs statements in order and returns one outcome each', async () => {
    // Arrange
    const exec = vi.fn(async (sql: string): Promise<SqlResult> => ({
      ...emptyResult,
      columns: [{ name: sql, type: 'text' }],
    }));
    // Act
    const outcomes = await runSqlStatements([stmt('SELECT 1'), stmt('SELECT 2')], exec);
    // Assert — order preserved, each statement executed once.
    expect(exec.mock.calls.map((c) => c[0])).toEqual(['SELECT 1', 'SELECT 2']);
    expect(outcomes.map((o) => o.result?.columns[0].name)).toEqual(['SELECT 1', 'SELECT 2']);
  });

  it('captures a per-statement error without aborting the batch', async () => {
    // Arrange — the middle statement throws.
    const exec = vi.fn(async (sql: string): Promise<SqlResult> => {
      if (sql === 'BOOM') throw new Error('syntax error near BOOM');
      return emptyResult;
    });
    // Act
    const outcomes = await runSqlStatements(
      [stmt('SELECT 1'), stmt('BOOM', 9), stmt('SELECT 2', 15)],
      exec,
    );
    // Assert — all three ran; the failure is pinned to its statement/offset.
    expect(outcomes).toHaveLength(3);
    expect(outcomes[1].error).toMatch(/BOOM/);
    expect(outcomes[1].statement.from).toBe(9);
    expect(outcomes[2].result).toBeDefined();
  });
});
