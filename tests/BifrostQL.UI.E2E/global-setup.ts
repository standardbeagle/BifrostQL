import { spawn, spawnSync, ChildProcess } from 'child_process';
import * as fs from 'fs';
import * as path from 'path';
import * as net from 'net';
import { reapStaleServer, stopProcessGroup } from './server-process';

const SERVER_URL_FILE = path.join(__dirname, '.server-url');
const PID_FILE = path.join(__dirname, '.server-pid');
const PROJECT_PATH = path.join(__dirname, '../../src/BifrostQL.UI/BifrostQL.UI.csproj');
// Headless server readiness budget. The server itself binds in a few seconds
// once built (see the explicit build step below), so this only needs to cover
// runtime startup — not a cold compile.
const STARTUP_TIMEOUT = 120_000;

async function findFreePort(): Promise<number> {
  return new Promise((resolve, reject) => {
    const server = net.createServer();
    server.listen(0, () => {
      const port = (server.address() as net.AddressInfo).port;
      server.close(() => resolve(port));
    });
    server.on('error', reject);
  });
}

async function waitForServer(url: string, timeoutMs: number): Promise<void> {
  const start = Date.now();
  while (Date.now() - start < timeoutMs) {
    try {
      const response = await fetch(`${url}/api/health`);
      if (response.ok) return;
    } catch {
      // server not ready yet
    }
    await new Promise(r => setTimeout(r, 500));
  }
  throw new Error(`Server at ${url} did not start within ${timeoutMs}ms`);
}

/**
 * Safety net for the run BEFORE this one: if its teardown never executed (runner
 * crashed or was killed) the marker files survive pointing at a live server.
 * Overwriting them blindly — as this used to — orphans that server permanently,
 * with nothing left able to identify it. Reconcile instead: kill it if alive,
 * drop the markers either way.
 */
async function reconcileMarkers() {
  if (fs.existsSync(PID_FILE)) {
    const pid = parseInt(fs.readFileSync(PID_FILE, 'utf-8').trim(), 10);
    if (Number.isInteger(pid) && pid > 0) {
      await reapStaleServer(pid);
    }
    fs.rmSync(PID_FILE, { force: true });
  }
  fs.rmSync(SERVER_URL_FILE, { force: true });
}

export default async function globalSetup() {
  await reconcileMarkers();

  // Build explicitly first. `dotnet run` would otherwise fold a cold compile
  // (~20s+) into the server-readiness window, which is what made startup flaky
  // under the old 60s cap in clean/CI environments. With the build done, the
  // spawned `dotnet run --no-build` only needs to boot the runtime.
  console.log('Building BifrostQL.UI...');
  const build = spawnSync('dotnet', ['build', PROJECT_PATH, '-c', 'Debug', '--nologo', '-v', 'quiet'], {
    stdio: 'inherit',
  });
  if (build.status !== 0) {
    throw new Error(`dotnet build failed with exit code ${build.status}`);
  }

  const port = await findFreePort();
  const baseUrl = `http://localhost:${port}`;

  console.log(`Starting BifrostQL server on port ${port}...`);

  const serverProcess: ChildProcess = spawn('dotnet', [
    'run', '--no-build', '--project', PROJECT_PATH, '--',
    '--headless', '--port', port.toString(),
  ], {
    stdio: ['ignore', 'pipe', 'pipe'],
    // `dotnet run` execs the application as a CHILD, so serverProcess.pid is the
    // launcher's. Detaching makes the launcher a process-group leader that the
    // application inherits, so teardown can signal the GROUP and actually reach
    // the server. Without this the recorded PID cannot stop the real listener.
    detached: true,
  });

  // Log server output for debugging
  serverProcess.stdout?.on('data', (data: Buffer) => {
    const line = data.toString().trim();
    if (line) console.log(`[server] ${line}`);
  });

  serverProcess.stderr?.on('data', (data: Buffer) => {
    const line = data.toString().trim();
    if (line) console.error(`[server:err] ${line}`);
  });

  serverProcess.on('error', (err) => {
    console.error(`Failed to start server: ${err.message}`);
  });

  // The recorded PID doubles as the process-group id thanks to `detached: true`.
  const serverGroup = serverProcess.pid!;

  // Write PID and URL for teardown and config
  fs.writeFileSync(PID_FILE, serverGroup.toString());
  fs.writeFileSync(SERVER_URL_FILE, baseUrl);

  try {
    await waitForServer(baseUrl, STARTUP_TIMEOUT);
    console.log(`BifrostQL server ready at ${baseUrl}`);
  } catch (err) {
    // Same group-signalling escalation as teardown — a server that failed its
    // readiness check may still have bound the port.
    await stopProcessGroup(serverGroup, 'BifrostQL server (failed startup)');
    fs.rmSync(PID_FILE, { force: true });
    fs.rmSync(SERVER_URL_FILE, { force: true });
    throw err;
  }
}
