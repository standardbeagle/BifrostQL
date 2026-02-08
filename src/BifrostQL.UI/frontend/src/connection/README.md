# BifrostQL Connection UI Components

A comprehensive set of React components for database connection management in the BifrostQL desktop application. These components provide a modern, accessible interface for connecting to SQL Server databases and creating test databases.

## Features

- **Modern, responsive UI** with dark mode support
- **Full accessibility** including ARIA labels, keyboard navigation, and screen reader support
- **Form validation** with clear error messages
- **localStorage persistence** for recent connections
- **Test database creation** with multiple template options
- **TypeScript support** with full type definitions
- **Mobile responsive** design

## Installation

The components are located in the `src/connection/` directory. Import them as needed:

```tsx
import {
  ConnectionForm,
  WelcomePanel,
  TestDatabaseDialog,
  ConnectionInfo,
  AuthMethod
} from './connection';
```

Don't forget to include the CSS file for theming:

```tsx
import './connection/connection.css';
```

## Components

### ConnectionForm

A form component for collecting SQL Server connection details.

#### Props

```typescript
interface ConnectionFormProps {
  onConnect: (connectionString: string, connectionName: string) => void;
  onTestConnection?: (connectionString: string) => Promise<boolean>;
  initialState?: ConnectionState;
}
```

#### Usage

```tsx
import { ConnectionForm } from './connection';

function App() {
  const handleConnect = (connectionString: string, name: string) => {
    console.log('Connecting to:', name);
    // Initiate connection
  };

  const handleTest = async (connectionString: string): Promise<boolean> => {
    try {
      // Test connection logic
      return true;
    } catch {
      return false;
    }
  };

  return (
    <ConnectionForm
      onConnect={handleConnect}
      onTestConnection={handleTest}
    />
  );
}
```

### WelcomePanel

A welcome screen with connection options and recent connections.

#### Props

```typescript
interface WelcomePanelProps {
  onConnectClick: () => void;
  onCreateTestDatabase: () => void;
  recentConnections?: ConnectionInfo[];
  onSelectRecentConnection?: (connection: ConnectionInfo) => void;
  onClearRecentConnections?: () => void;
}
```

#### Usage

```tsx
import { WelcomePanel } from './connection';
import { loadRecentConnections } from './connection';

function App() {
  const [recentConnections, setRecentConnections] = useState(
    loadRecentConnections()
  );

  return (
    <WelcomePanel
      onConnectClick={() => setShowConnectionForm(true)}
      onCreateTestDatabase={() => setShowTestDbDialog(true)}
      recentConnections={recentConnections}
      onSelectRecentConnection={(conn) => connectTo(conn)}
      onClearRecentConnections={() => setRecentConnections([])}
    />
  );
}
```

### TestDatabaseDialog

A modal dialog for creating test databases from templates.

#### Props

```typescript
interface TestDatabaseDialogProps {
  isOpen: boolean;
  onClose: () => void;
  onCreate: (template: TestDatabaseTemplate) => Promise<void>;
  onConnectAfterCreate?: (connectionString: string) => void;
  isCreating?: boolean;
  progress?: TestDatabaseProgress | null;
  error?: string | null;
}
```

#### Usage

```tsx
import { TestDatabaseDialog, TestDatabaseTemplate } from './connection';

function App() {
  const [isDialogOpen, setIsDialogOpen] = useState(false);
  const [isCreating, setIsCreating] = useState(false);
  const [progress, setProgress] = useState(null);

  const handleCreate = async (template: TestDatabaseTemplate) => {
    setIsCreating(true);
    try {
      // Create database logic
      setProgress({ stage: 'Creating tables...', percent: 50, message: '...' });
    } finally {
      setIsCreating(false);
    }
  };

  return (
    <TestDatabaseDialog
      isOpen={isDialogOpen}
      onClose={() => setIsDialogOpen(false)}
      onCreate={handleCreate}
      isCreating={isCreating}
      progress={progress}
    />
  );
}
```

## Type Definitions

### ConnectionFormData

```typescript
interface ConnectionFormData {
  server: string;              // Server address (default: "localhost")
  database: string;            // Database name (required)
  authMethod: AuthMethod;      // Authentication method
  username?: string;           // Username for SQL Auth
  password?: string;           // Password for SQL Auth
  trustServerCertificate: boolean;
}
```

### AuthMethod

```typescript
enum AuthMethod {
  SqlServer = 'sql-server',   // SQL Server Authentication
  Windows = 'windows',        // Windows Authentication
}
```

### ConnectionInfo

```typescript
interface ConnectionInfo {
  id: string;
  name: string;
  connectionString: string;
  connectedAt: string;        // ISO date string
  server: string;
  database: string;
}
```

### TestDatabaseTemplate

```typescript
enum TestDatabaseTemplate {
  Northwind = 'northwind',
  AdventureWorksLite = 'adventureworks-lite',
  SimpleBlog = 'simple-blog',
}
```

## Utility Functions

### saveRecentConnections

Save connections to localStorage:

```typescript
import { saveRecentConnections } from './connection';

const connections: ConnectionInfo[] = [...];
saveRecentConnections(connections);
```

### loadRecentConnections

Load connections from localStorage:

```typescript
import { loadRecentConnections } from './connection';

const connections = loadRecentConnections();
```

## CSS Variables

The components use CSS custom properties for theming. Define these in your application for consistent styling:

```css
:root {
  --color-primary: #3b82f6;
  --color-bg-primary: #0f172a;
  --color-text-primary: #e2e8f0;
  /* ... see connection.css for full list */
}
```

## Accessibility

All components follow WCAG 2.1 AA guidelines:

- **Keyboard Navigation**: All interactive elements are keyboard accessible
- **ARIA Labels**: Proper ARIA attributes for screen readers
- **Focus Management**: Clear focus indicators and logical tab order
- **Error Handling**: Errors are announced to screen readers
- **Color Contrast**: Meets WCAG AA standards for text contrast

## Browser Support

- Chrome/Edge: Latest 2 versions
- Firefox: Latest 2 versions
- Safari: Latest 2 versions

## Integration with main.tsx

To integrate these components with your existing main.tsx:

```tsx
import React, { useState } from 'react';
import ReactDOM from 'react-dom/client';
import Editor from '@standardbeagle/edit-db';
import {
  WelcomePanel,
  ConnectionForm,
  ConnectionInfo
} from './connection';
import './connection/connection.css';
import './app.css';

function App() {
  const [state, setState] = useState<'welcome' | 'connecting' | 'connected'>('welcome');
  const [connection, setConnection] = useState<ConnectionInfo | null>(null);

  if (state === 'connected' && connection) {
    const graphqlUri = `${window.location.origin}/graphql`;
    return (
      <div className="app-container">
        <Editor uri={graphqlUri} />
      </div>
    );
  }

  if (state === 'connecting') {
    return <ConnectionForm onConnect={handleConnect} />;
  }

  return (
    <WelcomePanel
      onConnectClick={() => setState('connecting')}
      onCreateTestDatabase={() => {}}
      recentConnections={[]}
      onSelectRecentConnection={(c) => handleConnect(c.connectionString, c.name)}
    />
  );
}

ReactDOM.createRoot(document.getElementById('root')!).render(
  <React.StrictMode>
    <App />
  </React.StrictMode>
);
```

## License

MIT
