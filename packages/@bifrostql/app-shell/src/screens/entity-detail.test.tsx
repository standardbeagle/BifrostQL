import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { render, screen, waitFor } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import type { ReactNode } from 'react';
import { BifrostProvider } from '@bifrostql/react';
import { EntityDetail, buildDetailFields } from './entity-detail';
import type { AppMetadata, EntityMetadata } from '../metadata/types';

const ENDPOINT = 'http://localhost:5000/graphql';

/** Overlay with one entity declaring a `displayFields` ordering. */
const sampleMetadata: AppMetadata = {
  entities: {
    'dbo.users': {
      label: 'Users',
      displayFields: ['name', 'email'],
      fields: {
        name: {},
        email: {},
        secret: { visible: false },
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

describe('buildDetailFields', () => {
  it('uses the displayFields order when present', () => {
    // Arrange
    const entity = sampleMetadata.entities!['dbo.users'];

    // Act
    const fields = buildDetailFields(entity);

    // Assert
    expect(fields.map(([name]) => name)).toEqual(['name', 'email']);
  });

  it('falls back to all visible fields when displayFields is unset', () => {
    // Arrange
    const entity: EntityMetadata = {
      fields: { a: {}, b: {}, hidden: { visible: false } },
    };

    // Act
    const fields = buildDetailFields(entity);

    // Assert
    expect(fields.map(([name]) => name)).toEqual(['a', 'b']);
  });
});

describe('EntityDetail', () => {
  let originalFetch: typeof globalThis.fetch;

  beforeEach(() => {
    originalFetch = globalThis.fetch;
  });

  afterEach(() => {
    globalThis.fetch = originalFetch;
    vi.restoreAllMocks();
  });

  it('renders a definition list of the row values', async () => {
    // Arrange
    globalThis.fetch = createFetchMock();
    const Wrapper = createWrapper();

    // Act
    render(
      <Wrapper>
        <EntityDetail
          entityKey="dbo.users"
          row={{ name: 'Alice', email: 'alice@example.com', secret: 'x' }}
        />
      </Wrapper>,
    );

    // Assert: ordered display fields render; hidden field is excluded.
    await waitFor(() =>
      expect(
        screen.getByTestId('entity-detail-dbo.users'),
      ).toBeInTheDocument(),
    );
    expect(screen.getByText('Alice')).toBeInTheDocument();
    expect(screen.getByText('alice@example.com')).toBeInTheDocument();
    expect(screen.queryByTestId('detail-field-secret')).not.toBeInTheDocument();
  });

  it('renders the not-found fallback for an unknown entity', async () => {
    // Arrange
    globalThis.fetch = createFetchMock();
    const Wrapper = createWrapper();

    // Act
    render(
      <Wrapper>
        <EntityDetail
          entityKey="dbo.missing"
          row={{}}
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
