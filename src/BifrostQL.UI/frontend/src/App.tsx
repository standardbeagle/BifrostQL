import { useState, useCallback, useEffect, useRef } from 'react';
import Editor, { type SavedObject } from '@standardbeagle/edit-db';
import '@standardbeagle/edit-db/style.css';
import {
  WelcomePanel,
  ConnectionForm,
  ProviderSelect,
  QuickStart,
  Provider,
} from './connection';
import { AboutPanel } from './about/AboutPanel';
import { loadSession } from './connection/session';
import { SqlConsole } from './SqlConsole';
import { QueryBuilderPane } from './designer/QueryBuilderPane';
import { SavedQueryList } from './designer/SavedQueryList';
import { FormBuilderPane } from './forms/FormBuilderPane';
import { runFormsMigrationOnce } from './forms/forms-migration-boot';
import { isSqlBridgeAvailable } from './lib/sql-bridge';
import {
  fetchProfiles,
  resolveActiveProfile,
  saveActiveProfileId,
  DEFAULT_PROFILES,
} from './profiles/profiles';
import type { ApiProfile } from './profiles/types';
import { EditorHeader, type EditorPane } from './EditorHeader';
import { useHealthCheck } from './hooks/useHealthCheck';
import { useTransport } from './hooks/useTransport';
import { useConnectionFlows } from './hooks/useConnectionFlows';
import './connection/connection.css';
import './app.css';

type AppView = 'welcome' | 'quickstart' | 'provider-select' | 'connect' | 'editor' | 'about';

export default function App() {
  const restored = loadSession();
  const [currentView, setCurrentView] = useState<AppView>(restored ? 'editor' : 'welcome');
  // View to return to when leaving the About page (opened from welcome or editor).
  const [aboutReturnView, setAboutReturnView] = useState<AppView>('welcome');
  const [editorKey, setEditorKey] = useState(0);
  // Editor pane toggle: GraphQL editor (default) vs raw SQL console. The SQL
  // console rides the Photino bridge, so it's only offered inside the desktop app.
  const [editorPane, setEditorPane] = useState<EditorPane>('graphql');
  const sqlBridgeAvailable = isSqlBridgeAvailable();

  // Saved queries (builder pane). The nav rail lists them and asks the designer
  // to open one; the designer owns save/rename/delete and tells the rail when the
  // store changed. A fresh object per click so reopening the same query re-loads
  // it (after confirming any unsaved edits) rather than being a no-op.
  //
  // The request is one-shot: the designer clears it once consumed. The builder
  // pane is conditionally rendered, so a pane switch unmounts and remounts it —
  // a request left standing would be replayed on remount and resurrect a query
  // the user had since deleted, with its old id and version.
  const [savedQueryToOpen, setSavedQueryToOpen] = useState<SavedObject | null>(null);
  const [activeSavedQueryId, setActiveSavedQueryId] = useState<string | null>(null);
  const [savedQueryListToken, setSavedQueryListToken] = useState(0);
  const handleOpenSavedQuery = useCallback((query: SavedObject) => {
    setSavedQueryToOpen({ ...query });
  }, []);
  const handleSavedQueryOpenHandled = useCallback(() => {
    setSavedQueryToOpen(null);
  }, []);
  const handleSavedQueryStoreChanged = useCallback(() => {
    setSavedQueryListToken((t) => t + 1);
  }, []);

  // API profiles (slice 6a endpoint). The picker re-points the embedded editor
  // at `?profile=<serverProfile>` so the server serves that profile's schema.
  const [apiProfiles, setApiProfiles] = useState<ApiProfile[]>(DEFAULT_PROFILES);
  const [activeProfileId, setActiveProfileId] = useState<string>(
    () => resolveActiveProfile(DEFAULT_PROFILES).id,
  );

  // Transition into the editor view. Bumping the key remounts the editor so it
  // re-introspects the freshly connected schema.
  const enterEditor = useCallback(() => {
    setEditorKey((k) => k + 1);
    setCurrentView('editor');
  }, []);

  // First-run migration: once the app reaches the editor (i.e. is connected to a
  // server that can serve /_saved-objects), lift any legacy localStorage forms into
  // the saved-object store. Fire-and-forget and idempotent; runs at most once per
  // session and is a no-op after it has succeeded once.
  const migrationRan = useRef(false);
  useEffect(() => {
    if (currentView === 'editor' && !migrationRan.current) {
      migrationRan.current = true;
      void runFormsMigrationOnce();
    }
  }, [currentView]);

  const flows = useConnectionFlows({ restored, enterEditor });
  const {
    setConnectionState,
    errorMessage,
    setErrorMessage,
    connectionInfo,
    recentConnections,
    vaultServers,
    selectedProvider,
    setSelectedProvider,
    isLaunching,
    launchProgress,
    handleTestConnection,
    handleConnect,
    handleConnectVaultServer,
    handleSelectRecentConnection,
    handleQuickStartLaunch,
    handleClearRecentConnections,
    handleDisconnect,
  } = flows;

  // Periodic health check — detects backend restarts and auto-recovers.
  useHealthCheck(setErrorMessage, setConnectionState);

  // Refresh the profile list whenever the active connection changes. The
  // server schema is connection-scoped, so each connection may expose a
  // different set of module profiles. On failure fetchProfiles() falls back to
  // DEFAULT_PROFILES (single raw entry → picker disabled).
  const connectionKey = connectionInfo?.id ?? null;
  // Tracks the last resolved profile id so the editor only remounts on a real
  // change (kept in a ref so the comparison stays out of a setState updater).
  const resolvedProfileRef = useRef(activeProfileId);
  useEffect(() => {
    let cancelled = false;
    fetchProfiles().then((fetched) => {
      if (cancelled) return;
      setApiProfiles(fetched);
      const nextId = resolveActiveProfile(fetched).id;
      // Only remount the editor when the resolved profile actually changed.
      // Bumping unconditionally made the editor mount twice on every startup
      // (once on mount, again when profiles resolved to the same default),
      // re-introspecting the schema and refetching all table data for nothing.
      if (resolvedProfileRef.current !== nextId) {
        resolvedProfileRef.current = nextId;
        setEditorKey((k) => k + 1);
      }
      setActiveProfileId(nextId);
    });
    return () => { cancelled = true; };
  }, [connectionKey]);

  const handleSelectProfile = useCallback((id: string) => {
    saveActiveProfileId(id);
    setActiveProfileId(id);
    resolvedProfileRef.current = id;
    // Remount the editor so it re-introspects the newly selected profile's
    // schema from the profile-scoped GraphQL endpoint.
    setEditorKey((k) => k + 1);
  }, []);

  const activeProfile = apiProfiles.find((p) => p.id === activeProfileId) ?? apiProfiles[0];
  // The server serves each module profile's schema behind a `?profile=` query
  // param. We thread that onto both the HTTP GraphQL path and the binary
  // WebSocket path so the selected transport hits the right profile-scoped
  // endpoint.
  const serverProfile = activeProfile.serverProfile;
  const graphqlPath = serverProfile
    ? `/graphql?profile=${encodeURIComponent(serverProfile)}`
    : '/graphql';
  const binaryPath = serverProfile
    ? `/bifrost-ws?profile=${encodeURIComponent(serverProfile)}`
    : '/bifrost-ws';

  const { transportMode, toggleTransport, transport, transportConnected, editorFetcher } =
    useTransport(graphqlPath, binaryPath);

  const handleTryItNow = useCallback(() => {
    setCurrentView('quickstart');
  }, []);

  const handleProviderSelect = useCallback((provider: Provider) => {
    setSelectedProvider(provider);
    setCurrentView('connect');
  }, [setSelectedProvider]);

  const handleShowAbout = useCallback(() => {
    setAboutReturnView(currentView);
    setCurrentView('about');
  }, [currentView]);

  const handleBack = useCallback(() => {
    setErrorMessage(null);
    switch (currentView) {
      case 'about':
        setCurrentView(aboutReturnView);
        return;
      case 'quickstart':
      case 'provider-select':
        setCurrentView('welcome');
        break;
      case 'connect':
        setCurrentView('provider-select');
        setSelectedProvider(null);
        break;
      case 'editor':
        handleDisconnect();
        setCurrentView('welcome');
        break;
      default:
        setCurrentView('welcome');
    }
  }, [currentView, aboutReturnView, setErrorMessage, setSelectedProvider, handleDisconnect]);

  const errorBanner = errorMessage && (
    <div className="bifrost-error-banner" role="alert">
      <span className="bifrost-error-banner__icon">!</span>
      <span className="bifrost-error-banner__message">{errorMessage}</span>
      <button
        className="bifrost-error-banner__dismiss"
        onClick={() => { setErrorMessage(null); setConnectionState('idle'); }}
        aria-label="Dismiss error"
      >
        &times;
      </button>
    </div>
  );

  if (currentView === 'about') {
    return (
      <div className="bifrost-connection-container">
        {errorBanner}
        <AboutPanel onBack={handleBack} />
      </div>
    );
  }

  if (currentView === 'quickstart') {
    return (
      <div className="bifrost-connection-container">
        {errorBanner}
        <QuickStart
          onLaunch={handleQuickStartLaunch}
          onBack={handleBack}
          isLaunching={isLaunching}
          launchProgress={launchProgress}
        />
      </div>
    );
  }

  if (currentView === 'provider-select') {
    return (
      <div className="bifrost-connection-container">
        {errorBanner}
        <ProviderSelect
          onProviderSelect={handleProviderSelect}
          onBack={handleBack}
        />
      </div>
    );
  }

  if (currentView === 'connect' && selectedProvider) {
    return (
      <div className="bifrost-connection-container">
        {errorBanner}
        <ConnectionForm
          provider={selectedProvider}
          onConnect={handleConnect}
          onTestConnection={handleTestConnection}
          onBack={handleBack}
        />
      </div>
    );
  }

  if (currentView === 'editor') {
    return (
      <div className="app-container">
        <EditorHeader
          connectionInfo={connectionInfo}
          apiProfiles={apiProfiles}
          activeProfileId={activeProfileId}
          onSelectProfile={handleSelectProfile}
          transportMode={transportMode}
          transportConnected={transportConnected}
          onToggleTransport={toggleTransport}
          sqlBridgeAvailable={sqlBridgeAvailable}
          editorPane={editorPane}
          onSelectPane={setEditorPane}
          onShowAbout={handleShowAbout}
          onDisconnect={handleBack}
        />
        {/*
          The Editor from @standardbeagle/edit-db accepts a `fetcher` prop
          implementing its GraphQLFetcher interface. We inject a
          TransportGraphQLFetcher that delegates to the selected QueryTransport,
          so every editor data path (schema introspection, table data,
          mutations, stats) routes through the active HTTP/JSON or binary
          WebSocket transport. Toggling the header control rebuilds the
          transport and remounts the editor (via the transportMode key) so new
          queries flow over the newly selected mode.
        */}
        {editorPane === 'sql' ? (
          <SqlConsole />
        ) : editorPane === 'builder' ? (
          <div style={{ display: 'flex', flex: 1, minHeight: 0 }}>
            <SavedQueryList
              activeId={activeSavedQueryId}
              reloadToken={savedQueryListToken}
              onOpen={handleOpenSavedQuery}
            />
            <QueryBuilderPane
              openRequest={savedQueryToOpen}
              onOpenHandled={handleSavedQueryOpenHandled}
              onActiveChange={setActiveSavedQueryId}
              onStoreChanged={handleSavedQueryStoreChanged}
            />
          </div>
        ) : editorPane === 'forms' ? (
          <FormBuilderPane fetcher={editorFetcher ?? undefined} />
        ) : editorFetcher && transport && transport.mode === transportMode ? (
          // Only mount the editor once the effect has published a transport
          // whose mode matches the current selection. During a mode toggle the
          // old (now-closed) transport is still in state for one render; gating
          // on the mode match keeps the editor off the closed instance until the
          // fresh one is ready, avoiding a "BinaryTransport is closed" query.
          <Editor
            key={`${editorKey}-${transportMode}`}
            fetcher={editorFetcher}
            showStats
            onLocate={(location) => {
              window.history.pushState(null, '', location);
            }}
          />
        ) : (
          <div className="bifrost-editor-loading" style={{ padding: 24, opacity: 0.7 }}>
            Connecting…
          </div>
        )}
      </div>
    );
  }

  // Welcome view (default)
  return (
    <div className="bifrost-welcome-container">
      {errorBanner}
      <WelcomePanel
        onConnectClick={() => { setErrorMessage(null); setCurrentView('provider-select'); }}
        onCreateTestDatabase={handleTryItNow}
        recentConnections={recentConnections}
        onSelectRecentConnection={handleSelectRecentConnection}
        onClearRecentConnections={handleClearRecentConnections}
        vaultServers={vaultServers}
        onConnectVaultServer={handleConnectVaultServer}
        onShowAbout={handleShowAbout}
      />
    </div>
  );
}
