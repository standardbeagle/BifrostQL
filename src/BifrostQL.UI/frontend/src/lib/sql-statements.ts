/**
 * Splits a SQL buffer into individual statements using the `@codemirror/lang-sql`
 * Lezer parse tree — NOT a regex. The grammar already models strings, line/block
 * comments, and identifiers as their own nodes, so a `;` inside a string literal or a
 * comment never surfaces as a top-level statement separator. Regex splitting on `;`
 * cannot make that distinction and would sever `WHERE note = 'a;b'` or `-- drop;` in the
 * wrong place; the tree makes it correct by construction.
 *
 * The tree shape (verified against lang-sql's grammar): the root `Script` has one
 * `Statement` child per statement (each spanning its trailing `;` when present) plus
 * standalone comment nodes between them. We collect the `Statement` nodes, so bare
 * comments between statements are dropped from execution — which is what we want.
 *
 * Each returned statement carries its `from` offset in the original buffer so callers can
 * report errors against the statement's position in the editor.
 */

import { dialectForProvider } from './sql-dialect';
import type { Provider } from '../connection/types';

export interface SqlStatement {
  /** Executable statement text, trailing `;` and surrounding whitespace stripped. */
  text: string;
  /** Character offset of the statement's first token in the source buffer. */
  from: number;
  /** Character offset just past the statement (before/including the separator). */
  to: number;
}

export function splitSqlStatements(source: string, provider: Provider): SqlStatement[] {
  const parser = dialectForProvider(provider).language.parser;
  const tree = parser.parse(source);
  const statements: SqlStatement[] = [];

  const cursor = tree.cursor();
  if (cursor.firstChild()) {
    do {
      if (cursor.name !== 'Statement') continue;
      const raw = source.slice(cursor.from, cursor.to);
      // Strip a single trailing separator plus surrounding whitespace; the `;` lives
      // inside the Statement node's range but is not part of the executable text.
      const text = raw.replace(/;\s*$/, '').trim();
      if (text.length === 0) continue;
      statements.push({ text, from: cursor.from, to: cursor.to });
    } while (cursor.nextSibling());
  }

  return statements;
}
