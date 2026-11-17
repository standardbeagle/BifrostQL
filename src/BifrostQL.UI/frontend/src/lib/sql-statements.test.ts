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

  it('keeps a SQLite trigger body (BEGIN … END) in one statement', () => {
    // Arrange — the body's own ';'s must not end the CREATE TRIGGER statement.
    const trigger =
      "CREATE TRIGGER t AFTER INSERT ON a BEGIN UPDATE b SET x = 'a;b'; UPDATE c SET y = 2; END";
    // Act
    const stmts = splitSqlStatements(`${trigger};\nSELECT 1`, 'sqlite');
    // Assert — no body statement escapes as a standalone UPDATE.
    expect(stmts.map((s) => s.text)).toEqual([trigger, 'SELECT 1']);
  });

  it('closes a CASE … END inside a trigger body without ending the block', () => {
    const trigger =
      'CREATE TRIGGER t AFTER INSERT ON a BEGIN UPDATE b SET x = CASE WHEN 1 THEN 2 ELSE 3 END; DELETE FROM c; END';
    const stmts = splitSqlStatements(`${trigger}; SELECT 2`, 'sqlite');
    expect(stmts.map((s) => s.text)).toEqual([trigger, 'SELECT 2']);
  });

  it('treats BEGIN TRANSACTION and a bare BEGIN as ordinary statements', () => {
    const stmts = splitSqlStatements(
      'BEGIN TRANSACTION; INSERT INTO t VALUES (1); COMMIT; BEGIN; SELECT 1; END',
      'sqlite',
    );
    expect(stmts.map((s) => s.text)).toEqual([
      'BEGIN TRANSACTION',
      'INSERT INTO t VALUES (1)',
      'COMMIT',
      'BEGIN',
      'SELECT 1',
      'END',
    ]);
  });

  it('keeps a MySQL procedure body with END IF in one statement', () => {
    const proc = 'CREATE PROCEDURE p() BEGIN IF 1 THEN SELECT 1; END IF; SELECT 2; END';
    const stmts = splitSqlStatements(`${proc}; SELECT 3`, 'mysql');
    expect(stmts.map((s) => s.text)).toEqual([proc, 'SELECT 3']);
  });

  it('splits SQL Server batches on GO and never sends GO itself', () => {
    // Arrange — T-SQL: the procedure body runs to the end of its batch.
    const proc = 'CREATE PROCEDURE p AS BEGIN SELECT 1; SELECT 2; END';
    const sql = `${proc}\nGO\nSELECT 3; SELECT 4`;
    // Act
    const stmts = splitSqlStatements(sql, 'sqlserver');
    // Assert
    expect(stmts.map((s) => s.text)).toEqual([proc, 'SELECT 3', 'SELECT 4']);
  });

  it('runs a SQL Server procedure without BEGIN … END to the end of its batch', () => {
    const proc = 'CREATE OR ALTER PROCEDURE p AS SELECT 1; SELECT 2';
    const stmts = splitSqlStatements(`${proc};\ngo\nSELECT 3`, 'sqlserver');
    expect(stmts.map((s) => s.text)).toEqual([proc, 'SELECT 3']);
  });

  it('does not treat GO inside a line as a batch separator', () => {
    const stmts = splitSqlStatements("SELECT 'GO' AS go_col; SELECT 1", 'sqlserver');
    expect(stmts.map((s) => s.text)).toEqual(["SELECT 'GO' AS go_col", 'SELECT 1']);
  });

  it('keeps a Postgres $$ function body in one statement', () => {
    const fn = 'CREATE FUNCTION f() RETURNS int AS $$ BEGIN RETURN 1; END; $$ LANGUAGE plpgsql';
    const stmts = splitSqlStatements(`${fn}; SELECT f()`, 'postgres');
    expect(stmts.map((s) => s.text)).toEqual([fn, 'SELECT f()']);
  });

  it('reports offsets relative to the whole buffer when given a base offset', () => {
    // Arrange — a selection that starts 20 characters into the buffer.
    const stmts = splitSqlStatements('SELECT 1; SELECT 2', 'sqlite', 20);
    expect(stmts.map((s) => s.from)).toEqual([20, 30]);
  });
});
