# @standardbeagle/edit-db

> React GraphQL database editor with automatic form generation, validation, and data table components.

[![npm version](https://img.shields.io/npm/v/@standardbeagle/edit-db)](https://www.npmjs.com/package/@standardbeagle/edit-db)
[![React](https://img.shields.io/badge/React-18+-61DAFB?logo=react)](https://react.dev/)
[![GraphQL](https://img.shields.io/badge/GraphQL-E10098?logo=graphql)](https://graphql.org/)
[![TypeScript](https://img.shields.io/badge/TypeScript-5.0+-3178C6?logo=typescript)](https://www.typescriptlang.org/)

## Overview

`@standardbeagle/edit-db` is a React component library for building database administration interfaces. Connect it to any GraphQL API—especially [BifrostQL](https://github.com/standardbeagle/BifrostQL)—and get a fully functional data editor with zero configuration.

**Key features:**

- **Automatic form generation** from GraphQL schema introspection
- **Built-in validation** with support for required fields, patterns, ranges, and custom rules
- **Data table with sorting, filtering, and pagination** powered by TanStack Table
- **Foreign key navigation** with automatic relationship detection
- **Responsive design** using Tailwind CSS and shadcn/ui components
- **TypeScript support** with full type definitions

## Installation

Install the package and its peer dependencies:

```bash
npm install @standardbeagle/edit-db @tanstack/react-query
```

```bash
yarn add @standardbeagle/edit-db @tanstack/react-query
```

```bash
pnpm add @standardbeagle/edit-db @tanstack/react-query
```

### Prerequisites

- React 18 or higher
- @tanstack/react-query 5.x
- A GraphQL API endpoint (BifrostQL recommended)

## Quick Start

```tsx
import { Editor } from '@standardbeagle/edit-db';
import '@standardbeagle/edit-db/style.css';

function App() {
  return (
    <Editor
      uri="https://api.example.com/graphql"
      uiPath="/admin"
    />
  );
}

export default App;
```

## Usage Examples

### Basic Setup with BifrostQL

```tsx
import { Editor } from '@standardbeagle/edit-db';
import '@standardbeagle/edit-db/style.css';

function DatabaseAdmin() {
  return (
    <div className="h-screen">
      <Editor
        uri="/graphql"
        uiPath="/admin"
        onLocate={(path) => console.log('Navigation:', path)}
      />
    </div>
  );
}
```

### Custom GraphQL Fetcher

For advanced use cases, provide a custom fetcher:

```tsx
import { Editor, GraphQLFetcher } from '@standardbeagle/edit-db';

class CustomFetcher implements GraphQLFetcher {
  async query<T>(query: string, variables?: Record<string, unknown>): Promise<T> {
    const response = await fetch('/api/graphql', {
      method: 'POST',
      headers: {
        'Content-Type': 'application/json',
        'Authorization': `Bearer ${getAuthToken()}`,
      },
      body: JSON.stringify({ query, variables }),
    });
    
    if (!response.ok) {
      throw new Error(`HTTP error! status: ${response.status}`);
    }
    
    const result = await response.json();
    
    if (result.errors) {
      throw new Error(result.errors[0].message);
    }
    
    return result.data;
  }
}

function App() {
  const fetcher = useMemo(() => new CustomFetcher(), []);
  
  return <Editor fetcher={fetcher} uiPath="/admin" />;
}
```

### Navigation Tracking

Track user navigation for analytics or state management:

```tsx
function App() {
  const [currentPath, setCurrentPath] = useState('');
  
  return (
    <>
      <nav>Current: {currentPath}</nav>
      <Editor
        uri="/graphql"
        uiPath="/admin"
        onLocate={setCurrentPath}
      />
    </>
  );
}
```

## Configuration Options

### Editor Props

| Prop | Type | Required | Description |
|------|------|----------|-------------|
| `uri` | `string` | Optional* | GraphQL endpoint URL |
| `fetcher` | `GraphQLFetcher` | Optional* | Custom fetcher instance |
| `uiPath` | `string` | Optional | Base path for routing (default: `/`) |
| `onLocate` | `(path: string) => void` | Optional | Navigation callback |

*Either `uri` or `fetcher` is required.

### GraphQL Schema Requirements

The editor expects a BifrostQL-compatible schema with the `_dbSchema` query:

```graphql
type Query {
  _dbSchema: [DbSchemaItem!]!
}

type DbSchemaItem {
  graphQlName: String!
  dbName: String!
  labelColumn: String!
  primaryKeys: [String!]!
  isEditable: Boolean!
  metadata: [MetadataItem!]!
  columns: [DbColumnItem!]!
  multiJoins: [JoinItem!]!
  singleJoins: [JoinItem!]!
}

type DbColumnItem {
  graphQlName: String!
  dbName: String!
  paramType: String!
  dbType: String!
  isPrimaryKey: Boolean!
  isIdentity: Boolean!
  isNullable: Boolean!
  isReadOnly: Boolean!
  metadata: [MetadataItem!]!
  # Optional validation metadata
  maxLength: Int
  minLength: Int
  min: Float
  max: Float
  step: Float
  pattern: String
  patternMessage: String
  inputType: String
  defaultValue: String
  enumValues: [String!]
  enumLabels: [String!]
}
```

## Component Architecture

### Main Components

#### `<Editor />`
The root component that sets up React Query, schema context, and routing.

#### `<MainFrame />`
Handles the application layout with sidebar navigation and main content area.

#### `<DataPanel />`
Displays data tables with sorting, filtering, and pagination.

#### `<DataEdit />`
Renders edit forms with automatic field generation based on schema metadata.

### Custom Hooks

| Hook | Purpose |
|------|---------|
| `useSchema()` | Access database schema and table metadata |
| `useDataTable()` | Manage table data, sorting, filtering, and pagination |
| `useTableMutation()` | Handle insert, update, and delete operations |
| `useTableRef()` | Fetch reference data for foreign key dropdowns |
| `usePath()` | Client-side routing without page reloads |
| `useColumnNav()` | Manage side panel navigation for related records |

### Form Validation

Validation rules are automatically applied from schema metadata:

```typescript
// Required fields
{ isNullable: false }

// String length
{ maxLength: 255, minLength: 1 }

// Numeric ranges
{ min: 0, max: 100, step: 1 }

// Pattern matching
{ pattern: '^[A-Z]{2}-\\d{4}$', patternMessage: 'Format: XX-0000' }

// Enum values
{ enumValues: ['active', 'inactive'], enumLabels: ['Active', 'Inactive'] }
```

## Styling

The package uses Tailwind CSS for styling. Import the stylesheet:

```tsx
import '@standardbeagle/edit-db/style.css';
```

For custom theming, override CSS variables:

```css
:root {
  --background: 0 0% 100%;
  --foreground: 222.2 84% 4.9%;
  --primary: 222.2 47.4% 11.2%;
  --primary-foreground: 210 40% 98%;
  /* ... */
}
```

## TypeScript Support

Full TypeScript definitions are included:

```tsx
import type { 
  EditorProps, 
  GraphQLFetcher, 
  Schema, 
  Table, 
  Column 
} from '@standardbeagle/edit-db';
```

## Browser Support

- Chrome 90+
- Firefox 88+
- Safari 14+
- Edge 90+

## Related Packages

- [BifrostQL](https://github.com/standardbeagle/BifrostQL) - Zero-config GraphQL API for databases
- [@tanstack/react-table](https://tanstack.com/table) - Headless UI for building data tables
- [@tanstack/react-query](https://tanstack.com/query) - Data fetching and caching
- [@tanstack/react-form](https://tanstack.com/form) - Form state management

## License

MIT © Standard Beagle

## Contributing

Contributions are welcome! Please see the [main BifrostQL repository](https://github.com/standardbeagle/BifrostQL) for contribution guidelines.
