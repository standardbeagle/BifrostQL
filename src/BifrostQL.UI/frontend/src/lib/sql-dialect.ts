/**
 * Maps a connection profile's provider to the matching CodeMirror `@codemirror/lang-sql`
 * dialect, so the editor tokenises, highlights, and completes against the syntax the
 * active database actually speaks (identifier quoting, keywords, string rules).
 *
 * The four providers here are the same closed set the connection UI exposes
 * (`connection/types.ts`). There is deliberately no fallback dialect: an unknown
 * provider is a data-integrity bug upstream, not something to paper over with a
 * best-guess dialect that would mis-tokenise identifiers.
 */

import { MSSQL, MySQL, PostgreSQL, SQLite, type SQLDialect } from '@codemirror/lang-sql';
import type { Provider } from '../connection/types';

export function dialectForProvider(provider: Provider): SQLDialect {
  switch (provider) {
    case 'sqlserver':
      return MSSQL;
    case 'postgres':
      return PostgreSQL;
    case 'mysql':
      return MySQL;
    case 'sqlite':
      return SQLite;
    default: {
      // Exhaustiveness guard: a new Provider must add a case here.
      const unreachable: never = provider;
      throw new Error(`Unsupported SQL provider: ${String(unreachable)}`);
    }
  }
}
