/**
 * Hygiene test: the composite-PK refactor on Q8LOiaZOHjkP eliminated every use of
 * `primaryKeys[0]` / `primaryKeys?.[0]` from examples/edit-db/src. This test guards
 * against regressions by walking the tree and failing if the pattern reappears.
 *
 * If you legitimately need the first primary key column, use `getPkTypes(table)[0]`,
 * `rowIdOf(...)`, or read it via a named helper — do NOT re-introduce index-zero
 * indexing on `primaryKeys`.
 */
import { describe, it, expect } from 'vitest';
import { readdirSync, readFileSync, statSync } from 'node:fs';
import { dirname, join, relative, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const THIS_FILE = fileURLToPath(import.meta.url);
const SRC_ROOT = resolve(dirname(THIS_FILE), '..'); // examples/edit-db/src
const PATTERN = /primaryKeys\??\.?\[0\]/;

// Allowlist: this test file itself mentions the pattern in the regex literal above.
const ALLOW = new Set<string>([
    'lib/pk-hygiene.test.ts',
]);

function walk(dir: string, files: string[] = []): string[] {
    for (const entry of readdirSync(dir)) {
        if (entry === 'node_modules' || entry.startsWith('.')) continue;
        const full = join(dir, entry);
        const stat = statSync(full);
        if (stat.isDirectory()) {
            walk(full, files);
        } else if (/\.(ts|tsx)$/.test(entry)) {
            files.push(full);
        }
    }
    return files;
}

describe('primaryKeys index-zero hygiene', () => {
    it('never uses `primaryKeys[0]` or `primaryKeys?.[0]` under examples/edit-db/src', () => {
        const offenders: { file: string; line: number; content: string }[] = [];

        for (const file of walk(SRC_ROOT)) {
            const rel = relative(SRC_ROOT, file);
            if (ALLOW.has(rel)) continue;

            const lines = readFileSync(file, 'utf8').split('\n');
            lines.forEach((line, i) => {
                if (PATTERN.test(line)) {
                    offenders.push({ file: rel, line: i + 1, content: line.trim() });
                }
            });
        }

        if (offenders.length > 0) {
            const summary = offenders
                .map((o) => `  ${o.file}:${o.line}\n    ${o.content}`)
                .join('\n');
            throw new Error(
                `Found ${offenders.length} use(s) of primaryKeys index-zero. Use a composite-aware helper (rowIdOf, pkFilterFor, getPkTypes) instead:\n${summary}`,
            );
        }

        expect(offenders).toEqual([]);
    });
});
