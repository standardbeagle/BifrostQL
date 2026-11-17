/**
 * Splits a SQL buffer into individual statements by walking the leaf tokens of the
 * `@codemirror/lang-sql` Lezer parse tree — NOT a regex over the text. The grammar
 * models strings (including Postgres `$$` bodies), quoted identifiers, and line/block
 * comments as their own tokens, so a `;` inside any of them never reaches the splitter.
 *
 * The grammar itself is flat: it ends a `Statement` at every `;`, including the `;`s
 * inside a `BEGIN … END` body. Splitting on its `Statement` nodes would cut
 * `CREATE TRIGGER … BEGIN UPDATE b …; UPDATE c …; END;` into three statements and run
 * the body statements on their own. So the splitter tracks block depth over the tokens:
 *
 * - `BEGIN` opens a block unless it starts a transaction (`BEGIN;`,
 *   `BEGIN TRANSACTION`, `BEGIN IMMEDIATE`, …). `CASE` opens one too, since it also
 *   closes with `END`.
 * - `END` closes one, except MySQL's `END IF` / `END LOOP` / `END WHILE` / `END REPEAT`,
 *   whose openers are not counted.
 * - A `;` ends a statement only at depth 0.
 * - SQL Server: `GO` alone on its line ends the batch (and the statement) and is never
 *   sent; `CREATE`/`ALTER` `PROCEDURE`/`FUNCTION`/`TRIGGER`/`VIEW` runs to the end of its
 *   batch, because T-SQL makes those bodies span the whole batch with or without
 *   `BEGIN … END`.
 *
 * Comments between statements are dropped from execution. Each statement carries its
 * offsets in the original buffer so callers can report errors against the editor.
 */

import { dialectForProvider } from './sql-dialect';
import type { Provider } from '../connection/types';

export interface SqlStatement {
  /** Executable statement text, trailing `;` and surrounding whitespace stripped. */
  text: string;
  /** Character offset of the statement's first token in the source buffer. */
  from: number;
  /** Character offset just past the statement (including its separator). */
  to: number;
}

interface Token {
  /** Node name: `Keyword`, `String`, `;`, `LineComment`, … */
  name: string;
  /** Upper-cased token text for keyword comparisons. */
  upper: string;
  from: number;
  to: number;
}

// Lezer's IterMode.IncludeAnonymous: the `;` separator is an anonymous token, and
// @lezer/common is not a direct dependency of this package.
const INCLUDE_ANONYMOUS = 2;

const TRANSACTION_BEGIN_FOLLOWERS = new Set([
  'TRANSACTION',
  'TRAN',
  'WORK',
  'DEFERRED',
  'IMMEDIATE',
  'EXCLUSIVE',
  'DISTRIBUTED',
  'ISOLATION',
]);
const UNCOUNTED_END_FOLLOWERS = new Set(['IF', 'LOOP', 'WHILE', 'REPEAT']);
const BATCH_SCOPED_OBJECTS = new Set(['PROCEDURE', 'PROC', 'FUNCTION', 'TRIGGER', 'VIEW']);

function isComment(token: Token): boolean {
  return token.name === 'LineComment' || token.name === 'BlockComment';
}

function readTokens(source: string, provider: Provider): Token[] {
  const tree = dialectForProvider(provider).language.parser.parse(source);
  const tokens: Token[] = [];
  tree.iterate({
    mode: INCLUDE_ANONYMOUS,
    enter: (node) => {
      if (node.node.firstChild || node.to <= node.from) return;
      const text = source.slice(node.from, node.to);
      if (text.trim().length === 0) return;
      tokens.push({ name: node.name, upper: text.toUpperCase(), from: node.from, to: node.to });
    },
  });
  return tokens;
}

/** True when `token` is `GO` standing alone on its line (an optional repeat count allowed). */
function isGoSeparator(source: string, token: Token): boolean {
  if (token.upper !== 'GO') return false;
  const lineStart = source.lastIndexOf('\n', token.from - 1) + 1;
  const lineEndAt = source.indexOf('\n', token.to);
  const lineEnd = lineEndAt === -1 ? source.length : lineEndAt;
  return (
    source.slice(lineStart, token.from).trim().length === 0 &&
    /^\s*\d*\s*$/.test(source.slice(token.to, lineEnd))
  );
}

export function splitSqlStatements(
  source: string,
  provider: Provider,
  /** Added to every offset, so a selection's statements report buffer positions. */
  baseOffset = 0,
): SqlStatement[] {
  const tokens = readTokens(source, provider);
  const isMsSql = provider === 'sqlserver';
  const statements: SqlStatement[] = [];

  let start: number | null = null;
  let end = 0;
  let depth = 0;
  let batchScoped = false;
  // Index of the statement's first significant token, for the T-SQL CREATE check.
  let firstIndex = -1;

  const significantAfter = (i: number): Token | undefined => {
    for (let j = i + 1; j < tokens.length; j++) if (!isComment(tokens[j])) return tokens[j];
    return undefined;
  };

  const flush = () => {
    if (start !== null) {
      const text = source.slice(start, end).replace(/;\s*$/, '').trim();
      if (text.length > 0) {
        statements.push({ text, from: start + baseOffset, to: end + baseOffset });
      }
    }
    start = null;
    depth = 0;
    batchScoped = false;
    firstIndex = -1;
  };

  for (let i = 0; i < tokens.length; i++) {
    const token = tokens[i];

    if (isMsSql && isGoSeparator(source, token)) {
      flush();
      continue;
    }
    if (start === null) {
      if (isComment(token) || token.name === ';') continue;
      start = token.from;
      firstIndex = i;
      if (isMsSql) batchScoped = isBatchScopedCreate(tokens, i);
    }
    end = token.to;

    if (token.name === ';') {
      if (depth === 0 && !batchScoped) flush();
      continue;
    }
    if (token.name !== 'Keyword' || i < firstIndex) continue;

    if (token.upper === 'BEGIN') {
      const next = significantAfter(i);
      const startsTransaction =
        next === undefined || next.name === ';' || TRANSACTION_BEGIN_FOLLOWERS.has(next.upper);
      if (!startsTransaction) depth++;
    } else if (token.upper === 'CASE') {
      depth++;
    } else if (token.upper === 'END') {
      const next = significantAfter(i);
      if (!(next && UNCOUNTED_END_FOLLOWERS.has(next.upper))) depth = Math.max(0, depth - 1);
    }
  }
  flush();

  return statements;
}

/** `CREATE [OR ALTER] | ALTER` followed by PROCEDURE / FUNCTION / TRIGGER / VIEW. */
function isBatchScopedCreate(tokens: Token[], i: number): boolean {
  const words: string[] = [];
  for (let j = i; j < tokens.length && words.length < 4; j++) {
    if (!isComment(tokens[j])) words.push(tokens[j].upper);
  }
  if (words[0] === 'ALTER') return BATCH_SCOPED_OBJECTS.has(words[1]);
  if (words[0] !== 'CREATE') return false;
  if (words[1] === 'OR' && words[2] === 'ALTER') return BATCH_SCOPED_OBJECTS.has(words[3]);
  return BATCH_SCOPED_OBJECTS.has(words[1]);
}
