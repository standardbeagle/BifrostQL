import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { render, screen, waitFor } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import type { ReactNode } from 'react';
import { BifrostProvider } from '@bifrostql/react';
import {
  EntityList,
  buildColumns,
  entityKeyToQueryName,
} from './entity-list';
import type { AppMetadata, EntityMetadata } from '../metadata/types';

const ENDPOINT = 'http://localhost:5000/graphql';

/** Overlay with one entity that has a grid preset and a hidden field. */
const sampleMetadata: AppMetadata = {
  entities: {
    'dbo.users': {
      label: 'Users',
      grid: { defaultColumns: ['name', 'email'] },
      fields: {
        name: {},
        email: {},
        secret: { visible: false },
      },
    },
  },
};

/** A `fetch` mock returning the overlay and an empty GraphQL data page. */
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
    // GraphQL data request from BifrostTable; the query field is the bare
    // table name (`dbo` schema is unprefixed).
    return Promise.resolve({
      ok: true,
      status: 200,
      statusText: 'OK',
      json: () =>
        Promise.resolve({ data: { users: { data: [], total: 0 } } }),
    } as Response);
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

describe('buildColumns', () => {
  it('uses the grid preset order when present', () => {
    // Arrange
    const entity = sampleMetadata.entities!['dbo.users'];

    // Act
    const columns = buildColumns(entity);

    // Assert
    expect(columns.map((c) => c.field)).toEqual(['name', 'email']);
  });

  it('falls back to all visible fields when no preset is set', () => {
    // Arrange
    const entity: EntityMetadata = {
      fields: { a: {}, b: {}, hidden: { visible: false } },
    };

    // Act
    const columns = buildColumns(entity);

    // Assert: the hidden field is excluded.
    expect(columns.map((c) => c.field)).toEqual(['a', 'b']);
  });
});

describe('entityKeyToQueryName', () => {
  it('strips the dbo schema prefix', () => {
    // Arrange / Act / Assert
    expect(entityKeyToQueryName('dbo.users')).toBe('users');
  });

  it('joins non-dbo schemas with an underscore', () => {
    // Arrange / Act / Assert
    expect(entityKeyToQueryName('sales.orders')).toBe('sales_orders');
  });

  it('returns unqualified keys unchanged', () => {
    // Arrange / Act / Assert
    expect(entityKeyToQueryName('users')).toBe('users');
  });
});

describe('EntityList', () => {
  let originalFetch: typeof globalThis.fetch;

  beforeEach(() => {
    originalFetch = globalThis.fetch;
  });

  afterEach(() => {
    globalThis.fetch = originalFetch;
    vi.restoreAllMocks();
  });

  it('renders a table for an entity declared in app-metadata', async () => {
    // Arrange
    globalThis.fetch = createFetchMock();
    const Wrapper = createWrapper();

    // Act
    render(
      <Wrapper>
        <EntityList entityKey="dbo.users" />
      </Wrapper>,
    );

    // Assert: title and table render once metadata resolves.
    await waitFor(() =>
      expect(screen.getByTestId('entity-list-dbo.users')).toBeInTheDocument(),
    );
    expect(screen.getByText('Users')).toBeInTheDocument();
    expect(screen.getByTestId('bifrost-table')).toBeInTheDocument();
  });

  it('renders the not-found fallback for an unknown entity', async () => {
    // Arrange
    globalThis.fetch = createFetchMock();
    const Wrapper = createWrapper();

    // Act
    render(
      <Wrapper>
        <EntityList
          entityKey="dbo.missing"
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
