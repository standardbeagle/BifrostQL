// @vitest-environment jsdom
/**
 * TLS posture of the SQL Server form. `TrustServerCertificate=True` disables
 * certificate validation entirely, which makes the connection trivially
 * interceptable. It is a legitimate choice for a self-signed dev server, but
 * it has to be the user's choice — it used to be the default, so an ordinary
 * connection silently gave up certificate validation.
 */

import { describe, expect, it } from 'vitest';
import { PROVIDER_ADAPTERS } from './provider-adapters';
import type { SqlServerFormData, SshConfig, WpConfig } from './types';

const sqlserver = PROVIDER_ADAPTERS.sqlserver;

const NO_SSH: SshConfig = {
  enabled: false, sshHost: '', sshPort: 22, sshUsername: '', identityFile: '',
};
const NO_WP: WpConfig = { enabled: false, wpPath: 'wp', wpRoot: '' };

describe('sqlserver adapter TLS defaults', () => {
  it('validates the server certificate by default', () => {
    const data = sqlserver.createDefaultFormData() as SqlServerFormData;
    expect(data.trustServerCertificate).toBe(false);
  });

  it('omits TrustServerCertificate from the connection string by default', () => {
    const data = sqlserver.createDefaultFormData() as SqlServerFormData;
    expect(sqlserver.buildConnectionString({ ...data, database: 'db' })).not.toContain(
      'TrustServerCertificate',
    );
  });

  it('still honours an explicit opt-in', () => {
    const data = sqlserver.createDefaultFormData() as SqlServerFormData;
    expect(
      sqlserver.buildConnectionString({ ...data, database: 'db', trustServerCertificate: true }),
    ).toContain('TrustServerCertificate=True');
  });
});

/**
 * The checkbox has to reach the saved vault entry, because the entry is what the
 * real connection is built from. It used to travel as `ssl`, which the host mapped
 * to an encryption mode and not to certificate trust — so the entry was persisted
 * still validating and the checkbox changed nothing about the connection made.
 */
describe('sqlserver adapter vault request', () => {
  const requestFor = (trustServerCertificate: boolean) =>
    sqlserver.buildConnectionRequest(
      {
        ...(sqlserver.createDefaultFormData() as SqlServerFormData),
        database: 'db',
        username: 'sa',
        trustServerCertificate,
      },
      'entry',
      NO_SSH,
      NO_WP,
    );

  it('carries the opt-in as its own field', () => {
    expect(requestFor(true).trustServerCertificate).toBe(true);
  });

  it('defaults the saved entry to validating', () => {
    expect(requestFor(false).trustServerCertificate).toBe(false);
  });

  it('does not smuggle the waiver through the ssl field', () => {
    expect(requestFor(true).ssl).toBeUndefined();
  });
});
