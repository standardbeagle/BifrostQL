import { describe, it, expect, beforeEach } from 'vitest';
import {
  readColumnPresetsFromLocalStorage,
  readFiltersFromLocalStorage,
  readPresetsFromLocalStorage,
  readSortFromLocalStorage,
  writeColumnPresetsToLocalStorage,
  writeFiltersToLocalStorage,
  writePresetsToLocalStorage,
  writeSortToLocalStorage,
} from './table-storage';

beforeEach(() => {
  window.localStorage.clear();
});

describe('sort persistence', () => {
  it('round-trips valid sort options', () => {
    writeSortToLocalStorage('k', [{ field: 'name', direction: 'asc' }]);
    expect(readSortFromLocalStorage('k')).toEqual([
      { field: 'name', direction: 'asc' },
    ]);
  });

  it('removes the key when writing an empty sort', () => {
    writeSortToLocalStorage('k', [{ field: 'name', direction: 'asc' }]);
    writeSortToLocalStorage('k', []);
    expect(window.localStorage.getItem('k')).toBeNull();
    expect(readSortFromLocalStorage('k')).toBeNull();
  });

  it('returns null for malformed JSON and drops invalid entries', () => {
    window.localStorage.setItem('bad', '{not json');
    expect(readSortFromLocalStorage('bad')).toBeNull();
    window.localStorage.setItem(
      'mixed',
      JSON.stringify([
        { field: 'a', direction: 'asc' },
        { field: 'b', direction: 'sideways' },
      ]),
    );
    expect(readSortFromLocalStorage('mixed')).toEqual([
      { field: 'a', direction: 'asc' },
    ]);
  });
});

describe('filter persistence', () => {
  it('round-trips filters under the _filters suffix', () => {
    writeFiltersToLocalStorage('k', { name: { _eq: 'x' } });
    expect(window.localStorage.getItem('k_filters')).not.toBeNull();
    expect(readFiltersFromLocalStorage('k')).toEqual({ name: { _eq: 'x' } });
  });

  it('removes the key when writing empty filters', () => {
    writeFiltersToLocalStorage('k', { name: { _eq: 'x' } });
    writeFiltersToLocalStorage('k', {});
    expect(window.localStorage.getItem('k_filters')).toBeNull();
  });
});

describe('filter preset persistence', () => {
  it('round-trips presets and keeps only valid entries', () => {
    writePresetsToLocalStorage('k', [
      { name: 'p1', filters: { a: { _eq: 1 } } },
    ]);
    expect(readPresetsFromLocalStorage('k')).toEqual([
      { name: 'p1', filters: { a: { _eq: 1 } } },
    ]);
  });

  it('returns empty array when nothing is stored', () => {
    expect(readPresetsFromLocalStorage('missing')).toEqual([]);
  });
});

describe('column preset persistence', () => {
  it('round-trips column presets', () => {
    const preset = {
      name: 'c1',
      visibleColumns: ['a'],
      columnOrder: ['a', 'b'],
      columnWidths: { a: 100 },
      pinnedColumns: { a: 'left' as const },
    };
    writeColumnPresetsToLocalStorage('k', [preset]);
    expect(readColumnPresetsFromLocalStorage('k')).toEqual([preset]);
  });

  it('drops entries missing required keys', () => {
    window.localStorage.setItem(
      'k_columnPresets',
      JSON.stringify([{ name: 'bad' }]),
    );
    expect(readColumnPresetsFromLocalStorage('k')).toEqual([]);
  });
});
