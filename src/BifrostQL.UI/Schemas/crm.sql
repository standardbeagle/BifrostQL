CREATE TABLE companies (
    company_id INTEGER PRIMARY KEY AUTOINCREMENT,
    name TEXT NOT NULL,
    industry TEXT,
    website TEXT,
    parent_company_id INTEGER REFERENCES companies(company_id),
    created_at TEXT NOT NULL DEFAULT (datetime('now'))
);

CREATE TABLE contacts (
    contact_id INTEGER PRIMARY KEY AUTOINCREMENT,
    first_name TEXT NOT NULL,
    last_name TEXT NOT NULL,
    email TEXT UNIQUE,
    phone TEXT,
    company_id INTEGER REFERENCES companies(company_id),
    job_title TEXT,
    created_at TEXT NOT NULL DEFAULT (datetime('now'))
);

CREATE TABLE deal_stages (
    deal_stage_id INTEGER PRIMARY KEY AUTOINCREMENT,
    name TEXT NOT NULL UNIQUE,
    display_order INTEGER NOT NULL DEFAULT 0
);

CREATE TABLE deals (
    deal_id INTEGER PRIMARY KEY AUTOINCREMENT,
    title TEXT NOT NULL,
    value REAL,
    contact_id INTEGER REFERENCES contacts(contact_id),
    company_id INTEGER REFERENCES companies(company_id),
    deal_stage_id INTEGER NOT NULL REFERENCES deal_stages(deal_stage_id),
    expected_close_date TEXT,
    created_at TEXT NOT NULL DEFAULT (datetime('now'))
);

CREATE TABLE activities (
    activity_id INTEGER PRIMARY KEY AUTOINCREMENT,
    type TEXT NOT NULL,
    subject TEXT NOT NULL,
    description TEXT,
    contact_id INTEGER REFERENCES contacts(contact_id),
    deal_id INTEGER REFERENCES deals(deal_id),
    completed_at TEXT,
    created_at TEXT NOT NULL DEFAULT (datetime('now'))
);

CREATE TABLE notes (
    note_id INTEGER PRIMARY KEY AUTOINCREMENT,
    entity_type TEXT NOT NULL,
    entity_id INTEGER NOT NULL,
    body TEXT NOT NULL,
    created_at TEXT NOT NULL DEFAULT (datetime('now'))
);
