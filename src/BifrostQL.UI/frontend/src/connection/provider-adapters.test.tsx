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
import type { SqlServerFormData } from './types';

const sqlserver = PROVIDER_ADAPTERS.sqlserver;

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
