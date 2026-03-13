import * as fs from 'fs';
import * as path from 'path';

const PID_FILE = path.join(__dirname, '.server-pid');
const SERVER_URL_FILE = path.join(__dirname, '.server-url');

export default async function globalTeardown() {
  if (fs.existsSync(PID_FILE)) {
    const pid = parseInt(fs.readFileSync(PID_FILE, 'utf-8').trim());
    console.log(`Stopping BifrostQL server (PID ${pid})...`);
    try {
      process.kill(pid, 'SIGTERM');
    } catch {
      // process already exited
    }
    fs.unlinkSync(PID_FILE);
  }

  if (fs.existsSync(SERVER_URL_FILE)) {
    fs.unlinkSync(SERVER_URL_FILE);
  }
}
