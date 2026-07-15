import { describe, it, expect } from 'vitest';
import { parseFormDefinition, visibleFields, type FormDefinition } from './form-definition';

describe('parseFormDefinition', () => {
  it('parses a well-formed definition and defaults optional fields', () => {
    const def = parseFormDefinition({
      table: 'dbo.users',
      title: 'User',
      columns: 2,
      fields: [
        { column: 'id', label: 'Id', control: 'number', readOnly: true, required: false, include: true },
        { column: 'name', label: 'Name', control: 'text', required: true },
      ],
    });

    expect(def).not.toBeNull();
    expect(def!.table).toBe('dbo.users');
    expect(def!.columns).toBe(2);
    // Missing include defaults to true; missing readOnly to false.
    expect(def!.fields[1]).toEqual({
      column: 'name',
      label: 'Name',
      control: 'text',
      readOnly: false,
      required: true,
      include: true,
    });
  });

  it('rejects a definition with no table or no parseable fields', () => {
    expect(parseFormDefinition(null)).toBeNull();
    expect(parseFormDefinition({ title: 'x', fields: [] })).toBeNull();
    expect(parseFormDefinition({ table: 'users', fields: [] })).toBeNull();
    // A fields array with only unparseable entries yields nothing to render.
    expect(parseFormDefinition({ table: 'users', fields: [{}, { column: 42 }] })).toBeNull();
  });

  it('drops unparseable fields but keeps the rest (tolerant)', () => {
    const def = parseFormDefinition({
      table: 'users',
      fields: [{ column: 'id' }, { nope: true }, { column: 'name', label: 'Name' }],
    });
    expect(def!.fields.map((f) => f.column)).toEqual(['id', 'name']);
  });

  it('clamps layout columns and falls back to an unknown control as text', () => {
    const def = parseFormDefinition({
      table: 'users',
      columns: 99,
      fields: [{ column: 'id', control: 'wat' }],
    });
    expect(def!.columns).toBe(4);
    expect(def!.fields[0].control).toBe('text');
    expect(def!.title).toBe('users'); // title defaults to the table
  });
});

describe('visibleFields', () => {
  it('returns only included fields in order', () => {
    const def: FormDefinition = {
      table: 'users',
      title: 'Users',
      columns: 1,
      fields: [
        { column: 'a', label: 'A', control: 'text', readOnly: false, required: false, include: true },
        { column: 'b', label: 'B', control: 'text', readOnly: false, required: false, include: false },
        { column: 'c', label: 'C', control: 'text', readOnly: false, required: false, include: true },
      ],
    };
    expect(visibleFields(def).map((f) => f.column)).toEqual(['a', 'c']);
  });
});
