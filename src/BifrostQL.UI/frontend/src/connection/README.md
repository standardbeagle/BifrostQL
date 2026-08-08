# BifrostQL Connection UI Components

React components for database connection management in the BifrostQL desktop
application. Four providers are supported: SQL Server, PostgreSQL, MySQL, and
SQLite (see `Provider` in `types.ts`).

The doc comments in `types.ts` are the authoritative reference for all props
and data shapes; this README is only an orientation.

## Components

- **`WelcomePanel`** — landing screen: connect, QuickStart (`onQuickStart`),
  recent connections, credential-vault servers.
- **`ProviderSelect`** — picks one of the four providers.
- **`ConnectionForm`** — per-provider connection details form:

  ```typescript
  interface ConnectionFormProps {
    provider: Provider;                                        // required
    onConnect: (request: ConnectionRequest) => void;
    onTestConnection?: (request: ConnectionRequest) => Promise<boolean>;
    onBack: () => void;                                        // required
  }
  ```

  Passwords are never part of `ConnectionRequest`; they are collected by the
  native credential prompt (`lib/credential-prompt.ts`).
- **`QuickStart`** — launches a ready-made sample database from a schema
  template (`QuickStartSchema`).

Import from the barrel and include the stylesheet:

```tsx
import { WelcomePanel, ConnectionForm, ProviderSelect, QuickStart } from './connection';
import './connection/connection.css';
```

## Persistence

`ConnectionInfo` (id, name, connectionString, connectedAt, server, database,
`provider`, optional `ssh` and `vaultServerName`) is the persisted connection
shape:

- Active session: `connection/session.ts` (sessionStorage).
- Recent connections: `connection/recent-connections.ts` (localStorage),
  exported as `saveRecentConnections` / `loadRecentConnections`.

Both paths sanitize secrets and validate untrusted stored JSON via
`connection/sanitize-connection.ts`.

## Vault servers

`fetchVaultServers` / `connectVaultServer` (`connection/vault-servers.ts`)
list and connect to servers from the encrypted credential vault
(`VaultServer` — metadata only, no passwords).
