-- SQLite schema for the BifrostQL chat example.
-- Both chat tables use a single-column INTEGER PRIMARY KEY — the chat module
-- requires an integer identity key because it orders by it (conversations
-- newest-first, message ties in creation order). No tenancy in this demo;
-- add tenant_id + `tenant-filter` metadata to both tables to demo isolation.
--
-- Create the database from the repo root with:
--   sqlite3 chat-demo.db < examples/chat/sample/chat-demo.sql

CREATE TABLE conversations (
    id    INTEGER PRIMARY KEY,
    title TEXT NULL
);

CREATE TABLE messages (
    id              INTEGER PRIMARY KEY,
    conversation_id INTEGER NOT NULL REFERENCES conversations(id),
    role            TEXT NOT NULL,
    content         TEXT NULL,
    created_at      DATETIME NULL
);

-- ---------------------------------------------------------------------------
-- Chat-connector demo tables (see docs guides/chat-connectors). Three
-- scenarios, one per connector type:
--
--   orders            chat-connector: explore   -> the model queries data
--   products          chat-connector: media     -> inline images in the chat
--   publish_schedule  chat-connector: plan      -> human-gated writes
-- ---------------------------------------------------------------------------

-- explore: the model can filter/sort/project this table (read-only, capped).
CREATE TABLE orders (
    id        INTEGER PRIMARY KEY,
    customer  TEXT NOT NULL,
    status    TEXT NOT NULL,
    total     REAL NOT NULL,
    placed_at DATETIME NOT NULL
);

INSERT INTO orders (customer, status, total, placed_at) VALUES
    ('Alma Reyes',    'shipped',   129.90, '2026-06-28 09:15:00'),
    ('Bo Lindqvist',  'pending',    54.00, '2026-07-02 14:40:00'),
    ('Cara Okafor',   'pending',   310.25, '2026-07-05 11:05:00'),
    ('Dev Anand',     'cancelled',  89.99, '2026-07-06 16:20:00'),
    ('Elin Berg',     'shipped',    19.50, '2026-07-08 08:55:00'),
    ('Faisal Khan',   'pending',   205.75, '2026-07-10 19:30:00');

-- media: image is a BLOB, so the media tool serves binary mode — the model
-- hands out bifrost-media://products/<id> references and the SPA fetches
-- them through the auth-gated GET /_chat/media/products/<id> route. The
-- caption column doubles as the images' alt text.
-- The seeds are tiny generated solid-color PNGs (12x12) so the demo works
-- without shipping binary files; replace with real product photos at will.
CREATE TABLE products (
    id      INTEGER PRIMARY KEY,
    name    TEXT NOT NULL,
    price   REAL NOT NULL,
    caption TEXT NULL,
    image   BLOB NULL
);

INSERT INTO products (name, price, caption, image) VALUES
    ('Signal Red Mug', 14.50, 'Signal Red Mug — solid red swatch',
     X'89504E470D0A1A0A0000000D494844520000000C0000000C0802000000D917CBB00000001349444154789C633861634410318C2A1A8C8A009635AE61108637650000000049454E44AE426082'),
    ('Forest Green Notebook', 9.90, 'Forest Green Notebook — solid green swatch',
     X'89504E470D0A1A0A0000000D494844520000000C0000000C0802000000D917CBB00000001349444154789C63B059104510318C2A1A8C8A0030F5AE618E71B70A0000000049454E44AE426082'),
    ('Deep Blue Bottle', 21.00, 'Deep Blue Bottle — solid blue swatch',
     X'89504E470D0A1A0A0000000D494844520000000C0000000C0802000000D917CBB00000001349444154789C63B049394110318C2A1A8C8A00583DCA81A92D91840000000049454E44AE426082');

-- plan: the model may PROPOSE inserts/updates on publish_schedule; nothing is
-- written until the user approves the proposal card in the SPA. blog_posts is
-- explore-only context the model schedules against.
CREATE TABLE blog_posts (
    id     INTEGER PRIMARY KEY,
    title  TEXT NOT NULL,
    author TEXT NOT NULL,
    status TEXT NOT NULL
);

INSERT INTO blog_posts (title, author, status) VALUES
    ('Launching the new catalog', 'Alma Reyes', 'draft'),
    ('Summer maintenance window', 'Bo Lindqvist', 'draft'),
    ('How we ship faster',        'Cara Okafor', 'published');

CREATE TABLE publish_schedule (
    id         INTEGER PRIMARY KEY,
    post_id    INTEGER NOT NULL REFERENCES blog_posts(id),
    publish_at DATETIME NOT NULL,
    status     TEXT NOT NULL
);
