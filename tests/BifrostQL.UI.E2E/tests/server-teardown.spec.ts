import { test, expect } from '@playwright/test';
import { spawn } from 'child_process';
import { listGroupMembers, stopProcessGroup } from '../server-process';

/**
 * Pins the property that actually broke: the E2E server is a CHILD of the
 * `dotnet run` launcher, so signalling the recorded PID does not reliably stop
 * it — a run whose launcher dies without forwarding the signal leaves the real
 * server holding its port forever. These tests use a cheap launcher/child stand-in
 * (`sh` + `sleep`) so they prove the lifecycle logic without a server build.
 */

/** Launcher process that owns a long-lived child, in its own process group. */
function spawnLauncherWithChild() {
  const proc = spawn('sh', ['-c', 'sleep 600 & wait'], {
    stdio: 'ignore',
    detached: true,
  });
  return proc.pid!;
}

async function settle(ms = 500) {
  await new Promise(resolve => setTimeout(resolve, ms));
}

test.describe('E2E server process-group teardown', () => {
  test('killing only the launcher PID strands the child (the leak this fixes)', async () => {
    const group = spawnLauncherWithChild();
    await settle();
    expect(listGroupMembers(group).length).toBeGreaterThanOrEqual(2);

    process.kill(group, 'SIGKILL'); // launcher only — no chance to forward
    await settle(1000);

    const survivors = listGroupMembers(group);
    expect(survivors.length, 'child survives a launcher-only kill').toBeGreaterThan(0);

    // Clean up via the group so the test itself leaks nothing.
    await stopProcessGroup(group, 'stranded child');
    expect(listGroupMembers(group)).toHaveLength(0);
  });

  test('stopProcessGroup reaps launcher and child, and verifies death', async () => {
    const group = spawnLauncherWithChild();
    await settle();
    expect(listGroupMembers(group).length).toBeGreaterThanOrEqual(2);

    const leaked = await stopProcessGroup(group, 'test server');

    expect(leaked, 'nothing survives teardown').toHaveLength(0);
    expect(listGroupMembers(group)).toHaveLength(0);
  });

  test('stopProcessGroup escalates to SIGKILL when SIGTERM is ignored', async () => {
    const proc = spawn('sh', ['-c', 'trap "" TERM; sleep 600 & wait'], {
      stdio: 'ignore',
      detached: true,
    });
    const group = proc.pid!;
    await settle();
    expect(listGroupMembers(group).length).toBeGreaterThanOrEqual(1);

    const leaked = await stopProcessGroup(group, 'SIGTERM-ignoring server', 1_000, 5_000);

    expect(leaked).toHaveLength(0);
    expect(listGroupMembers(group)).toHaveLength(0);
  });

  test('stopProcessGroup on a dead group is a no-op', async () => {
    const group = spawnLauncherWithChild();
    await settle();
    await stopProcessGroup(group, 'test server');

    await expect(stopProcessGroup(group, 'already-dead server')).resolves.toHaveLength(0);
  });
});
