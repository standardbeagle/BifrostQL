import { describe, expect, it } from 'vitest';
import { MSSQL, MySQL, PostgreSQL, SQLite } from '@codemirror/lang-sql';
import { dialectForProvider } from './sql-dialect';
import type { Provider } from '../connection/types';

describe('dialectForProvider', () => {
  it('maps each provider to its lang-sql dialect', () => {
    // Arrange / Act / Assert — identity check against the canonical dialects.
    expect(dialectForProvider('sqlserver')).toBe(MSSQL);
    expect(dialectForProvider('postgres')).toBe(PostgreSQL);
    expect(dialectForProvider('mysql')).toBe(MySQL);
    expect(dialectForProvider('sqlite')).toBe(SQLite);
  });

  it('throws on an unknown provider instead of guessing a dialect', () => {
    // Arrange — a value outside the closed Provider union (bad upstream data).
    const bogus = 'oracle' as unknown as Provider;
    // Act / Assert — fail fast, no fallback dialect.
    expect(() => dialectForProvider(bogus)).toThrow(/Unsupported SQL provider/);
  });
});
