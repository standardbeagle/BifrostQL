import { describe, it, expect, vi, afterEach } from 'vitest';
import type { ColumnConfig } from '@bifrostql/react';
import { serializeCsv, downloadCsv } from './csv-export';

const columns: ColumnConfig[] = [
  { field: 'name', header: 'Name' },
  { field: 'email', header: 'Email' },
  { field: 'note', header: 'Note' },
];

describe('serializeCsv', () => {
  it('emits a header row from column headers followed by data rows', () => {
    // Arrange
    const rows = [
      { name: 'Ada', email: 'ada@example.com', note: 'founder' },
      { name: 'Grace', email: 'grace@example.com', note: 'admiral' },
    ];

    // Act
    const csv = serializeCsv(rows, columns);

    // Assert
    expect(csv).toBe(
      'Name,Email,Note\r\n' +
        'Ada,ada@example.com,founder\r\n' +
        'Grace,grace@example.com,admiral',
    );
  });

  it('quotes fields containing a comma', () => {
    // Arrange
    const rows = [{ name: 'Lovelace, Ada', email: 'a@x.com', note: '' }];

    // Act
    const csv = serializeCsv(rows, columns);

    // Assert
    expect(csv).toContain('"Lovelace, Ada"');
  });

  it('quotes fields containing a double-quote and doubles the quote', () => {
    // Arrange
    const rows = [{ name: 'Ada "the" Lovelace', email: 'a@x.com', note: '' }];

    // Act
    const csv = serializeCsv(rows, columns);

    // Assert
    expect(csv).toContain('"Ada ""the"" Lovelace"');
  });

  it('quotes fields containing newlines', () => {
    // Arrange
    const rows = [{ name: 'line1\nline2', email: 'a@x.com', note: 'c\r\nd' }];

    // Act
    const csv = serializeCsv(rows, columns);

    // Assert
    expect(csv).toContain('"line1\nline2"');
    expect(csv).toContain('"c\r\nd"');
  });

  it('renders null and undefined cells as empty fields', () => {
    // Arrange
    const rows = [{ name: null, email: undefined, note: 'x' }];

    // Act
    const csv = serializeCsv(rows, columns);

    // Assert
    expect(csv).toBe('Name,Email,Note\r\n,,x');
  });

  it('only emits the columns it is given, ignoring extra row fields', () => {
    // Arrange: row carries a finance field the column list omits.
    const rows = [
      { name: 'Ada', email: 'a@x.com', note: 'n', amount_cents: 5000 },
    ];

    // Act
    const csv = serializeCsv(rows, columns);

    // Assert
    expect(csv).not.toContain('5000');
    expect(csv).not.toContain('amount_cents');
  });

  it('serializes an empty row list to just the header', () => {
    // Act
    const csv = serializeCsv([], columns);

    // Assert
    expect(csv).toBe('Name,Email,Note');
  });
});

describe('downloadCsv', () => {
  afterEach(() => {
    vi.restoreAllMocks();
  });

  it('triggers a download via a transient anchor and revokes the object URL', () => {
    // Arrange
    const createObjectURL = vi
      .fn()
      .mockReturnValue('blob:fake-url');
    const revokeObjectURL = vi.fn();
    vi.stubGlobal('URL', { createObjectURL, revokeObjectURL });
    const clickSpy = vi
      .spyOn(HTMLAnchorElement.prototype, 'click')
      .mockImplementation(() => {});

    // Act
    downloadCsv('a,b\r\n1,2', 'members.csv');

    // Assert
    expect(createObjectURL).toHaveBeenCalledOnce();
    expect(clickSpy).toHaveBeenCalledOnce();
    expect(revokeObjectURL).toHaveBeenCalledWith('blob:fake-url');
    // The transient anchor is removed again.
    expect(document.querySelector('a[download]')).toBeNull();

    vi.unstubAllGlobals();
  });
});
