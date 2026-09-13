import { describe, it, expect, vi, afterEach } from 'vitest';
import { render, screen, fireEvent, waitFor } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import type { ReactNode } from 'react';
import { BifrostProvider } from '@bifrostql/react';
import { FieldControl, resolveFieldKind } from './field-control';
import type { FieldMetadata } from '../metadata/app-metadata-types';

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
    render(<FieldControl name="title" value="hello" onChange={vi.fn()} />);

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

describe('FieldControl server policy', () => {
  const originalFetch = globalThis.fetch;

  afterEach(() => {
    globalThis.fetch = originalFetch;
    vi.restoreAllMocks();
  });

  /** `_dbSchema` projection for `users` with one writable and one locked column. */
  function policyFetchMock(writable: boolean) {
    return vi.fn(() =>
      Promise.resolve({
        ok: true,
        status: 200,
        statusText: 'OK',
        json: () =>
          Promise.resolve({
            data: {
              _grants: [],
              _dbSchema: [
                {
                  graphQlName: 'users',
                  allowedActions: ['read', 'update'],
                  columns: [{ graphQlName: 'email', readable: true, writable }],
                },
              ],
            },
          }),
      } as Response),
    );
  }

  function createWrapper() {
    const queryClient = new QueryClient({
      defaultOptions: { queries: { retry: false, gcTime: 0 } },
    });
    return function Wrapper({ children }: { children: ReactNode }) {
      return (
        <QueryClientProvider client={queryClient}>
          <BifrostProvider
            config={{ endpoint: 'http://localhost:5000/graphql' }}
          >
            {children}
          </BifrostProvider>
        </QueryClientProvider>
      );
    };
  }

  it('is read-only until the server projects the column as writable', async () => {
    // Arrange
    globalThis.fetch = policyFetchMock(true);
    const Wrapper = createWrapper();

    // Act
    render(
      <Wrapper>
        <FieldControl
          name="email"
          table="users"
          value="a@b.c"
          onChange={vi.fn()}
        />
      </Wrapper>,
    );

    // Assert: locked while the policy loads, editable once the server says so.
    expect(screen.getByLabelText('email')).toHaveAttribute('readonly');
    await waitFor(() =>
      expect(screen.getByLabelText('email')).not.toHaveAttribute('readonly'),
    );
    const [, init] = (globalThis.fetch as ReturnType<typeof vi.fn>).mock
      .calls[0];
    expect(JSON.parse(init.body).variables).toEqual({ table: 'users' });
  });

  it('stays read-only when the server projects the column as not writable', async () => {
    // Arrange
    globalThis.fetch = policyFetchMock(false);
    const Wrapper = createWrapper();

    // Act
    render(
      <Wrapper>
        <FieldControl
          name="email"
          table="users"
          value="a@b.c"
          onChange={vi.fn()}
        />
      </Wrapper>,
    );

    // Assert
    await waitFor(() => expect(globalThis.fetch).toHaveBeenCalledTimes(1));
    expect(screen.getByLabelText('email')).toHaveAttribute('readonly');
  });

  it('keeps field.readOnly even when the server allows the write', async () => {
    // Arrange
    globalThis.fetch = policyFetchMock(true);
    const Wrapper = createWrapper();

    // Act
    render(
      <Wrapper>
        <FieldControl
          name="email"
          table="users"
          field={{ readOnly: true }}
          value="a@b.c"
          onChange={vi.fn()}
        />
      </Wrapper>,
    );

    // Assert
    await waitFor(() => expect(globalThis.fetch).toHaveBeenCalledTimes(1));
    expect(screen.getByLabelText('email')).toHaveAttribute('readonly');
  });

  it('does not consult the server when no table is named', () => {
    // Arrange
    globalThis.fetch = vi.fn();

    // Act: no provider at all, as a plain presentational field.
    render(<FieldControl name="title" value="x" onChange={vi.fn()} />);

    // Assert
    expect(globalThis.fetch).not.toHaveBeenCalled();
    expect(screen.getByLabelText('title')).not.toHaveAttribute('readonly');
  });
});
