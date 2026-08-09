import { execFileSync } from 'child_process';

/**
 * Process-group lifecycle helpers shared by globalSetup and globalTeardown.
 *
 * The E2E server is started as `dotnet run --project ... -- --headless`, which
 * execs the application (`bifrostui`) as a CHILD of the launcher. The PID we can
 * record is therefore the LAUNCHER's, not the server's. Signalling that PID only
 * stops the server while the launcher is alive and cooperative — a SIGKILL on the
 * launcher, or a crashed/killed Playwright runner, strands the real server holding
 * its port forever. Both were reproduced on this repo.
 *
 * The fix: the server is spawned `detached: true`, so the launcher becomes a
 * process-group leader and the application inherits that group. Every stop then
 * signals the GROUP (`kill(-pgid)`), which reaches the application directly, and
 * escalates SIGTERM -> bounded wait -> SIGKILL, verifying death instead of
 * assuming it.
 */

/** Every member of a process group must look like ours before we signal it. */
const OWNED_PROCESS_PATTERN = /BifrostQL\.UI|bifrostui/;

export interface ProcessInfo {
  pid: number;
  args: string;
}

/** Lists live members of a process group. Empty when the group is gone. */
export function listGroupMembers(pgid: number): ProcessInfo[] {
  let output: string;
  try {
    output = execFileSync('ps', ['-eo', 'pid=,pgid=,args='], { encoding: 'utf-8' });
  } catch {
    return [];
  }
  const members: ProcessInfo[] = [];
  for (const line of output.split('\n')) {
    const match = line.trim().match(/^(\d+)\s+(\d+)\s+(.*)$/);
    if (!match) continue;
    if (Number(match[2]) !== pgid) continue;
    members.push({ pid: Number(match[1]), args: match[3] });
  }
  return members;
}

function signalGroup(pgid: number, signal: NodeJS.Signals): void {
  try {
    process.kill(-pgid, signal);
  } catch {
    // group already gone
  }
}

const sleep = (ms: number) => new Promise(resolve => setTimeout(resolve, ms));

async function waitForGroupExit(pgid: number, timeoutMs: number): Promise<boolean> {
  const deadline = Date.now() + timeoutMs;
  while (Date.now() < deadline) {
    if (listGroupMembers(pgid).length === 0) return true;
    await sleep(100);
  }
  return listGroupMembers(pgid).length === 0;
}

/**
 * Stops a process group: SIGTERM, bounded wait, SIGKILL, then verify.
 * Returns the members that survived even SIGKILL (normally empty).
 */
export async function stopProcessGroup(
  pgid: number,
  label: string,
  termTimeoutMs = 10_000,
  killTimeoutMs = 5_000,
): Promise<ProcessInfo[]> {
  if (listGroupMembers(pgid).length === 0) return [];

  console.log(`Stopping ${label} (process group ${pgid}) with SIGTERM...`);
  signalGroup(pgid, 'SIGTERM');
  if (await waitForGroupExit(pgid, termTimeoutMs)) return [];

  console.warn(`${label} survived SIGTERM after ${termTimeoutMs}ms — escalating to SIGKILL.`);
  signalGroup(pgid, 'SIGKILL');
  await waitForGroupExit(pgid, killTimeoutMs);

  const survivors = listGroupMembers(pgid);
  if (survivors.length > 0) {
    console.error(
      `${label} SURVIVED SIGKILL — leaked processes: ` +
      survivors.map(p => `${p.pid} (${p.args})`).join(', '),
    );
  }
  return survivors;
}

/**
 * Reaps a server left behind by a previous run whose teardown never executed
 * (runner crashed, SIGKILLed, or CI cancelled). Only groups that still contain a
 * process recognisably ours are signalled, so a recycled PID belonging to an
 * unrelated process is never touched.
 */
export async function reapStaleServer(pgid: number): Promise<void> {
  const members = listGroupMembers(pgid);
  if (members.length === 0) {
    console.log(`Stale PID file referenced process group ${pgid}, which is gone.`);
    return;
  }
  if (!members.some(m => OWNED_PROCESS_PATTERN.test(m.args))) {
    console.warn(
      `Stale PID file referenced process group ${pgid}, but no member looks like a ` +
      `BifrostQL.UI server — refusing to signal it (PID reuse).`,
    );
    return;
  }
  console.warn(
    `Found a leaked server from a previous run (process group ${pgid}: ` +
    `${members.map(m => m.pid).join(', ')}) — reaping it before starting a new one.`,
  );
  await stopProcessGroup(pgid, 'leaked BifrostQL server');
}
