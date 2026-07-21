// @vitest-environment jsdom
import { fireEvent, render, screen } from '@testing-library/react';
import { useState, type ComponentType } from 'react';
import { describe, expect, it, vi } from 'vitest';
import { ErdPane, GET_ERD_SCHEMA, normalizeErdSchema } from './ErdPane';
import { mapSchemaToErd } from './model';
import type { ErdSchemaTable } from './types';

vi.mock('@xyflow/react', async () => {
  return {
    ReactFlow: ({ nodes, nodeTypes }: { nodes: Array<{ id: string; type: string; data: unknown }>; nodeTypes: Record<string, ComponentType<{ data: unknown }> > }) => <div>{nodes.map((node) => {
      const Node = nodeTypes[node.type];
      return <Node key={node.id} data={node.data} />;
    })}</div>,
    Background: () => null,
    Controls: () => null,
    MiniMap: () => null,
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

describe('ErdPane', () => {
  it('normalizes the live _dbSchema shape before mapping relationship edges', () => {
    const graph = mapSchemaToErd(normalizeErdSchema(liveSchemaFixture));

    expect(graph.nodes[0].data).toMatchObject({ name: 'orderItems', label: 'Dbo.Order Items' });
    expect(graph.nodes[0].data.columns[0]).toMatchObject({ name: 'id' });
    expect(graph.edges[0].data?.kind).toBe('name-based');
    expect(GET_ERD_SCHEMA).toMatch(/relationshipKind/);
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
});
