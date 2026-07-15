import { describe, expect, it } from 'vitest';
import { EditorState } from '@codemirror/state';
import { CompletionContext, type CompletionResult } from '@codemirror/autocomplete';
import { SQLite } from '@codemirror/lang-sql';
import { buildSchemaCompletionMap, createSchemaCompletionSource } from './sql-completion';
import type { BuilderSchema } from './builder-bridge';

// A two-table mock schema standing in for the live DbModel the bridge would return.
const mockSchema: BuilderSchema = {
  tables: [
    { schema: 'main', name: 'users', qualified: 'main.users' },
    { schema: 'main', name: 'orders', qualified: 'main.orders' },
  ],
  columns: [
    { table: 'main.users', name: 'id', type: 'integer', nullable: false, isPrimaryKey: true },
    { table: 'main.users', name: 'email', type: 'text', nullable: false, isPrimaryKey: false },
    { table: 'main.orders', name: 'id', type: 'integer', nullable: false, isPrimaryKey: true },
    { table: 'main.orders', name: 'user_id', type: 'integer', nullable: false, isPrimaryKey: false },
  ],
  relationships: [],
};

/** Runs the completion source at `pos` in `doc` and returns the offered labels. */
function completionLabels(doc: string, pos: number): string[] {
  const source = createSchemaCompletionSource('sqlite', mockSchema);
  const state = EditorState.create({ doc, extensions: [SQLite] });
  const result = source(new CompletionContext(state, pos, true)) as CompletionResult | null;
  return result ? result.options.map((o) => o.label) : [];
}

describe('buildSchemaCompletionMap', () => {
  it('keys columns by unqualified table name', () => {
    // Act
    const map = buildSchemaCompletionMap(mockSchema);
    // Assert
    expect(map).toEqual({ users: ['id', 'email'], orders: ['id', 'user_id'] });
  });

  it('unions columns when two schemas share a bare table name', () => {
    // Arrange — same "users" name under two different schemas.
    const collided: BuilderSchema = {
      tables: [
        { schema: 'main', name: 'users', qualified: 'main.users' },
        { schema: 'audit', name: 'users', qualified: 'audit.users' },
      ],
      columns: [
        { table: 'main.users', name: 'id', type: 'integer', nullable: false, isPrimaryKey: true },
        { table: 'audit.users', name: 'changed_at', type: 'text', nullable: false, isPrimaryKey: false },
      ],
      relationships: [],
    };
    // Act
    const map = buildSchemaCompletionMap(collided);
    // Assert — no column dropped.
    expect(map.users).toEqual(['id', 'changed_at']);
  });
});

describe('createSchemaCompletionSource', () => {
  it('offers table names after FROM', () => {
    // Arrange — cursor sits right after "FROM ".
    const doc = 'SELECT * FROM ';
    // Act
    const labels = completionLabels(doc, doc.length);
    // Assert — acceptance: tables after FROM on a mock schema.
    expect(labels).toContain('users');
    expect(labels).toContain('orders');
  });

  it('offers the aliased table columns after alias-dot', () => {
    // Arrange — "u" aliases users; cursor is right after "u.".
    const doc = 'SELECT u. FROM users u';
    // Act
    const labels = completionLabels(doc, 'SELECT u.'.length);
    // Assert — acceptance: columns scoped to the FROM-clause table after alias-dot.
    expect(labels).toEqual(expect.arrayContaining(['id', 'email']));
    expect(labels).not.toContain('user_id'); // orders column must not leak in
  });
});
