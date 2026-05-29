import { spawn, spawnSync, ChildProcess } from 'child_process';
import * as fs from 'fs';
import * as path from 'path';
import * as net from 'net';

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

export default async function globalSetup() {
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
    detached: false,
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

  // Write PID and URL for teardown and config
  fs.writeFileSync(PID_FILE, serverProcess.pid!.toString());
  fs.writeFileSync(SERVER_URL_FILE, baseUrl);

  try {
    await waitForServer(baseUrl, STARTUP_TIMEOUT);
    console.log(`BifrostQL server ready at ${baseUrl}`);
  } catch (err) {
    // Kill the server if it didn't start
    serverProcess.kill('SIGTERM');
    fs.unlinkSync(PID_FILE);
    fs.unlinkSync(SERVER_URL_FILE);
    throw err;
  }
}
