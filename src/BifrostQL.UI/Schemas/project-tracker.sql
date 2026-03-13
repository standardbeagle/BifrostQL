CREATE TABLE workspaces (
    workspace_id INTEGER PRIMARY KEY AUTOINCREMENT,
    name TEXT NOT NULL,
    description TEXT,
    created_at TEXT NOT NULL DEFAULT (datetime('now'))
);

CREATE TABLE members (
    member_id INTEGER PRIMARY KEY AUTOINCREMENT,
    workspace_id INTEGER NOT NULL REFERENCES workspaces(workspace_id),
    name TEXT NOT NULL,
    email TEXT NOT NULL,
    role TEXT NOT NULL DEFAULT 'member',
    avatar_url TEXT,
    created_at TEXT NOT NULL DEFAULT (datetime('now')),
    UNIQUE(workspace_id, email)
);

CREATE TABLE projects (
    project_id INTEGER PRIMARY KEY AUTOINCREMENT,
    workspace_id INTEGER NOT NULL REFERENCES workspaces(workspace_id),
    name TEXT NOT NULL,
    description TEXT,
    color TEXT NOT NULL DEFAULT '#4A90D9',
    status TEXT NOT NULL DEFAULT 'active',
    owner_id INTEGER REFERENCES members(member_id) ON DELETE SET NULL,
    due_date TEXT,
    created_at TEXT NOT NULL DEFAULT (datetime('now')),
    updated_at TEXT NOT NULL DEFAULT (datetime('now'))
);

CREATE TABLE sections (
    section_id INTEGER PRIMARY KEY AUTOINCREMENT,
    project_id INTEGER NOT NULL REFERENCES projects(project_id),
    name TEXT NOT NULL,
    sort_order INTEGER NOT NULL DEFAULT 0,
    created_at TEXT NOT NULL DEFAULT (datetime('now'))
);

CREATE TABLE tasks (
    task_id INTEGER PRIMARY KEY AUTOINCREMENT,
    project_id INTEGER NOT NULL REFERENCES projects(project_id),
    section_id INTEGER NOT NULL REFERENCES sections(section_id),
    title TEXT NOT NULL,
    description TEXT,
    status TEXT NOT NULL DEFAULT 'todo',
    priority TEXT NOT NULL DEFAULT 'none',
    assignee_id INTEGER REFERENCES members(member_id) ON DELETE SET NULL,
    parent_task_id INTEGER REFERENCES tasks(task_id) ON DELETE CASCADE,
    due_date TEXT,
    start_date TEXT,
    completed_at TEXT,
    sort_order INTEGER NOT NULL DEFAULT 0,
    created_at TEXT NOT NULL DEFAULT (datetime('now')),
    updated_at TEXT NOT NULL DEFAULT (datetime('now'))
);

CREATE TABLE labels (
    label_id INTEGER PRIMARY KEY AUTOINCREMENT,
    workspace_id INTEGER NOT NULL REFERENCES workspaces(workspace_id),
    name TEXT NOT NULL,
    color TEXT NOT NULL DEFAULT '#808080'
);

CREATE TABLE task_labels (
    task_label_id INTEGER PRIMARY KEY AUTOINCREMENT,
    task_id INTEGER NOT NULL REFERENCES tasks(task_id) ON DELETE CASCADE,
    label_id INTEGER NOT NULL REFERENCES labels(label_id) ON DELETE CASCADE,
    UNIQUE(task_id, label_id)
);

CREATE TABLE task_assignments (
    task_assignment_id INTEGER PRIMARY KEY AUTOINCREMENT,
    task_id INTEGER NOT NULL REFERENCES tasks(task_id) ON DELETE CASCADE,
    member_id INTEGER NOT NULL REFERENCES members(member_id) ON DELETE CASCADE,
    role TEXT NOT NULL DEFAULT 'responsible',
    assigned_at TEXT NOT NULL DEFAULT (datetime('now')),
    UNIQUE(task_id, member_id, role)
);
