// @vitest-environment jsdom
//
// Integration cover for the ER diagram's two interactive claims, rendered
// against the REAL @xyflow/react renderer and the REAL edit-db Editor.
// ErdPane.test.tsx mocks both wholesale, which is why it stayed green through
// two shipped defects: a node click that moved the URL but not the editor, and
// edges the renderer never drew. Nothing in this file may mock either one.
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { installFlowMeasurement } from './flow-test-env';
import type { ErdSchemaTable } from './types';
import App from '../App';

const schemaFixture: ErdSchemaTable[] = [{
  dbName: 'dbo.order_items', graphQlName: 'orderItems', labelColumn: 'id', primaryKeys: ['id'], isEditable: true, metadata: [],
  columns: [{ dbName: 'id', graphQlName: 'id', paramType: 'Int', dbType: 'int', isPrimaryKey: true, isIdentity: true, isNullable: false, isReadOnly: false, metadata: [] }],
  multiJoins: [], manyToManyJoins: [],
  singleJoins: [{ name: 'orders', fieldName: 'orders', sourceColumnNames: ['orderId'], destinationTable: 'orders', destinationColumnNames: ['id'], relationshipKind: 'name-based' }],
}, {
  dbName: 'dbo.orders', graphQlName: 'orders', labelColumn: 'id', primaryKeys: ['id'], isEditable: true, metadata: [],
  columns: [{ dbName: 'id', graphQlName: 'id', paramType: 'Int', dbType: 'int', isPrimaryKey: true, isIdentity: true, isNullable: false, isReadOnly: false, metadata: [] }],
  multiJoins: [], singleJoins: [], manyToManyJoins: [],
}] as unknown as ErdSchemaTable[];

const fetcher = { query: vi.fn().mockResolvedValue({ _dbSchema: schemaFixture, orderItems: { data: [], total: 0 } }) };

vi.mock('../connection/session', () => ({ loadSession: () => ({ id: 'connected' }) }));
vi.mock('../hooks/useConnectionFlows', () => ({ useConnectionFlows: () => ({ setConnectionState: vi.fn(), errorMessage: null, setErrorMessage: vi.fn(), connectionInfo: null, recentConnections: [], vaultServers: [], selectedProvider: null, setSelectedProvider: vi.fn(), isLaunching: false, launchProgress: null, handleTestConnection: vi.fn(), handleConnect: vi.fn(), handleConnectVaultServer: vi.fn(), handleSelectRecentConnection: vi.fn(), handleQuickStartLaunch: vi.fn(), handleRemoveRecentConnection: vi.fn(), handleClearRecentConnections: vi.fn(), handleDisconnect: vi.fn() }) }));
vi.mock('../hooks/useHealthCheck', () => ({ useHealthCheck: () => undefined }));
vi.mock('../hooks/useTransport', () => ({ useTransport: () => ({ transportMode: 'http', toggleTransport: vi.fn(), transport: { mode: 'http' }, transportConnected: true, editorFetcher: fetcher }) }));
vi.mock('../profiles/api-profiles', () => ({ DEFAULT_PROFILES: [{ id: 'raw', serverProfile: '' }], fetchProfiles: vi.fn().mockResolvedValue({ profiles: [{ id: 'raw', serverProfile: '' }], status: 'ok' }), resolveActiveProfile: (profiles: Array<{ id: string }>) => profiles[0], saveActiveProfileId: vi.fn() }));
vi.mock('../forms/forms-migration-boot', () => ({ runFormsMigrationOnce: vi.fn() }));
vi.mock('../EditorHeader', () => ({ EditorHeader: ({ editorPane, onSelectPane }: { editorPane: string; onSelectPane: (pane: 'erd') => void }) => <><output data-testid="editor-pane">{editorPane}</output><button type="button" onClick={() => onSelectPane('erd')}>ER diagram</button></> }));

beforeEach(() => {
  installFlowMeasurement();
  window.history.replaceState(null, '', '/');
});
afterEach(() => cleanup());

describe('ER diagram, wired to the real renderer and the real editor', () => {
  it('opens the clicked table in the embedded editor, not just in the address bar', async () => {
    // edit-db routes from a prop-seeded reducer, not from window.location, so a
    // shell that only pushes the URL leaves the editor on its start page.
    render(<App />);
    fireEvent.click(screen.getByRole('button', { name: 'ER diagram' }));

    fireEvent.click(await screen.findByTitle('Open orderItems in editor', {}, { timeout: 5000 }));

    expect(screen.getByTestId('editor-pane').textContent).toBe('graphql');
    expect(window.location.pathname).toBe('/orderItems');
    // The editor's own breadcrumb for the opened table — present only when its
    // router actually resolved the /orderItems route.
    await screen.findByTitle('Order Items');
    expect(screen.queryByText('Select a Table')).toBeNull();
  });

  it('shows the join columns when the relationship edge is hovered', async () => {
    render(<App />);
    fireEvent.click(screen.getByRole('button', { name: 'ER diagram' }));
    await screen.findByTitle('Open orderItems in editor', {}, { timeout: 5000 });

    // React Flow draws the edge itself; find it in the rendered graph rather
    // than trusting a stubbed edge component.
    const edge = await waitFor(() => {
      const found = document.querySelector<SVGPathElement>('.react-flow__edge path[role="img"]');
      if (!found) throw new Error('React Flow rendered no relationship edge');
      return found;
    });

    fireEvent.mouseOver(edge);

    expect(screen.getByRole('tooltip').textContent).toContain('Join columns: orderId → id');
  });
});
