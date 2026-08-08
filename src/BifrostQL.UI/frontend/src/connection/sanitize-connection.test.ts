import { describe, expect, it } from 'vitest';
import { parseAdoConnectionString, parsePort } from './sanitize-connection';

describe('parsePort', () => {
  it('accepts exact integer ports in range', () => {
    expect(parsePort('5432')).toBe(5432);
    expect(parsePort(' 3306 ')).toBe(3306);
    expect(parsePort(1433)).toBe(1433);
  });

  it('rejects partial, decimal, empty, and out-of-range ports', () => {
    expect(parsePort('5432abc')).toBeUndefined();
    expect(parsePort('12.5')).toBeUndefined();
    expect(parsePort('')).toBeUndefined();
    expect(parsePort('0')).toBeUndefined();
    expect(parsePort('65536')).toBeUndefined();
  });
});

/**
 * A pasted connection string is a statement of intent. Dropping its
 * TrustServerCertificate would save an entry that validates, which then fails
 * against the self-signed server the string was written for — and inventing one
 * where the string has none would waive validation the user never gave up.
 */
describe('parseAdoConnectionString TrustServerCertificate', () => {
  it('carries an explicit trust waiver', () => {
    const parsed = parseAdoConnectionString(
      'Server=db;Database=app;User Id=sa;TrustServerCertificate=True',
      'sqlserver',
    );
    expect(parsed.trustServerCertificate).toBe(true);
  });

  it('reads an explicit false as validating', () => {
    const parsed = parseAdoConnectionString(
      'Server=db;Database=app;TrustServerCertificate=False',
      'sqlserver',
    );
    expect(parsed.trustServerCertificate).toBe(false);
  });

  it('leaves it unset when the string says nothing', () => {
    const parsed = parseAdoConnectionString('Server=db;Database=app', 'sqlserver');
    expect(parsed.trustServerCertificate).toBeUndefined();
  });
});
