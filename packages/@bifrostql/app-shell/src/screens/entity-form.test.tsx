import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { render, screen, waitFor, fireEvent } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import type { ReactNode } from 'react';
import { BifrostProvider } from '@bifrostql/react';
import { EntityForm, buildFormFields } from './entity-form';
import type { AppMetadata, EntityMetadata } from '../metadata/types';

const ENDPOINT = 'http://localhost:5000/graphql';

/** Overlay with one entity whose fields exercise several widget kinds. */
const sampleMetadata: AppMetadata = {
  entities: {
    'dbo.users': {
      label: 'Users',
      fields: {
        name: {},
        active: { widget: 'boolean' },
        bio: { widget: 'json' },
        internal: { visible: false },
      },
    },
  },
};

function createFetchMock(metadata: AppMetadata = sampleMetadata) {
  return vi.fn((input: RequestInfo | URL) => {
    const url = typeof input === 'string' ? input : input.toString();
    if (url.includes('/_app-metadata')) {
      return Promise.resolve({
        ok: true,
        status: 200,
        statusText: 'OK',
        json: () => Promise.resolve(metadata),
      } as Response);
    }
    return Promise.reject(new Error(`Unexpected fetch: ${url}`));
  });
}

function createWrapper() {
  const queryClient = new QueryClient({
    defaultOptions: { queries: { retry: false, gcTime: 0 } },
  });
  return function Wrapper({ children }: { children: ReactNode }) {
    return (
      <QueryClientProvider client={queryClient}>
        <BifrostProvider config={{ endpoint: ENDPOINT }}>
          {children}
        </BifrostProvider>
      </QueryClientProvider>
    );
  };
}

describe('buildFormFields', () => {
  it('omits hidden fields and keeps the rest', () => {
    // Arrange
    const entity: EntityMetadata = {
      fields: { a: {}, b: { visible: false }, c: {} },
    };

    // Act
    const fields = buildFormFields(entity);

    // Assert
    expect(fields.map(([name]) => name)).toEqual(['a', 'c']);
  });
});

describe('EntityForm', () => {
  let originalFetch: typeof globalThis.fetch;

  beforeEach(() => {
    originalFetch = globalThis.fetch;
  });

  afterEach(() => {
    globalThis.fetch = originalFetch;
    vi.restoreAllMocks();
  });

  it('renders a control per visible field, dispatched by widget', async () => {
    // Arrange
    globalThis.fetch = createFetchMock();
    const Wrapper = createWrapper();

    // Act
    render(
      <Wrapper>
        <EntityForm
          entityKey="dbo.users"
          mode="create"
          onSubmit={vi.fn()}
        />
      </Wrapper>,
    );

    // Assert: scalar, boolean, and json controls render; hidden field absent.
    await waitFor(() =>
      expect(screen.getByTestId('entity-form-dbo.users')).toBeInTheDocument(),
    );
    expect(screen.getByLabelText('name')).toHaveAttribute('type', 'text');
    expect(screen.getByLabelText('active')).toHaveAttribute(
      'type',
      'checkbox',
    );
    expect(screen.getByLabelText('bio').tagName).toBe('TEXTAREA');
    expect(screen.queryByLabelText('internal')).not.toBeInTheDocument();
  });

  it('submits the edited values', async () => {
    // Arrange
    globalThis.fetch = createFetchMock();
    const Wrapper = createWrapper();
    const onSubmit = vi.fn();

    render(
      <Wrapper>
        <EntityForm
          entityKey="dbo.users"
          mode="edit"
          initialValues={{ name: 'Alice', active: false, bio: '' }}
          onSubmit={onSubmit}
        />
      </Wrapper>,
    );

    await waitFor(() =>
      expect(screen.getByLabelText('name')).toBeInTheDocument(),
    );

    // Act: change the name, toggle active, submit.
    fireEvent.change(screen.getByLabelText('name'), {
      target: { value: 'Alice B' },
    });
    fireEvent.click(screen.getByLabelText('active'));
    fireEvent.click(screen.getByRole('button', { name: 'Save' }));

    // Assert
    expect(onSubmit).toHaveBeenCalledWith({
      name: 'Alice B',
      active: true,
      bio: '',
    });
  });

  it('renders the not-found fallback for an unknown entity', async () => {
    // Arrange
    globalThis.fetch = createFetchMock();
    const Wrapper = createWrapper();

    // Act
    render(
      <Wrapper>
        <EntityForm
          entityKey="dbo.missing"
          mode="create"
          onSubmit={vi.fn()}
          notFoundFallback={<div>missing</div>}
        />
      </Wrapper>,
    );

    // Assert
    await waitFor(() =>
      expect(screen.getByText('missing')).toBeInTheDocument(),
    );
  });
});
