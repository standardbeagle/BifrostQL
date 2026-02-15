import { describe, it, expect, beforeEach, afterEach, vi } from 'vitest';
import {
  serializeSort,
  parseSort,
  serializeFilter,
  parseFilter,
  writeToUrl,
  readFromUrl,
} from './url-state';

describe('serializeSort', () => {
  it('serializes a single sort option', () => {
    expect(serializeSort([{ field: 'name', direction: 'asc' }])).toBe(
      'name:asc',
    );
  });

  it('serializes multiple sort options', () => {
    expect(
      serializeSort([
        { field: 'name', direction: 'asc' },
        { field: 'created_at', direction: 'desc' },
      ]),
    ).toBe('name:asc,created_at:desc');
  });

  it('returns empty string for empty array', () => {
    expect(serializeSort([])).toBe('');
  });
});

describe('parseSort', () => {
  it('parses a single sort option', () => {
    expect(parseSort('name:asc')).toEqual([
      { field: 'name', direction: 'asc' },
    ]);
  });

  it('parses multiple sort options', () => {
    expect(parseSort('name:asc,created_at:desc')).toEqual([
      { field: 'name', direction: 'asc' },
      { field: 'created_at', direction: 'desc' },
    ]);
  });

  it('returns empty array for empty string', () => {
    expect(parseSort('')).toEqual([]);
  });

  it('skips invalid entries', () => {
    expect(parseSort('name:asc,bad:invalid,email:desc')).toEqual([
      { field: 'name', direction: 'asc' },
      { field: 'email', direction: 'desc' },
    ]);
  });

  it('skips entries without direction', () => {
    expect(parseSort('name')).toEqual([]);
  });
});

describe('serializeFilter', () => {
  it('serializes a filter object to JSON', () => {
    const filter = { status: 'active' };
    expect(serializeFilter(filter)).toBe('{"status":"active"}');
  });

  it('serializes nested filter operators', () => {
    const filter = { name: { _contains: 'alice' } };
    expect(serializeFilter(filter)).toBe('{"name":{"_contains":"alice"}}');
  });
});

describe('parseFilter', () => {
  it('parses a valid JSON filter', () => {
    expect(parseFilter('{"status":"active"}')).toEqual({ status: 'active' });
  });

  it('parses nested filter operators', () => {
    expect(parseFilter('{"name":{"_contains":"alice"}}')).toEqual({
      name: { _contains: 'alice' },
    });
  });

  it('returns undefined for empty string', () => {
    expect(parseFilter('')).toBeUndefined();
  });

  it('returns undefined for invalid JSON', () => {
    expect(parseFilter('not-json')).toBeUndefined();
  });

  it('returns undefined for non-object JSON values', () => {
    expect(parseFilter('"string"')).toBeUndefined();
    expect(parseFilter('42')).toBeUndefined();
    expect(parseFilter('null')).toBeUndefined();
    expect(parseFilter('[1,2]')).toBeUndefined();
  });
});

describe('writeToUrl', () => {
  let replaceStateMock: ReturnType<typeof vi.fn>;
  let originalReplaceState: typeof window.history.replaceState;

  beforeEach(() => {
    Object.defineProperty(window, 'location', {
      writable: true,
      value: { href: 'http://localhost/users', search: '' },
    });
    originalReplaceState = window.history.replaceState;
    replaceStateMock = vi.fn();
    window.history.replaceState = replaceStateMock;
  });

  afterEach(() => {
    window.history.replaceState = originalReplaceState;
  });

  it('writes sort to URL', () => {
    writeToUrl({ sort: [{ field: 'name', direction: 'asc' }] }, 'table');
    const url = replaceStateMock.mock.calls[0][2] as string;
    expect(url).toContain('table_sort=name%3Aasc');
  });

  it('writes page to URL', () => {
    writeToUrl({ page: 2 }, 'table');
    const url = replaceStateMock.mock.calls[0][2] as string;
    expect(url).toContain('table_page=2');
  });

  it('writes pageSize to URL', () => {
    writeToUrl({ pageSize: 50 }, 'table');
    const url = replaceStateMock.mock.calls[0][2] as string;
    expect(url).toContain('table_size=50');
  });

  it('writes filter to URL', () => {
    writeToUrl({ filter: { status: 'active' } }, 'table');
    const url = replaceStateMock.mock.calls[0][2] as string;
    expect(url).toContain('table_filter=');
  });

  it('removes params when values are empty', () => {
    writeToUrl({ sort: [], page: 0, filter: {} }, 'table');
    const url = replaceStateMock.mock.calls[0][2] as string;
    expect(url).not.toContain('table_sort');
    expect(url).not.toContain('table_page');
    expect(url).not.toContain('table_filter');
  });

  it('uses custom prefix', () => {
    writeToUrl({ sort: [{ field: 'name', direction: 'asc' }] }, 'grid');
    const url = replaceStateMock.mock.calls[0][2] as string;
    expect(url).toContain('grid_sort=');
  });
});

describe('readFromUrl', () => {
  afterEach(() => {
    Object.defineProperty(window, 'location', {
      writable: true,
      value: { href: 'http://localhost/', search: '' },
    });
  });

  it('reads sort from URL', () => {
    Object.defineProperty(window, 'location', {
      writable: true,
      value: {
        href: 'http://localhost/users?table_sort=name:asc,created_at:desc',
        search: '?table_sort=name:asc,created_at:desc',
      },
    });

    const state = readFromUrl('table');
    expect(state.sort).toEqual([
      { field: 'name', direction: 'asc' },
      { field: 'created_at', direction: 'desc' },
    ]);
  });

  it('reads page from URL', () => {
    Object.defineProperty(window, 'location', {
      writable: true,
      value: {
        href: 'http://localhost/users?table_page=2',
        search: '?table_page=2',
      },
    });

    const state = readFromUrl('table');
    expect(state.page).toBe(2);
  });

  it('reads pageSize from URL', () => {
    Object.defineProperty(window, 'location', {
      writable: true,
      value: {
        href: 'http://localhost/users?table_size=50',
        search: '?table_size=50',
      },
    });

    const state = readFromUrl('table');
    expect(state.pageSize).toBe(50);
  });

  it('reads filter from URL', () => {
    const filter = encodeURIComponent('{"status":"active"}');
    Object.defineProperty(window, 'location', {
      writable: true,
      value: {
        href: `http://localhost/users?table_filter=${filter}`,
        search: `?table_filter=${filter}`,
      },
    });

    const state = readFromUrl('table');
    expect(state.filter).toEqual({ status: 'active' });
  });

  it('returns empty object when no params present', () => {
    Object.defineProperty(window, 'location', {
      writable: true,
      value: { href: 'http://localhost/users', search: '' },
    });

    const state = readFromUrl('table');
    expect(state).toEqual({});
  });

  it('uses custom prefix', () => {
    Object.defineProperty(window, 'location', {
      writable: true,
      value: {
        href: 'http://localhost/users?grid_page=3',
        search: '?grid_page=3',
      },
    });

    const state = readFromUrl('grid');
    expect(state.page).toBe(3);
  });

  it('ignores invalid page values', () => {
    Object.defineProperty(window, 'location', {
      writable: true,
      value: {
        href: 'http://localhost/users?table_page=abc',
        search: '?table_page=abc',
      },
    });

    const state = readFromUrl('table');
    expect(state.page).toBeUndefined();
  });

  it('ignores negative page values', () => {
    Object.defineProperty(window, 'location', {
      writable: true,
      value: {
        href: 'http://localhost/users?table_page=-1',
        search: '?table_page=-1',
      },
    });

    const state = readFromUrl('table');
    expect(state.page).toBeUndefined();
  });

  it('ignores invalid pageSize values', () => {
    Object.defineProperty(window, 'location', {
      writable: true,
      value: {
        href: 'http://localhost/users?table_size=0',
        search: '?table_size=0',
      },
    });

    const state = readFromUrl('table');
    expect(state.pageSize).toBeUndefined();
  });
});

describe('SSR compatibility', () => {
  it('readFromUrl returns empty object when window is undefined', () => {
    const originalWindow = globalThis.window;
    // @ts-expect-error - deliberately removing window for SSR test
    delete globalThis.window;

    const state = readFromUrl('table');
    expect(state).toEqual({});

    globalThis.window = originalWindow;
  });
});
