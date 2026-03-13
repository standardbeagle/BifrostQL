CREATE TABLE deal_stages (
    deal_stage_id INTEGER PRIMARY KEY AUTOINCREMENT,
    name TEXT NOT NULL UNIQUE,
    display_order INTEGER NOT NULL DEFAULT 0,
    probability REAL NOT NULL DEFAULT 0.0,
    is_closed_won INTEGER NOT NULL DEFAULT 0,
    is_closed_lost INTEGER NOT NULL DEFAULT 0
);

CREATE TABLE companies (
    company_id INTEGER PRIMARY KEY AUTOINCREMENT,
    name TEXT NOT NULL,
    domain TEXT,
    industry TEXT,
    size TEXT,
    phone TEXT,
    address TEXT,
    city TEXT,
    state TEXT,
    country TEXT,
    parent_company_id INTEGER REFERENCES companies(company_id) ON DELETE SET NULL,
    created_at TEXT NOT NULL DEFAULT (datetime('now')),
    updated_at TEXT NOT NULL DEFAULT (datetime('now'))
);

CREATE TABLE contacts (
    contact_id INTEGER PRIMARY KEY AUTOINCREMENT,
    first_name TEXT NOT NULL,
    last_name TEXT NOT NULL,
    email TEXT UNIQUE,
    phone TEXT,
    title TEXT,
    company_id INTEGER REFERENCES companies(company_id) ON DELETE SET NULL,
    is_primary INTEGER NOT NULL DEFAULT 0,
    created_at TEXT NOT NULL DEFAULT (datetime('now')),
    updated_at TEXT NOT NULL DEFAULT (datetime('now'))
);

CREATE TABLE deals (
    deal_id INTEGER PRIMARY KEY AUTOINCREMENT,
    title TEXT NOT NULL,
    value REAL,
    currency TEXT NOT NULL DEFAULT 'USD',
    deal_stage_id INTEGER NOT NULL REFERENCES deal_stages(deal_stage_id),
    company_id INTEGER REFERENCES companies(company_id) ON DELETE SET NULL,
    contact_id INTEGER REFERENCES contacts(contact_id) ON DELETE SET NULL,
    expected_close_date TEXT,
    actual_close_date TEXT,
    probability REAL,
    created_at TEXT NOT NULL DEFAULT (datetime('now')),
    updated_at TEXT NOT NULL DEFAULT (datetime('now'))
);

CREATE TABLE activities (
    activity_id INTEGER PRIMARY KEY AUTOINCREMENT,
    type TEXT NOT NULL,
    subject TEXT NOT NULL,
    description TEXT,
    contact_id INTEGER REFERENCES contacts(contact_id) ON DELETE SET NULL,
    deal_id INTEGER REFERENCES deals(deal_id) ON DELETE SET NULL,
    company_id INTEGER REFERENCES companies(company_id) ON DELETE SET NULL,
    due_date TEXT,
    completed_at TEXT,
    created_at TEXT NOT NULL DEFAULT (datetime('now'))
);

CREATE TABLE notes (
    note_id INTEGER PRIMARY KEY AUTOINCREMENT,
    entity_type TEXT NOT NULL,
    entity_id INTEGER NOT NULL,
    content TEXT NOT NULL,
    created_by TEXT,
    created_at TEXT NOT NULL DEFAULT (datetime('now'))
);
