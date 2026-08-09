// Records the BifrostQL UI running against a real SQLite database it builds
// through its own Quick Start flow, then the generated GraphQL API answering a
// real nested query in GraphiQL. Everything on screen is the live app; the only
// injected style hides the saved-connection list, which holds real customer
// hostnames.
async (page) => {
  const OUT = process.env.BIFROST_CAPTURE_DIR || '/tmp/bifrost-capture';
  const APP = 'http://localhost:5000/';
  const browser = page.context().browser();
  const ctx = await browser.newContext({
    viewport: { width: 1920, height: 1080 },
    deviceScaleFactor: 1,
    recordVideo: { dir: OUT, size: { width: 1920, height: 1080 } },
  });
  const p = await ctx.newPage();
  const log = [];
  const t0 = Date.now();
  // Record when each beat ENDS, relative to recording start, so captions can be
  // burned in at the right offsets without guessing.
  const beat = async (name, ms) => {
    await p.waitForTimeout(ms);
    log.push({ name, atMs: Date.now() - t0 });
  };

  // Privacy only: the welcome screen lists the operator's real saved servers.
  await p.addInitScript(() => {
    const css = document.createElement('style');
    css.textContent = `[class*="saved"], [class*="Saved"] { visibility: hidden !important; }`;
    document.addEventListener('DOMContentLoaded', () => document.head.appendChild(css));
  });

  await p.goto(APP, { waitUntil: 'networkidle' });
  await beat('welcome', 2200);

  await p.getByRole('button', { name: /Try It Now/i }).click();
  await beat('schema-picker', 2600);

  await p.getByRole('button', { name: /E-commerce/i }).click();
  await beat('schema-chosen', 1100);
  await p.locator('input[type=radio][value="full"]').click();
  await beat('full-dataset', 1100);
  await p.getByRole('button', { name: /^Launch$/ }).click();

  await p.getByRole('link', { name: /Products/ }).waitFor({ timeout: 60000 });
  await beat('explorer', 3200);

  // Products: the category foreign key resolved to its label, and per-row
  // related-record counts.
  await p.getByRole('link', { name: /Products/ }).click();
  await p.locator('tbody tr').first().waitFor({ timeout: 30000 });
  await beat('products-grid', 4000);

  // Drill from a product into the rows that reference it.
  const related = p.locator('a[href^="/reviews/from/products/"]').first();
  if (await related.count()) {
    await related.click();
    await beat('related-reviews', 3800);
  }

  // Orders: 800 rows, sortable and paged.
  await p.getByRole('link', { name: /Orders/ }).first().click();
  await p.locator('tbody tr').first().waitFor({ timeout: 30000 });
  await beat('orders-grid', 3000);

  // Schema-generated edit form: required markers and foreign-key pickers.
  await p.getByRole('button', { name: /^Add$/ }).click();
  await p.locator('[role=dialog]').waitFor({ timeout: 15000 });
  await beat('generated-form', 3200);
  await p.getByRole('button', { name: /^Cancel$/ }).click();
  await beat('form-closed', 1600);

  // GraphiQL: the generated API answering a real nested query. The query is
  // passed in the URL rather than typed — GraphiQL's editor auto-closes
  // brackets, so synthetic keystrokes produce a malformed document.
  const query = [
    '{',
    '  orders(limit: 3, sort: [order_id_asc]) {',
    '    total',
    '    data {',
    '      order_id',
    '      status',
    '      customers { first_name last_name }',
    '      order_items { data { quantity products { name } } }',
    '    }',
    '  }',
    '}',
  ].join('\n');
  await p.goto('http://localhost:5000/graphiql?query=' + encodeURIComponent(query),
    { waitUntil: 'networkidle' });
  await beat('graphiql-open', 3500);
  await p.getByRole('button', { name: /Execute query/i }).click();
  await beat('graphiql-result', 5500);

  await ctx.close();
  return JSON.stringify({ beats: log, dir: OUT });
}
