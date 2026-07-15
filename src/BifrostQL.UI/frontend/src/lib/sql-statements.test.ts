import { describe, expect, it } from 'vitest';
import { splitSqlStatements } from './sql-statements';

describe('splitSqlStatements', () => {
  it('splits a multi-statement buffer into separate statements', () => {
    // Act
    const stmts = splitSqlStatements('SELECT 1; SELECT 2; SELECT 3', 'sqlite');
    // Assert
    expect(stmts.map((s) => s.text)).toEqual(['SELECT 1', 'SELECT 2', 'SELECT 3']);
  });

  it('does not split on a semicolon inside a string literal', () => {
    // Arrange — the ';' lives inside 'a;b', not a statement separator.
    const sql = "UPDATE t SET note = 'a;b' WHERE id = 1";
    // Act
    const stmts = splitSqlStatements(sql, 'sqlite');
    // Assert — one statement, the embedded ';' preserved (tree, not regex).
    expect(stmts).toHaveLength(1);
    expect(stmts[0].text).toBe(sql);
  });

  it('does not split on a semicolon inside a comment', () => {
    // Arrange — a line comment containing a ';' between two statements.
    const sql = 'SELECT 1; -- keep this; comment\nSELECT 2';
    // Act
    const stmts = splitSqlStatements(sql, 'sqlite');
    // Assert — comment is not executed and did not create a spurious split.
    expect(stmts.map((s) => s.text)).toEqual(['SELECT 1', 'SELECT 2']);
  });

  it('carries each statement offset for error reporting', () => {
    // Arrange
    const sql = 'SELECT 1;\nSELECT bad_col FROM t';
    // Act
    const stmts = splitSqlStatements(sql, 'sqlite');
    // Assert — the second statement starts after the newline.
    expect(stmts[1].from).toBe(sql.indexOf('SELECT bad_col'));
  });

  it('keeps DDL statements intact (CREATE TABLE / DROP parity)', () => {
    // Arrange — the console must not filter DDL out; the desktop bridge allows it.
    const sql = 'CREATE TABLE widget (id INTEGER PRIMARY KEY, name TEXT); DROP TABLE widget';
    // Act
    const stmts = splitSqlStatements(sql, 'sqlite');
    // Assert
    expect(stmts.map((s) => s.text)).toEqual([
      'CREATE TABLE widget (id INTEGER PRIMARY KEY, name TEXT)',
      'DROP TABLE widget',
    ]);
  });

  it('ignores trailing whitespace and empty trailing statements', () => {
    // Act
    const stmts = splitSqlStatements('SELECT 1;   \n  ', 'sqlite');
    // Assert
    expect(stmts.map((s) => s.text)).toEqual(['SELECT 1']);
  });
});
