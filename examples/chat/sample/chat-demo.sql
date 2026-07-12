-- SQLite schema for the BifrostQL chat example.
-- Both tables use a single-column INTEGER PRIMARY KEY — the chat module
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
