// Records the multi-protocol transcript page. The commands and output on screen
// are a verbatim capture of a real session against the running verification host
// (protocol-transcript.txt); only the reveal pacing is scripted, so the lines are
// legible at video speed.
async (page) => {
  const OUT = process.env.BIFROST_CAPTURE_DIR || '/tmp/bifrost-capture-protocols';
  const browser = page.context().browser();
  const ctx = await browser.newContext({
    viewport: { width: 1920, height: 1080 },
    deviceScaleFactor: 1,
    recordVideo: { dir: OUT, size: { width: 1920, height: 1080 } },
  });
  const p = await ctx.newPage();
  await p.goto('file://' + (process.env.BIFROST_TRANSCRIPT_PAGE
    || '/tmp/bifrost-capture/protocols.html'));
  await p.waitForFunction(() => window.__done === true, { timeout: 120000 });
  await p.waitForTimeout(2500);
  const ms = await p.evaluate(() => performance.now());
  await ctx.close();
  return JSON.stringify({ dir: OUT, elapsedMs: Math.round(ms) });
}
