import { describe, it, expect, vi } from 'vitest';
import { render, screen, fireEvent } from '@testing-library/react';
import { FieldControl, resolveFieldKind } from './field-control';
import type { FieldMetadata } from '../metadata/types';

describe('resolveFieldKind', () => {
  it('maps known widget hints to their field kinds', () => {
    // Arrange / Act / Assert: each recognized widget resolves to its kind.
    expect(resolveFieldKind({ widget: 'date' })).toBe('date');
    expect(resolveFieldKind({ widget: 'datetime' })).toBe('date');
    expect(resolveFieldKind({ widget: 'boolean' })).toBe('boolean');
    expect(resolveFieldKind({ widget: 'checkbox' })).toBe('boolean');
    expect(resolveFieldKind({ widget: 'select' })).toBe('enum');
    expect(resolveFieldKind({ widget: 'enum' })).toBe('enum');
    expect(resolveFieldKind({ widget: 'json' })).toBe('json');
    expect(resolveFieldKind({ widget: 'textarea' })).toBe('json');
    expect(resolveFieldKind({ widget: 'fk' })).toBe('fk');
    expect(resolveFieldKind({ widget: 'foreignKey' })).toBe('fk');
  });

  it('falls back to scalar for unknown or missing widgets', () => {
    // Arrange / Act / Assert
    expect(resolveFieldKind(undefined)).toBe('scalar');
    expect(resolveFieldKind({})).toBe('scalar');
    expect(resolveFieldKind({ widget: 'mystery-widget' })).toBe('scalar');
  });

  it('normalizes widget case and whitespace', () => {
    // Arrange / Act / Assert
    expect(resolveFieldKind({ widget: '  DATE  ' })).toBe('date');
  });
});

describe('FieldControl dispatch', () => {
  it('renders a text input for scalar fields', () => {
    // Arrange / Act
    render(
      <FieldControl name="title" value="hello" onChange={vi.fn()} />,
    );

    // Assert: a text input is rendered with the current value.
    const input = screen.getByLabelText('title');
    expect(input).toHaveAttribute('type', 'text');
    expect(input).toHaveValue('hello');
  });

  it('renders a date input for date fields', () => {
    // Arrange / Act
    render(
      <FieldControl
        name="created"
        field={{ widget: 'date' }}
        value="2026-05-14"
        onChange={vi.fn()}
      />,
    );

    // Assert
    expect(screen.getByLabelText('created')).toHaveAttribute('type', 'date');
  });

  it('renders a checkbox for boolean fields and emits the checked state', () => {
    // Arrange
    const onChange = vi.fn();
    render(
      <FieldControl
        name="active"
        field={{ widget: 'boolean' }}
        value={false}
        onChange={onChange}
      />,
    );

    // Act
    fireEvent.click(screen.getByLabelText('active'));

    // Assert
    expect(onChange).toHaveBeenCalledWith(true);
  });

  it('renders a select for enum fields with the supplied options', () => {
    // Arrange / Act
    render(
      <FieldControl
        name="status"
        field={{ widget: 'select' }}
        enumOptions={['open', 'closed']}
        value="open"
        onChange={vi.fn()}
      />,
    );

    // Assert
    expect(screen.getByRole('option', { name: 'open' })).toBeInTheDocument();
    expect(screen.getByRole('option', { name: 'closed' })).toBeInTheDocument();
  });

  it('renders a textarea for json fields', () => {
    // Arrange / Act
    render(
      <FieldControl
        name="config"
        field={{ widget: 'json' }}
        value='{"a":1}'
        onChange={vi.fn()}
      />,
    );

    // Assert
    expect(screen.getByLabelText('config').tagName).toBe('TEXTAREA');
  });

  it('renders an fk lookup select carrying the target entity', () => {
    // Arrange / Act
    render(
      <FieldControl
        name="owner_id"
        field={{ widget: 'fk' }}
        fkTargetEntity="dbo.users"
        fkOptions={[{ key: '1', label: 'Alice' }]}
        value="1"
        onChange={vi.fn()}
      />,
    );

    // Assert
    const select = screen.getByLabelText('owner_id');
    expect(select).toHaveAttribute('data-target-entity', 'dbo.users');
    expect(screen.getByRole('option', { name: 'Alice' })).toBeInTheDocument();
  });

  it('forwards readOnly and helpText from field metadata', () => {
    // Arrange
    const field: FieldMetadata = { readOnly: true, helpText: 'Cannot edit' };

    // Act
    render(
      <FieldControl name="id" field={field} value="abc" onChange={vi.fn()} />,
    );

    // Assert
    expect(screen.getByLabelText('id')).toHaveAttribute('readonly');
    expect(screen.getByText('Cannot edit')).toBeInTheDocument();
  });

  it('uses an explicit label when provided', () => {
    // Arrange / Act
    render(
      <FieldControl
        name="email_addr"
        label="Email Address"
        value=""
        onChange={vi.fn()}
      />,
    );

    // Assert
    expect(screen.getByLabelText('Email Address')).toBeInTheDocument();
  });
});
