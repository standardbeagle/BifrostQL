import * as fs from 'fs';
import * as path from 'path';
import { stopProcessGroup } from './server-process';

const PID_FILE = path.join(__dirname, '.server-pid');
const SERVER_URL_FILE = path.join(__dirname, '.server-url');

export default async function globalTeardown() {
  if (fs.existsSync(PID_FILE)) {
    const pid = parseInt(fs.readFileSync(PID_FILE, 'utf-8').trim(), 10);
    if (Number.isInteger(pid) && pid > 0) {
      // globalSetup spawns detached, so this PID is the server's process-group
      // id. Signalling the group reaches the `bifrostui` application, which is a
      // CHILD of the `dotnet run` launcher — signalling the launcher PID alone
      // left the real listener alive whenever it could not forward the signal.
      await stopProcessGroup(pid, 'BifrostQL server');
    }
    fs.rmSync(PID_FILE, { force: true });
  }

  fs.rmSync(SERVER_URL_FILE, { force: true });
}
