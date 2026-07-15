import { describe, it, expect } from 'vitest';
import { resolveWidget, controlRender } from './form-widget';
import type { FormField } from './form-definition';

function field(overrides: Partial<FormField> = {}): FormField {
  return {
    column: 'name',
    label: 'Name',
    control: 'text',
    readOnly: false,
    required: false,
    include: true,
    ...overrides,
  };
}

describe('resolveWidget', () => {
  it('uses the saved control when there is no metadata hint', () => {
    expect(resolveWidget(field({ control: 'number' }))).toEqual({
      control: 'number',
      readOnly: false,
      visible: true,
    });
  });

  it('lets a recognised metadata widget override the saved control', () => {
    expect(resolveWidget(field({ control: 'text' }), { widget: 'select' }).control).toBe('select');
    // An unknown widget hint is ignored — the saved control stands.
    expect(resolveWidget(field({ control: 'text' }), { widget: 'fancy' }).control).toBe('text');
  });

  it('takes read-only from either source (union)', () => {
    expect(resolveWidget(field({ readOnly: true }), { readOnly: false }).readOnly).toBe(true);
    expect(resolveWidget(field({ readOnly: false }), { readOnly: true }).readOnly).toBe(true);
    expect(resolveWidget(field({ readOnly: false }), {}).readOnly).toBe(false);
  });

  it('hides a field when excluded or when metadata marks it invisible', () => {
    expect(resolveWidget(field({ include: false })).visible).toBe(false);
    expect(resolveWidget(field({ include: true }), { visible: false }).visible).toBe(false);
    expect(resolveWidget(field({ include: true }), { visible: true }).visible).toBe(true);
  });
});

describe('controlRender', () => {
  it('maps each control to its element', () => {
    expect(controlRender('text')).toEqual({ kind: 'input', type: 'text' });
    expect(controlRender('number')).toEqual({ kind: 'input', type: 'number' });
    expect(controlRender('date')).toEqual({ kind: 'input', type: 'date' });
    expect(controlRender('datetime')).toEqual({ kind: 'input', type: 'datetime-local' });
    expect(controlRender('textarea')).toEqual({ kind: 'textarea' });
    expect(controlRender('checkbox')).toEqual({ kind: 'checkbox' });
    expect(controlRender('select')).toEqual({ kind: 'select' });
  });
});
