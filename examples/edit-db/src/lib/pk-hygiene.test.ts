/**
 * Hygiene guard for `.claude/rules/composite-pk-compliance.md`.
 *
 * BifrostQL tables can have composite (multi-column) primary keys and composite
 * foreign keys. Taking the FIRST element of a key/FK column list silently drops
 * the rest and mis-targets rows — and it never fails loudly, so it survives
 * review and tests. This walks examples/edit-db/src and fails on the shapes that
 * have actually shipped bugs.
 *
 * Comment lines are ignored on purpose: prose that NAMES the anti-pattern (in a
 * doc comment explaining why a helper exists, or in a test explaining what it
 * prevents) is exactly the documentation this rule wants to encourage, and
 * flagging it would push authors to stop writing it.
 *
 * Adding an allowlist entry is a review decision, not a formality. Each one
 * carries a `why` that must state the invariant making the first element the
 * WHOLE answer — not merely a convenient one.
 */
import { describe, it, expect } from 'vitest';
import { readdirSync, readFileSync, statSync } from 'node:fs';
import { dirname, join, relative, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const THIS_FILE = fileURLToPath(import.meta.url);
const SRC_ROOT = resolve(dirname(THIS_FILE), '..'); // examples/edit-db/src

interface Rule {
    id: string;
    pattern: RegExp;
    /** Lines matching this are not instances of the rule at all. */
    exempt?: RegExp;
    guidance: string;
    /** file -> why the first element is the complete answer there. */
    allow: Record<string, string>;
}

const RULES: Rule[] = [
    {
        id: 'primaryKeys-index-zero',
        pattern: /primaryKeys\??\.?\[0\]/,
        guidance: 'Use rowIdOf / pkFilterFor / getPkTypes — never the first PK column alone.',
        allow: {},
    },
    {
        id: 'getPkTypes-index-zero',
        pattern: /getPkTypes\([^)]*\)\s*(\?\.)?\[0\]/,
        guidance: 'Row identity needs EVERY PK column. getPkTypes(t)[0] is only valid where a single-column key is already guaranteed.',
        allow: {
            'lib/query-builder.ts':
                'getPkType() is by definition "the first PK column\'s type" and says so; the flat-drill site is gated by canFlatFilterDrill, which requires exactly one destination column.',
            'lib/table-ref.ts':
                'Picks a DISPLAY label column when the table declares none. Not row identity — the value is never used to address a row.',
            'hooks/useDataTable.tsx':
                'Chooses a default SORT column. Not row identity; any sortable column is a valid answer.',
        },
    },
    {
        id: 'fk-column-index-zero',
        pattern: /\b(sourceColumnNames|destinationColumnNames|targetColumnNames|junctionSourceColumnNames|junctionTargetColumnNames)\??\.?\[0\]/,
        guidance: 'A composite FK has more than one column. Pair positionally (fkDestinationColumnFor) or declare the relationship unsupported — never take [0].',
        allow: {
            'hooks/useDataTable.tsx':
                'Anchor detection (is this column the FIRST source column of the join?) plus the anchor join\'s own meta; the composite multi-join branch is guarded by isComposite.',
            'lib/query-builder.ts':
                'Anchor detection in buildDataColumns, and the flat-drill FK column gated by canFlatFilterDrill (exactly one destination column).',
            'components/detail-panel.tsx':
                'Both sites are the non-composite arm of an explicit isComposite() ternary; the composite arm omits filterColumn entirely.',
            'lib/polymorphic.ts':
                'Matches a join by its first destination column when resolving a polymorphic child; the caller declines ambiguous matches rather than guessing.',
            'data-edit.tsx':
                'ParentField renders only for fkRole "anchor-single", which is by construction a single-column FK; composite FKs route to CompositeParentField.',
            'lib/m2m.ts':
                'The junction link column, which attachJunctionDetail already rejects unless there is exactly one. Row identity comes from m2mTargetIdentityColumns.',
        },
    },
    {
        // The precise shape of the shipped bug: FALLING BACK to the first FK column
        // when a lookup failed. Deliberately has NO allowlist — the per-file
        // allowlists above exist for legitimate anchor detection and guarded
        // non-composite branches, and a file being on one of those lists must not
        // also excuse this. There is no correct reason to default to column zero.
        id: 'fk-column-index-zero-fallback',
        pattern: /\?\?\s*[\w.]*(source|destination|target)ColumnNames\??\.?\[0\]/i,
        guidance: 'Falling back to the first FK column turns an unresolved pairing into a confident wrong answer. Return null and let the caller decline the behaviour (see fkDestinationColumnFor).',
        allow: {},
    },
    {
        id: 'hardcoded-id-identity',
        // A literal 'id' used as a fallback KEY. Display fallbacks (label columns)
        // are a different thing and are exempted below.
        pattern: /\?\?\s*\[?['"]id['"]\]?/,
        exempt: /label/i,
        guidance: 'There is no guarantee a table has a column called "id". Refuse to build the query instead of guessing a key column.',
        allow: {
            'hooks/useDataTable.tsx':
                'Last-resort default SORT column for a table with no columns at all. Not row identity.',
        },
    },
];

function walk(dir: string, files: string[] = []): string[] {
    for (const entry of readdirSync(dir)) {
        if (entry === 'node_modules' || entry.startsWith('.')) continue;
        const full = join(dir, entry);
        if (statSync(full).isDirectory()) walk(full, files);
        else if (/\.(ts|tsx)$/.test(entry)) files.push(full);
    }
    return files;
}

// Comment-only lines: line comments, block-comment open/close, and JSDoc continuations.
function isCommentLine(line: string): boolean {
    return /^\s*(\/\/|\/\*|\*\/|\*)/.test(line.trim().length === 0 ? 'x' : line);
}

const SOURCE_FILES = walk(SRC_ROOT).map((file) => ({
    rel: relative(SRC_ROOT, file).split('\\').join('/'),
    lines: readFileSync(file, 'utf8').split('\n'),
}));

describe('composite-PK hygiene', () => {
    for (const rule of RULES) {
        it(`no unreviewed \`${rule.id}\` under examples/edit-db/src`, () => {
            const offenders: string[] = [];
            for (const { rel, lines } of SOURCE_FILES) {
                // This file states every pattern literally, by necessity.
                if (rel === 'lib/pk-hygiene.test.ts') continue;
                if (rule.allow[rel]) continue;
                lines.forEach((line, i) => {
                    if (isCommentLine(line)) return;
                    if (rule.exempt?.test(line)) return;
                    if (rule.pattern.test(line)) offenders.push(`  ${rel}:${i + 1}\n    ${line.trim()}`);
                });
            }

            if (offenders.length > 0) {
                throw new Error(
                    `Found ${offenders.length} unreviewed use(s) of \`${rule.id}\`.\n` +
                    `${rule.guidance}\n` +
                    `If a site is genuinely correct, add its file to that rule's allow map in ` +
                    `pk-hygiene.test.ts WITH the invariant that makes the first element the whole ` +
                    `answer. Do not add one to silence a real bug.\n${offenders.join('\n')}`,
                );
            }
            expect(offenders).toEqual([]);
        });
    }

    it('keeps every allowlist entry pointed at a file that still exists', () => {
        // A stale entry silently re-opens a rule for a path that gets recreated later.
        const known = new Set(SOURCE_FILES.map((f) => f.rel));
        const stale: string[] = [];
        for (const rule of RULES) {
            for (const file of Object.keys(rule.allow)) {
                if (!known.has(file)) stale.push(`${rule.id} -> ${file}`);
            }
        }
        expect(stale).toEqual([]);
    });

    it('requires every allowlist entry to carry a reason', () => {
        const empty: string[] = [];
        for (const rule of RULES) {
            for (const [file, why] of Object.entries(rule.allow)) {
                if (why.trim().length < 20) empty.push(`${rule.id} -> ${file}`);
            }
        }
        expect(empty).toEqual([]);
    });
});
