// @vitest-environment jsdom
import { cleanup, fireEvent, render, screen } from '@testing-library/react';
import { useState, type ComponentType } from 'react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { ErdPane, GET_ERD_SCHEMA, normalizeErdSchema } from './ErdPane';
import { mapSchemaToErd } from './model';
import type { ErdSchemaTable } from './types';
import App from '../App';

vi.mock('../connection/session', () => ({ loadSession: () => ({ id: 'connected' }) }));
vi.mock('../hooks/useConnectionFlows', () => ({ useConnectionFlows: () => ({ setConnectionState: vi.fn(), errorMessage: null, setErrorMessage: vi.fn(), connectionInfo: null, recentConnections: [], vaultServers: [], selectedProvider: null, setSelectedProvider: vi.fn(), isLaunching: false, launchProgress: null, handleTestConnection: vi.fn(), handleConnect: vi.fn(), handleConnectVaultServer: vi.fn(), handleSelectRecentConnection: vi.fn(), handleQuickStartLaunch: vi.fn(), handleClearRecentConnections: vi.fn(), handleDisconnect: vi.fn() }) }));
vi.mock('../hooks/useHealthCheck', () => ({ useHealthCheck: () => undefined }));
vi.mock('../hooks/useTransport', () => ({ useTransport: () => ({ transportMode: 'http', toggleTransport: vi.fn(), transport: { mode: 'http' }, transportConnected: true, editorFetcher: { query: vi.fn().mockResolvedValue({ _dbSchema: liveSchemaFixture }) } }) }));
vi.mock('../profiles/profiles', () => ({ DEFAULT_PROFILES: [{ id: 'raw', serverProfile: '' }], fetchProfiles: vi.fn().mockResolvedValue([{ id: 'raw', serverProfile: '' }]), resolveActiveProfile: (profiles: Array<{ id: string }>) => profiles[0], saveActiveProfileId: vi.fn() }));
vi.mock('../forms/forms-migration-boot', () => ({ runFormsMigrationOnce: vi.fn() }));
vi.mock('../EditorHeader', () => ({ EditorHeader: ({ editorPane, onSelectPane }: { editorPane: string; onSelectPane: (pane: 'erd') => void }) => <><output data-testid="editor-pane">{editorPane}</output><button type="button" onClick={() => onSelectPane('erd')}>ER diagram</button></> }));
vi.mock('@standardbeagle/edit-db', async (importOriginal) => ({ ...(await importOriginal<typeof import('@standardbeagle/edit-db')>()), default: () => <div data-testid="editor-grid" data-route={window.location.pathname}>Editor grid</div> }));

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

  it('uses the real App callback to switch from the ER diagram to the selected editor grid route', async () => {
    window.history.replaceState(null, '', '/');

    render(<App />);
    fireEvent.click(screen.getByRole('button', { name: 'ER diagram' }));
    fireEvent.click(await screen.findByTitle('Open orderItems in editor'));

    expect(screen.getByTestId('editor-pane').textContent).toBe('graphql');
    expect(screen.getByTestId('editor-grid').getAttribute('data-route')).toBe('/orderItems');
  });
});
