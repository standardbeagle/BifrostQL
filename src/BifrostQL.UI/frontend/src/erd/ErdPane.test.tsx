// @vitest-environment jsdom
import { cleanup, fireEvent, render, screen } from '@testing-library/react';
import { useState, type ComponentType } from 'react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { ErdPane, GET_ERD_SCHEMA, normalizeErdSchema } from './ErdPane';
import { mapSchemaToErd } from './model';
import type { ErdSchemaTable } from './types';
vi.mock('@xyflow/react', async () => {
  return {
    ReactFlow: ({ nodes, edges, nodeTypes, edgeTypes }: { nodes: Array<{ id: string; type: string; data: unknown }>; edges: Array<{ id: string; type?: string; data: unknown }>; nodeTypes: Record<string, ComponentType<{ data: unknown }> >; edgeTypes: Record<string, ComponentType<{ data: unknown; sourceX?: number; sourceY?: number; targetX?: number; targetY?: number }>> }) => <div>{nodes.map((node) => {
      const Node = nodeTypes[node.type];
      return <Node key={node.id} data={node.data} />;
    })}{edges.map((edge) => {
      const Edge = edgeTypes[edge.type ?? 'default'];
      return <Edge key={edge.id} data={edge.data} sourceX={0} sourceY={0} targetX={100} targetY={100} />;
    })}</div>,
    Background: () => null,
    Controls: () => null,
    MiniMap: () => null,
    BaseEdge: () => null,
    EdgeLabelRenderer: ({ children }: { children: React.ReactNode }) => <>{children}</>,
    getBezierPath: () => ['M 0 0 L 100 100', 50, 50],
    // Surfaced as a testable element: React Flow drops every edge whose node
    // declares no handle, which degrades the diagram to unconnected boxes
    // without any error. The mock replaces the whole renderer, so nothing else
    // in this file can observe that.
    Handle: ({ type }: { type: string }) => <div data-testid={`handle-${type}`} />,
    Position: { Left: 'left', Right: 'right', Top: 'top', Bottom: 'bottom' },
  };
});

const liveSchemaFixture: ErdSchemaTable[] = [{
  dbName: 'dbo.order_items', graphQlName: 'orderItems', labelColumn: 'id', primaryKeys: ['id'],
  columns: [{ dbName: 'id', graphQlName: 'id', isPrimaryKey: true }], multiJoins: [],
  singleJoins: [{ name: 'orders', fieldName: 'orders', sourceColumnNames: ['orderId'], destinationTable: 'orders', destinationColumnNames: ['id'], relationshipKind: 'name-based' }],
}, {
  dbName: 'dbo.orders', graphQlName: 'orders', labelColumn: 'id', primaryKeys: ['id'],
  columns: [{ dbName: 'id', graphQlName: 'id', isPrimaryKey: true }], multiJoins: [], singleJoins: [],
}];

afterEach(() => cleanup());

describe('ErdPane', () => {
  it('normalizes the live _dbSchema shape before mapping relationship edges', () => {
    const graph = mapSchemaToErd(normalizeErdSchema(liveSchemaFixture));

    expect(graph.nodes[0].data).toMatchObject({ name: 'orderItems', label: 'Dbo.Order Items' });
    expect(graph.nodes[0].data.columns[0]).toMatchObject({ name: 'id' });
    expect(graph.edges[0].data?.kind).toBe('name-based');
    expect(GET_ERD_SCHEMA).toMatch(/relationshipKind/);
  });

  it('gives every table node the handles React Flow needs to attach edges', async () => {
    // Without a source and a target handle React Flow silently drops every
    // edge, so the pane renders the tables but none of the relationships —
    // an ER diagram with no Rs, and no error to say so.
    const fetcher = { query: vi.fn().mockResolvedValue({ _dbSchema: liveSchemaFixture }) };

    render(<ErdPane fetcher={fetcher as never} onOpenTable={vi.fn()} />);
    await screen.findByTitle('Open orderItems in editor');

    expect(screen.getAllByTestId('handle-source')).toHaveLength(liveSchemaFixture.length);
    expect(screen.getAllByTestId('handle-target')).toHaveLength(liveSchemaFixture.length);
  });

  it('opens the editor grid and selected table route from a fetched table node', async () => {
    const fetcher = { query: vi.fn().mockResolvedValue({ _dbSchema: liveSchemaFixture }) };
    function Shell() {
      const [pane, setPane] = useState<'erd' | 'grid'>('erd');
      const [selectedTable, setSelectedTable] = useState('');
      const openTable = (name: string) => {
        window.history.pushState(null, '', `/${name}`);
        setSelectedTable(name);
        setPane('grid');
      };
      return pane === 'erd'
        ? <ErdPane fetcher={fetcher as never} onOpenTable={openTable} />
        : <div data-testid="editor-grid">Grid: {selectedTable}</div>;
    }

    render(<Shell />);
    fireEvent.click(await screen.findByTitle('Open orderItems in editor'));

    expect(screen.getByTestId('editor-grid').textContent).toBe('Grid: orderItems');
    expect(window.location.pathname).toBe('/orderItems');
  });

  it('shows join columns when a relationship edge is hovered', async () => {
    const fetcher = { query: vi.fn().mockResolvedValue({ _dbSchema: liveSchemaFixture }) };

    render(<ErdPane fetcher={fetcher as never} onOpenTable={vi.fn()} />);
    const edge = await screen.findByRole('img', { name: 'Relationship join columns: orderId → id' });
    fireEvent.mouseEnter(edge);

    expect(screen.getByRole('tooltip').textContent).toContain('Join columns: orderId → id');
  });
});

// The App-level "node click opens the table in the editor" assertion used to
// live here against a stubbed Editor that reported window.location.pathname.
// That stub cannot fail: the shell pushes the URL, but the real editor routes
// from a prop-seeded reducer and ignored it entirely. The honest version, with
// the real Editor and the real React Flow renderer, is in ErdPane.live.test.tsx.
